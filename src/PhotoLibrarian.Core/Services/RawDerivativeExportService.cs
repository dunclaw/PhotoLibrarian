// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices.WindowsRuntime;
using PhotoLibrarian.Core.Models;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace PhotoLibrarian.Core.Services;

public enum RawDerivativeFormat { Png, Jpeg }

public sealed record RawDerivativeResult(string FilePath, int Width, int Height);

/// <summary>Publishes one opaque, source-resolution, 8-bit sRGB derivative. Never rewrites RAW.</summary>
public static class RawDerivativeExportService
{
    public const int JpegQuality = 95;

    public static Task<RawDerivativeResult> ExportAsync(string sourcePath, string outputFolder,
        RawDerivativeFormat format, EditParameters parameters, CancellationToken cancellationToken = default) =>
        ExportAsync(sourcePath, outputFolder, format, parameters, DevelopAsync, cancellationToken);

    public static async Task<RawDerivativeResult> ExportAsync(string sourcePath, string outputFolder,
        RawDerivativeFormat format, EditParameters parameters,
        Func<string, EditParameters, CancellationToken, Task<RawSdrImage>> render,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!RawToneMappingService.IsRaw(sourcePath))
            throw new NotSupportedException("Derivative development requires a RAW original.");
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format), "Choose SDR PNG or JPEG.");
        if (!Path.IsPathFullyQualified(outputFolder) || !Directory.Exists(outputFolder))
            throw new DirectoryNotFoundException("Choose an existing output folder.");
        var snapshot = parameters.Clone();
        RawToneMappingService.ValidateRecipe(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        // Both files are private until the completed container is atomically renamed.
        var encoded = Path.Combine(outputFolder, $".photolibrarian-raw-{Guid.NewGuid():N}.tmp");
        var tagged = Path.Combine(outputFolder, $".photolibrarian-raw-{Guid.NewGuid():N}.tmp");
        var encodedCreated = false;
        var taggedCreated = false;
        try
        {
            var image = await render(sourcePath, snapshot.Clone(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested(); // Even a non-cooperative renderer cannot publish.
            await Task.Run(async () =>
            {
                var pixels = image.ToPreviewBgra8(cancellationToken);
                await using (var file = new FileStream(encoded, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    encodedCreated = true;
                    await EncodeAsync(file, image, pixels, format, cancellationToken);
                }
                var xmp = SdrDerivativeMetadata.CreateXmp(sourcePath, snapshot, image);
                await using (var input = File.OpenRead(encoded))
                await using (var output = new FileStream(tagged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    taggedCreated = true;
                    await SdrDerivativeMetadata.TagAsync(input, output, format, xmp, cancellationToken);
                }
                // Cleanup can fail too. Finish it before the publish commit, never afterwards.
                File.Delete(encoded);
                encodedCreated = false;
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            // Retain the exact snapshot beside the source, including dormant raster edits.
            // A recipe write failure means the derivative is not published.
            await ImageEditService.SaveRawPreviewRecipeAsync(sourcePath, snapshot, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var path = await Task.Run(
                () => Publish(tagged, sourcePath, outputFolder, format, cancellationToken), cancellationToken);
            taggedCreated = false;
            return new(path, image.Width, image.Height);
        }
        finally
        {
            try
            {
                if (encodedCreated && File.Exists(encoded)) File.Delete(encoded);
            }
            finally
            {
                if (taggedCreated && File.Exists(tagged)) File.Delete(tagged);
            }
        }
    }

    private static async Task<RawSdrImage> DevelopAsync(string path, EditParameters parameters, CancellationToken token)
    {
        var source = await RawDevelopmentService.DecodeAsync(path, token);
        return await RawToneMappingService.RenderAsync(source, parameters, token);
    }

    internal static async Task EncodeAsync(FileStream file, RawSdrImage image, byte[] pixels,
        RawDerivativeFormat format, CancellationToken token)
    {
        var options = new BitmapPropertySet();
        if (format == RawDerivativeFormat.Jpeg)
        {
            options["ImageQuality"] = new BitmapTypedValue(JpegQuality / 100f, PropertyType.Single);
            // WICJpegYCrCbSubsampling444: avoid chroma subsampling loss at colored edges.
            options["JpegYCrCbSubsampling"] = new BitmapTypedValue((byte)3, PropertyType.UInt8);
        }
        var encoder = await BitmapEncoder.CreateAsync(
            format == RawDerivativeFormat.Png ? BitmapEncoder.PngEncoderId : BitmapEncoder.JpegEncoderId,
            file.AsRandomAccessStream(), options).AsTask(token);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)image.Width, (uint)image.Height, 96, 96, pixels);
        await encoder.FlushAsync().AsTask(token);
        await file.FlushAsync(token);
        file.Flush(flushToDisk: true);
    }

    private static string Publish(string stagingPath, string sourcePath, string folder,
        RawDerivativeFormat format, CancellationToken token)
    {
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = format == RawDerivativeFormat.Png ? ".png" : ".jpg";
        for (var number = 1; ; number++)
        {
            token.ThrowIfCancellationRequested();
            var suffix = number == 1 ? " - Developed" : $" - Developed ({number})";
            var candidateStem = Path.Combine(folder, stem + suffix);
            // The editor reads same-stem sidecars. Never alias someone else's recipe/metadata.
            if (Directory.EnumerateFileSystemEntries(folder, Path.GetFileName(candidateStem) + ".*").Any())
                continue;
            var path = candidateStem + extension;
            try
            {
                File.Move(stagingPath, path, overwrite: false);
                return path; // Commit point: later cancellation must not report this as cancelled.
            }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path))
            {
                // Concurrent same-format exports own this name; retry without overwriting.
            }
        }
    }
}
