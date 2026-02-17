using Windows.Storage;
using Windows.Storage.FileProperties;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Uses Windows.Storage thumbnail provider (same as File Explorer) for fast thumbnail generation.
/// This is what Windows Explorer uses internally and is highly optimized.
/// </summary>
public static class WindowsThumbnailService
{
    /// <summary>
    /// Gets thumbnail as encoded bytes (PNG/BMP from cache). Much faster than pixel decode.
    /// Use this for BitmapImage.SetSourceAsync() which can decode the stream directly.
    /// </summary>
    public static async Task<byte[]?> GetThumbnailStreamAsync(string filePath, int size)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            
            // Request cached thumbnail - UseCurrentScale avoids DPI scaling overhead
            using var thumb = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem,
                (uint)size,
                ThumbnailOptions.UseCurrentScale);
            
            if (thumb == null || thumb.Size == 0)
                return null;
            
            // Return the encoded stream bytes (PNG/BMP from cache)
            var bytes = new byte[thumb.Size];
            await thumb.AsStreamForRead().ReadAsync(bytes, 0, bytes.Length);
            return bytes;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WindowsThumbnailService.GetThumbnailStreamAsync failed for {System.IO.Path.GetFileName(filePath)}: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Gets thumbnail as decoded BGRA8 pixels. Use this for WriteableBitmap.
    /// </summary>
    public static async Task<(byte[] pixels, int width, int height)?> GenerateThumbnailPixelsAsync(string filePath, int size)
    {
        try
        {
            // Use WinRT API which leverages Windows thumbnail cache (what Explorer uses)
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            
            // Request thumbnail - use cache if available, generate if needed
            // UseCurrentScale = don't apply DPI scaling
            // ReturnOnlyIfCached would fail if no cache exists, so we omit it
            using var thumb = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem,
                (uint)size,
                ThumbnailOptions.UseCurrentScale);
            
            if (thumb == null || thumb.Size == 0)
                return null;
            
            // Read the stream directly - thumbnail cache already gives us decoded bitmap data
            var bytes = new byte[thumb.Size];
            await thumb.AsStreamForRead().ReadAsync(bytes, 0, bytes.Length);
            
            // Decode to BGRA8 pixels
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var pixelData = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                new Windows.Graphics.Imaging.BitmapTransform(),
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            
            var pixels = pixelData.DetachPixelData();
            return (pixels, (int)decoder.PixelWidth, (int)decoder.PixelHeight);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WindowsThumbnailService failed for {System.IO.Path.GetFileName(filePath)}: {ex.Message}");
            return null;
        }
    }
}
