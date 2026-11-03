// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace PhotoLibrarian.Core.Services;

public sealed record PanoramaStitchProgress(string Stage, int Percent);

public sealed record PanoramaStitchResult(
    byte[] Pixels,
    int Width,
    int Height,
    string FirstSourcePath,
    string SecondSourcePath,
    int MatchCount,
    int InlierCount,
    double InlierRatio,
    double ExposureGain);

/// <summary>Builds and saves a local two-photo panorama without changing either source.</summary>
public static class PanoramaStitchingService
{
    public const uint MaximumInputDimension = 2400;
    public const int MaximumOutputPixels = 16_000_000;
    public const int MaximumInputFileBytes = 512 * 1024 * 1024;
    public const int MaximumXmpBytes = 60_000;

    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp" };

    public static bool IsSupported(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        SupportedExtensions.Contains(Path.GetExtension(path));

    public static async Task<PanoramaStitchResult> CreatePreviewAsync(
        string firstPath,
        string secondPath,
        IProgress<PanoramaStitchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondPath);
        if (string.Equals(Path.GetFullPath(firstPath), Path.GetFullPath(secondPath),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose two different photos.");
        ValidateInput(firstPath);
        ValidateInput(secondPath);

        progress?.Report(new("Reading first photo", 5));
        var first = await WindowsImagingCodec.DecodeAsync(
            firstPath, MaximumInputDimension, cancellationToken);
        progress?.Report(new("Reading second photo", 18));
        var second = await WindowsImagingCodec.DecodeAsync(
            secondPath, MaximumInputDimension, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await Task.Run(
            () => PanoramaStitcher.Stitch(
                first, second, firstPath, secondPath, progress, cancellationToken),
            cancellationToken);
        return result;
    }

    public static async Task<string> SaveAsync(
        PanoramaStitchResult panorama,
        string outputFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(panorama);
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
            var sourceName = Path.GetFileNameWithoutExtension(panorama.FirstSourcePath);
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
        xmp.SetProperty(namespaceName, "Method", "Managed feature matching, RANSAC homography, feather blending; baked");
        xmp.SetProperty(namespaceName, "SourceFile1", Path.GetFileName(panorama.FirstSourcePath));
        xmp.SetProperty(namespaceName, "SourceFile2", Path.GetFileName(panorama.SecondSourcePath));
        xmp.SetProperty(namespaceName, "FeatureMatches", panorama.MatchCount.ToString(CultureInfo.InvariantCulture));
        xmp.SetProperty(namespaceName, "HomographyInliers", panorama.InlierCount.ToString(CultureInfo.InvariantCulture));
        xmp.SetProperty(namespaceName, "InlierRatio", panorama.InlierRatio.ToString("F4", CultureInfo.InvariantCulture));
        xmp.SetProperty(namespaceName, "SecondPhotoExposureGain", panorama.ExposureGain.ToString("F4", CultureInfo.InvariantCulture));
        xmp.SetProperty(namespaceName, "PixelDimensions", $"{panorama.Width} x {panorama.Height}");
        return XmpCore.XmpMetaFactory.SerializeToString(
            xmp, new XmpCore.Options.SerializeOptions { OmitPacketWrapper = true });
    }
}
