using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Generates and caches image thumbnails using Windows Imaging Component (WIC)
/// via System.Drawing or WIC interop. Falls back to managed decode if native unavailable.
/// </summary>
public sealed class ThumbnailService
{
    private readonly ThumbnailRepository _thumbnailRepo;

    // Limit concurrent decode operations to avoid thread pool starvation
    // Increased to allow more parallel decoding for better UI responsiveness
    private static readonly SemaphoreSlim s_decodeSemaphore = new(Math.Max(4, Environment.ProcessorCount));

    public ThumbnailService(ThumbnailRepository thumbnailRepo)
    {
        _thumbnailRepo = thumbnailRepo;
    }

    /// <summary>
    /// Gets a cached thumbnail, or generates and caches one if missing.
    /// </summary>
    public async Task<byte[]?> GetOrCreateThumbnailAsync(long imageId, string filePath, ThumbnailSize size)
    {
        // Try cache first
        var cached = await _thumbnailRepo.GetThumbnailAsync(imageId, size);
        if (cached is not null && cached.Length > 100) // Validate cached data has reasonable size
            return cached;

        // Generate thumbnail
        var data = await GenerateThumbnailAsync(filePath, (int)size);
        if (data is null || data.Length < 100)
            return null;

        // Cache it (only if valid)
        try
        {
            await _thumbnailRepo.SaveThumbnailAsync(imageId, size, data);
        }
        catch
        {
            // If caching fails, still return the generated thumbnail
        }
        return data;
    }

    /// <summary>
    /// Generates a JPEG thumbnail for the given image file.
    /// Uses embedded EXIF thumbnail when available for speed, otherwise decodes and resizes.
    /// </summary>
    public static async Task<byte[]?> GenerateThumbnailAsync(string filePath, int maxDimension)
    {
        try
        {
            // Try to extract embedded EXIF thumbnail first (very fast, no full decode)
            var exifThumb = await Task.Run(() => TryExtractExifThumbnail(filePath));
            if (exifThumb is not null && exifThumb.Length > 0)
                return exifThumb;

            // Fall back to decode + resize using WIC via BitmapDecoder (with timeout)
            await s_decodeSemaphore.WaitAsync();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var decodeTask = DecodeAndResizeThumbnailAsync(filePath, maxDimension);
                var completed = await Task.WhenAny(decodeTask, Task.Delay(Timeout.Infinite, cts.Token));
                return completed == decodeTask ? await decodeTask : null;
            }
            finally
            {
                s_decodeSemaphore.Release();
            }
        }
        catch
        {
            return null;
        }
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
    /// Pre-generates thumbnails for a batch of images.
    /// </summary>
    public async Task GenerateBatchAsync(
        IEnumerable<(long imageId, string filePath)> images,
        ThumbnailSize size,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        int count = 0;
        var semaphore = new SemaphoreSlim(Environment.ProcessorCount);

        var tasks = images.Select(async img =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!await _thumbnailRepo.HasThumbnailAsync(img.imageId, size))
                {
                    await GetOrCreateThumbnailAsync(img.imageId, img.filePath, size);
                }
                var c = Interlocked.Increment(ref count);
                if (c % 50 == 0) progress?.Report(c);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        progress?.Report(count);
    }
}
