using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using PhotoLibrarian.Core.Models;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace PhotoLibrarian.Core.Services;

public sealed record ImageExportFailure(string SourcePath, string Message);
public sealed record ImageExportProgress(int Completed, int Total, string FileName);
public sealed record ImageExportResult(
    IReadOnlyList<string> ExportedPaths,
    IReadOnlyList<ImageExportFailure> Failures,
    bool IsCancelled);

/// <summary>Exports first-frame JPEG copies without modifying sources or copying sidecars.</summary>
public static class ImageExportService
{
    // Copy portable descriptive metadata, not stale dimensions, face rectangles, thumbnails,
    // orientation or color profiles (pixels are oriented and converted to sRGB below).
    private static readonly string[] MetadataKeys =
    [
        "System.Title", "System.Comment", "System.Keywords",
        "System.Rating", "System.SimpleRating", "System.Photo.DateTaken",
        "System.Author", "System.Copyright",
        "System.Photo.CameraManufacturer", "System.Photo.CameraModel"
    ];

    public static async Task<ImageExportResult> ExportAsync(
        IReadOnlyList<string> sourcePaths,
        string outputFolder,
        ImageExportOptions options,
        IProgress<ImageExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        options.Validate();
        if (!Path.IsPathFullyQualified(outputFolder) || !Directory.Exists(outputFolder))
            throw new DirectoryNotFoundException("Choose an existing output folder.");

        var exported = new List<string>();
        var failures = new List<ImageExportFailure>();
        for (var index = 0; index < sourcePaths.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
                return new(exported, failures, true);

            var sourcePath = sourcePaths[index];
            try
            {
                exported.Add(await ExportOneAsync(sourcePath, outputFolder, options, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new(exported, failures, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                COMException or ArgumentException or NotSupportedException or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine($"[EXPORT] {sourcePath}: {ex}");
                failures.Add(new(sourcePath, ex.Message));
            }
            progress?.Report(new(index + 1, sourcePaths.Count, Path.GetFileName(sourcePath)));
        }

        return new(exported, failures, false);
    }

    private static async Task<string> ExportOneAsync(
        string sourcePath, string outputFolder, ImageExportOptions options, CancellationToken token)
    {
        // Encode to a private staging file, then atomically publish with a non-overwriting move.
        // Cancellation or an encoder failure must never leave a partial JPEG at its final name.
        var stagingPath = Path.Combine(outputFolder, $".photolibrarian-export-{Guid.NewGuid():N}.tmp");
        var stagingCreated = false;
        try
        {
            await using (var source = File.OpenRead(sourcePath))
            await using (var destination = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                stagingCreated = true;
                var decoder = await BitmapDecoder.CreateAsync(source.AsRandomAccessStream()).AsTask(token);
                var size = options.GetDimensions(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
                var swapsAxes = decoder.OrientedPixelWidth != decoder.PixelWidth;
                // For square images, swapping the axes makes no difference to the target dimensions.
                var transform = new BitmapTransform
                {
                    ScaledWidth = swapsAxes ? size.Height : size.Width,
                    ScaledHeight = swapsAxes ? size.Width : size.Height,
                    InterpolationMode = BitmapInterpolationMode.Fant
                };
                var data = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb).AsTask(token);
                var pixels = data.DetachPixelData();
                // JPEG has no alpha channel; flatten transparent pixels onto white.
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    var white = 255 - pixels[i + 3];
                    pixels[i] = (byte)Math.Min(255, pixels[i] + white);
                    pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] + white);
                    pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] + white);
                    pixels[i + 3] = 255;
                }

                var encoder = await BitmapEncoder.CreateAsync(
                    BitmapEncoder.JpegEncoderId, destination.AsRandomAccessStream(),
                    new BitmapPropertySet
                    {
                        ["ImageQuality"] = new BitmapTypedValue(options.Quality / 100f, PropertyType.Single)
                    }).AsTask(token);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    size.Width, size.Height, 96, 96, pixels);

                if (!options.StripMetadata)
                {
                    var properties = await decoder.BitmapProperties.GetPropertiesAsync(MetadataKeys).AsTask(token);
                    var metadata = new BitmapPropertySet();
                    foreach (var property in properties)
                    {
                        if (property.Value.Value is not null)
                            metadata.Add(property.Key, property.Value);
                    }
                    if (metadata.Count > 0)
                        await encoder.BitmapProperties.SetPropertiesAsync(metadata).AsTask(token);
                }

                await encoder.FlushAsync().AsTask(token);
                await destination.FlushAsync(token);
            }

            token.ThrowIfCancellationRequested();
            var stem = Path.GetFileNameWithoutExtension(sourcePath);
            for (var number = 1; ; number++)
            {
                token.ThrowIfCancellationRequested();
                var suffix = number == 1 ? " - Resized" : $" - Resized ({number})";
                var outputPath = Path.Combine(outputFolder, $"{stem}{suffix}.jpg");
                try
                {
                    File.Move(stagingPath, outputPath, overwrite: false);
                    return outputPath;
                }
                catch (IOException) when (File.Exists(outputPath) || Directory.Exists(outputPath))
                {
                    // Another export or an existing copy owns this name.
                }
            }
        }
        finally
        {
            if (stagingCreated && File.Exists(stagingPath))
                File.Delete(stagingPath);
        }
    }
}
