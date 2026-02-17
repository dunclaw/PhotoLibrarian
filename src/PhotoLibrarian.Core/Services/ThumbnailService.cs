using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// DEPRECATED: Legacy thumbnail service - use WindowsThumbnailService instead.
/// This service is kept for backward compatibility and fallback scenarios only.
/// WindowsThumbnailService leverages Windows thumbnail cache for better performance.
/// </summary>
public sealed class ThumbnailService
{
    // Kept for dependency injection compatibility
    [Obsolete("ThumbnailRepository no longer used - Windows thumbnail cache is used instead")]
    public ThumbnailService(ThumbnailRepository? thumbnailRepo = null)
    {
        // No-op constructor for DI compatibility
    }

    /// <summary>
    /// OBSOLETE: Use WindowsThumbnailService.GetThumbnailStreamAsync() instead.
    /// This method is kept for backward compatibility but should not be used.
    /// </summary>
    [Obsolete("Use WindowsThumbnailService.GetThumbnailStreamAsync() instead")]
    public async Task<byte[]?> GetOrCreateThumbnailAsync(long imageId, string filePath, ThumbnailSize size)
    {
        // Fallback: generate thumbnail pixels and encode to JPEG
        var result = await GenerateThumbnailPixelsAsync(filePath, (int)size);
        if (!result.HasValue)
            return null;
            
        return await EncodePixelsToJpegAsync(result.Value.pixels, result.Value.width, result.Value.height);
    }
    
