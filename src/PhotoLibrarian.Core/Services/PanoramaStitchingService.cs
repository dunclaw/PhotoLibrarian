// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace PhotoLibrarian.Core.Services;

public sealed record PanoramaStitchProgress(string Stage, int Percent);

public sealed record PanoramaSourcePlacement(
    int DecodedWidth,
    int DecodedHeight,
    IReadOnlyList<double> Transform,
    IReadOnlyList<double> ExposureGains);

public sealed record PanoramaStitchResult(
    byte[] Pixels,
    int Width,
    int Height,
    IReadOnlyList<string> SourcePaths,
    int MatchCount,
    int InlierCount,
    double InlierRatio,
    IReadOnlyList<double> ExposureGains,
    double OutputScale,
    double RetainedArea,
    IReadOnlyList<PanoramaSourcePlacement> SourcePlacements);

/// <summary>Builds and saves a local multi-photo panorama without changing its sources.</summary>
public static class PanoramaStitchingService
{
    public const uint MaximumInputDimension = 2400;
    public const int MaximumSourceCount = 64;
    public const int MaximumDecodedPixels = 48_000_000;
    public const int MaximumOutputPixels = 16_000_000;
    public const int MaximumOutputDimension = 32_000;
    public const int MaximumInputFileBytes = 512 * 1024 * 1024;
    public const int MaximumXmpBytes = 60_000;

    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp" };