    private static async Task<byte[]?> EncodePixelsToJpegAsync(byte[] pixels, int width, int height)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var outStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                var encoder = Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                    Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId,
                    outStream).GetAwaiter().GetResult();

                encoder.SetPixelData(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    (uint)width,
                    (uint)height,
                    96, 96,
                    pixels);

                encoder.FlushAsync().GetAwaiter().GetResult();

                outStream.Seek(0);
                var bytes = new byte[outStream.Size];
                var buffer = bytes.AsBuffer();
                outStream.ReadAsync(buffer, (uint)outStream.Size, Windows.Storage.Streams.InputStreamOptions.None)
                    .GetAwaiter().GetResult();
                    
                return bytes;
            }
            catch
            {
                return null;
            }
        });
    }

    /// <summary>
    /// Generates a JPEG thumbnail for the given image file and returns raw BGRA8 pixel data.
    /// This can be passed directly to WriteableBitmap on the UI thread without decoding.
    /// </summary>
    public static async Task<(byte[] pixels, int width, int height)?> GenerateThumbnailPixelsAsync(string filePath, int maxDimension)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // TEMPORARY: Skip EXIF thumbnail extraction to test WIC performance
            // TODO: Re-enable once WIC performance is acceptable

            // Decode to raw BGRA8 pixels on background thread
            return await Task.Run(() =>
            {
                var wicStart = sw.ElapsedMilliseconds;
                try
                {
                    using var stream = File.OpenRead(filePath);
                    
                    var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(
                        stream.AsRandomAccessStream()).GetAwaiter().GetResult();

                    // Use OrientedPixelWidth/Height which accounts for EXIF orientation
                    var orientedWidth = decoder.OrientedPixelWidth;
                    var orientedHeight = decoder.OrientedPixelHeight;

                    double scale = Math.Min((double)maxDimension / orientedWidth, (double)maxDimension / orientedHeight);
                    scale = Math.Min(scale, 1.0);

                    uint newWidth = Math.Max(1, (uint)(orientedWidth * scale));
                    uint newHeight = Math.Max(1, (uint)(orientedHeight * scale));

                    var transform = new Windows.Graphics.Imaging.BitmapTransform
                    {
                        ScaledWidth = newWidth,
                        ScaledHeight = newHeight,
                        InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant
                    };

                    var pixelData = decoder.GetPixelDataAsync(
                        Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                        Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                        transform,
                        Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                        Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage
                    ).GetAwaiter().GetResult();

                    var pixels = pixelData.DetachPixelData();
                    
                    var wicTime = sw.ElapsedMilliseconds - wicStart;
                    Core.Diagnostics.DebugLog.WriteLine($"    WIC decode to pixels: {Path.GetFileName(filePath)} in {wicTime}ms ({pixels.Length} bytes, {newWidth}x{newHeight})");
                    
                    return (pixels, (int)newWidth, (int)newHeight);
                }
                catch (Exception ex)
                {
                    Core.Diagnostics.DebugLog.WriteLine($"    WIC decode FAILED: {Path.GetFileName(filePath)} - {ex.Message}");
                    return ((byte[] pixels, int width, int height)?)null;
                }
            });
        }
        catch
        {
            return null;
        }
    }
    
    /// <summary>
    /// Legacy method for tests/benchmarks - generates JPEG thumbnail bytes.
    /// </summary>
    public static async Task<byte[]?> GenerateThumbnailAsync(string filePath, int maxDimension)
    {
        var result = await GenerateThumbnailPixelsAsync(filePath, maxDimension);
        if (!result.HasValue) return null;
        return await EncodePixelsToJpegAsync(result.Value.pixels, result.Value.width, result.Value.height);
    }

    /// <summary>
    /// Extracts the embedded EXIF thumbnail from the file using offset/length from metadata.
    /// </summary>
    private static byte[]? TryExtractExifThumbnail(string filePath)
    {
        try
        {
            var directories = MetadataExtractor.ImageMetadataReader.ReadMetadata(filePath);
            var thumbDir = directories
                .OfType<MetadataExtractor.Formats.Exif.ExifThumbnailDirectory>()
                .FirstOrDefault();

            if (thumbDir is null) return null;

            var lengthObj = thumbDir.GetObject(MetadataExtractor.Formats.Exif.ExifThumbnailDirectory.TagThumbnailLength);
            if (lengthObj is not int length || length <= 0)
                return null;

            var offset = thumbDir.AdjustedThumbnailOffset;
            if (offset is null or < 0) return null;

            using var fs = File.OpenRead(filePath);
            fs.Seek(offset.Value, SeekOrigin.Begin);
            var thumbnail = new byte[length];
            int bytesRead = fs.Read(thumbnail, 0, length);
            return bytesRead == length ? thumbnail : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes and resizes an image using WIC (async, no blocking calls).
    /// </summary>
    private static async Task<byte[]?> DecodeAndResizeThumbnailAsync(string filePath, int maxDimension)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(
                stream.AsRandomAccessStream());

            var originalWidth = decoder.PixelWidth;
            var originalHeight = decoder.PixelHeight;

            double scale = Math.Min((double)maxDimension / originalWidth, (double)maxDimension / originalHeight);
            scale = Math.Min(scale, 1.0); // Don't upscale

            uint newWidth = (uint)(originalWidth * scale);
            uint newHeight = (uint)(originalHeight * scale);

            var transform = new Windows.Graphics.Imaging.BitmapTransform
            {
                ScaledWidth = newWidth,
                ScaledHeight = newHeight,
                InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant
            };

            var pixelData = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb
            );

            var pixels = pixelData.DetachPixelData();

            // Encode to JPEG
            using var outStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId,
                outStream);

            encoder.SetPixelData(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                newWidth, newHeight, 96, 96, pixels);

            encoder.BitmapTransform.InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant;
            await encoder.FlushAsync();

            using var ms = new MemoryStream();
            outStream.Seek(0);
            outStream.AsStreamForRead().CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// OBSOLETE: Batch thumbnail generation removed - Windows thumbnail cache handles this efficiently.
    /// This method is kept for backward compatibility but does nothing.
    /// </summary>
    [Obsolete("Batch thumbnail generation removed - Windows thumbnail cache is used instead")]
    public Task GenerateBatchAsync(
        IEnumerable<(long imageId, string filePath)> images,
        ThumbnailSize size,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        // No-op - Windows thumbnail cache handles pre-generation automatically
        progress?.Report(images.Count());
        return Task.CompletedTask;
    }
}