    public static bool IsSupported(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        SupportedExtensions.Contains(Path.GetExtension(path));

    public static async Task<PanoramaStitchResult> CreatePreviewAsync(
        IReadOnlyList<string> sourcePaths,
        IProgress<PanoramaStitchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count < 2 || sourcePaths.Count > MaximumSourceCount)
            throw new ArgumentException($"Select 2 to {MaximumSourceCount} overlapping photos.", nameof(sourcePaths));
        var paths = sourcePaths.Select(Path.GetFullPath).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new ArgumentException("Each selected photo must be different.", nameof(sourcePaths));
        foreach (var path in paths)
            ValidateInput(path);

        // A per-photo square bound also bounds the total decoded working set, including portrait inputs.
        var dimension = (uint)Math.Min(
            MaximumInputDimension, Math.Floor(Math.Sqrt((double)MaximumDecodedPixels / paths.Length)));
        return await Task.Run(async () =>
        {
            var images = new List<WindowsImagingCodec.DecodedImage>(paths.Length);
            for (var index = 0; index < paths.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new($"Reading photo {index + 1} of {paths.Length}", index * 15 / paths.Length));
                images.Add(await WindowsImagingCodec.DecodeAsync(paths[index], dimension, cancellationToken));
            }
            return PanoramaStitcher.Stitch(images, paths, progress, cancellationToken);
        }, cancellationToken);
    }

    public static async Task<string> SaveAsync(
        PanoramaStitchResult panorama,
        string outputFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(panorama);
        if (panorama.Width < 1 || panorama.Height < 1 ||
            panorama.Width > MaximumOutputDimension || panorama.Height > MaximumOutputDimension ||
            (long)panorama.Width * panorama.Height > MaximumOutputPixels ||
            panorama.Pixels.LongLength != (long)panorama.Width * panorama.Height * 4 ||
            panorama.SourcePaths.Count < 2 || panorama.SourcePaths.Count > MaximumSourceCount ||
            panorama.ExposureGains.Count != panorama.SourcePaths.Count ||
            panorama.SourcePlacements.Count != panorama.SourcePaths.Count)
            throw new InvalidDataException("The panorama pixels or source provenance are invalid.");
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        if (!Path.IsPathFullyQualified(outputFolder))
            throw new ArgumentException("Choose a fully qualified output folder.", nameof(outputFolder));
        if (!Directory.Exists(outputFolder))
            throw new DirectoryNotFoundException("Choose an existing output folder.");
        var fullOutputFolder = Path.GetFullPath(outputFolder);

        cancellationToken.ThrowIfCancellationRequested();
        var stagingPath = Path.Combine(fullOutputFolder, $".photolibrarian-panorama-{Guid.NewGuid():N}.tmp");
        var stagingCreated = false;
        try
        {
            var xmp = CreateProvenanceXmp(panorama);
            var xmpBytes = Encoding.UTF8.GetBytes(xmp);
            if (xmpBytes.Length > MaximumXmpBytes)
                throw new InvalidDataException("Panorama provenance is too large to embed in JPEG.");

            var pixels = await EncodeJpegAsync(panorama, cancellationToken);
            await using (var stream = new FileStream(
                stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stagingCreated = true;
                await WriteJpegWithXmpAsync(stream, pixels, xmpBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var sourceName = Path.GetFileNameWithoutExtension(panorama.SourcePaths[0]);
            var safeStem = string.Concat(sourceName.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(safeStem))
                safeStem = "Photo";
            for (var number = 1; ; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var suffix = number == 1 ? " - Panorama" : $" - Panorama ({number})";
                var outputPath = Path.Combine(fullOutputFolder, safeStem + suffix + ".jpg");
                if (File.Exists(Path.ChangeExtension(outputPath, ".xmp")))
                    continue;
                try
                {
                    File.Move(stagingPath, outputPath, overwrite: false);
                    stagingCreated = false;
                    return outputPath;
                }
                catch (IOException) when (File.Exists(outputPath) || Directory.Exists(outputPath))
                {
                    // A concurrent save owns this name; retry without replacing it.
                }
            }
        }
        finally
        {
            if (stagingCreated && File.Exists(stagingPath))
                File.Delete(stagingPath);
        }
    }

    private static void ValidateInput(string path)
    {
        if (!IsSupported(path))
            throw new NotSupportedException(
                $"Panorama stitching supports JPEG, PNG, TIFF, and BMP photos; {Path.GetExtension(path)} is not supported. RAW and video files are not supported.");
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("A selected photo could not be found.", path);
        if (info.Length <= 0 || info.Length > MaximumInputFileBytes)
            throw new InvalidDataException("Each photo must be between 1 byte and 512 MB.");
    }

    private static async Task<byte[]> EncodeJpegAsync(
        PanoramaStitchResult panorama,
        CancellationToken cancellationToken)
    {
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var options = new BitmapPropertySet
        {
            ["ImageQuality"] = new BitmapTypedValue(0.94f, PropertyType.Single)
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options)
            .AsTask(cancellationToken);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)panorama.Width,
            (uint)panorama.Height,
            96,
            96,
            panorama.Pixels);
        await encoder.FlushAsync().AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        stream.Seek(0);
        using var output = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(output, cancellationToken);
        return output.ToArray();
    }

    private static async Task WriteJpegWithXmpAsync(
        Stream output,
        byte[] jpeg,
        byte[] xmp,
        CancellationToken cancellationToken)
    {
        if (jpeg.Length < 2 || jpeg[0] != 0xff || jpeg[1] != 0xd8)
            throw new InvalidDataException("The JPEG encoder returned an invalid image.");
        const string xmpHeader = "http://ns.adobe.com/xap/1.0/\0";
        var header = Encoding.ASCII.GetBytes(xmpHeader);
        var dataLength = checked(header.Length + xmp.Length);
        if (dataLength > 65_533)
            throw new InvalidDataException("Panorama provenance is too large to embed in JPEG.");

        await output.WriteAsync(jpeg.AsMemory(0, 2), cancellationToken);
        await output.WriteAsync(new byte[]
        {
            0xff, 0xe1, (byte)((dataLength + 2) >> 8), (byte)(dataLength + 2)
        }, cancellationToken);
        await output.WriteAsync(header, cancellationToken);
        await output.WriteAsync(xmp, cancellationToken);
        await output.WriteAsync(jpeg.AsMemory(2), cancellationToken);
    }

    private static string CreateProvenanceXmp(PanoramaStitchResult panorama)
    {
        const string dc = "http://purl.org/dc/elements/1.1/";
        const string namespaceName = "https://photolibrarian.app/ns/panorama/1.0/";
        XmpCore.XmpMetaFactory.SchemaRegistry.RegisterNamespace(namespaceName, "plpan");
        var xmp = XmpCore.XmpMetaFactory.Create();
        xmp.SetProperty(dc, "format", "image/jpeg");
        xmp.SetProperty(namespaceName, "CreatedBy", "PhotoLibrarian");
        xmp.SetProperty(namespaceName, "Method", "Managed overlap graph, cycle-checked projective homographies, linear-light feather blending, rectangular crop; baked; source edit recipes not applied");
        xmp.SetProperty(namespaceName, "SourceCount", panorama.SourcePaths.Count.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < panorama.SourcePaths.Count; index++)
        {
            xmp.SetProperty(namespaceName, $"SourceFile{index + 1}", Path.GetFileName(panorama.SourcePaths[index]));
            xmp.SetProperty(namespaceName, $"ExposureGain{index + 1}",
                panorama.ExposureGains[index].ToString("F4", CultureInfo.InvariantCulture));
            var placement = panorama.SourcePlacements[index];
            xmp.SetProperty(namespaceName, $"DecodedDimensions{index + 1}",
                $"{placement.DecodedWidth} x {placement.DecodedHeight}");
            xmp.SetProperty(namespaceName, $"NormalizedSourceToOutput{index + 1}",
                string.Join(" ", placement.Transform.Select(value => value.ToString("G10", CultureInfo.InvariantCulture))));
            xmp.SetProperty(namespaceName, $"BgrExposureGains{index + 1}",
                string.Join(" ", placement.ExposureGains.Select(value => value.ToString("G6", CultureInfo.InvariantCulture))));
        }
        xmp.SetProperty(namespaceName, "FeatureMatches", panorama.MatchCount.ToString(CultureInfo.InvariantCulture));
        xmp.SetProperty(namespaceName, "HomographyInliers", panorama.InlierCount.ToString(CultureInfo.InvariantCulture));
        xmp.SetProperty(namespaceName, "InlierRatio", panorama.InlierRatio.ToString("F4", CultureInfo.InvariantCulture));
        xmp.SetProperty(namespaceName, "OutputScale", panorama.OutputScale.ToString("F4", CultureInfo.InvariantCulture));
        xmp.SetProperty(namespaceName, "RetainedArea", panorama.RetainedArea.ToString("F4", CultureInfo.InvariantCulture));
        xmp.SetProperty(namespaceName, "PixelDimensions", $"{panorama.Width} x {panorama.Height}");
        return XmpCore.XmpMetaFactory.SerializeToString(
            xmp, new XmpCore.Options.SerializeOptions { OmitPacketWrapper = true });
    }
}
