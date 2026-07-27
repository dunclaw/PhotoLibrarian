using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Crops images in-place, preserving metadata via WIC transcoding.
///
/// Pipeline:
///   1) Decode the source file with EXIF orientation applied (RespectExifOrientation), so the
///      caller's crop bounds are interpreted in display coordinates.
///   2) Extract the cropped region as a SoftwareBitmap via GetSoftwareBitmapAsync with a
///      BitmapTransform.Bounds clip.
///   3) Build a transcoded encoder seeded from the source decoder (this preserves all metadata),
///      then SetSoftwareBitmap on it to replace the pixel data with the cropped result.
///   4) Reset System.Photo.Orientation to 1 (top-left) since we baked the rotation into pixels.
///   5) Flush to an in-memory buffer, then atomically replace the source file.
///
/// NOTE: For JPEG this is a re-encode (not block-level lossless crop). Lossless JPEG block crop
/// is a future optimization that requires bounds aligned to 8x8/16x16 blocks.
/// </summary>
public static class CropService
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp" };

    public static bool IsSupported(string filePath) =>
        SupportedExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>
    /// Crops the image in-place. <paramref name="bounds"/> is expressed in display (oriented) pixels
    /// matching what the user sees in the viewer. Returns the new (width, height) of the file on disk.
    /// </summary>
    public static async Task<(uint Width, uint Height)> CropImageAsync(string filePath, BitmapBounds bounds)
    {
        if (!IsSupported(filePath))
            throw new NotSupportedException($"Crop not supported for {Path.GetExtension(filePath)}");
        if (bounds.Width == 0 || bounds.Height == 0)
            throw new ArgumentException("Crop bounds must be non-empty", nameof(bounds));

        var file = await StorageFile.GetFileFromPathAsync(filePath);

        // 1) Read source bitmap with EXIF orientation applied; extract cropped pixels.
        SoftwareBitmap cropped;
        BitmapDecoder srcDecoder;
        using (var srcStream = await file.OpenAsync(FileAccessMode.Read))
        {
            srcDecoder = await BitmapDecoder.CreateAsync(srcStream);

            // Clamp bounds to the oriented image extents to avoid HRESULT 0x88982F8A.
            var orientedWidth = srcDecoder.OrientedPixelWidth;
            var orientedHeight = srcDecoder.OrientedPixelHeight;
            var clamped = ClampBounds(bounds, orientedWidth, orientedHeight);

            var transform = new BitmapTransform { Bounds = clamped };
            cropped = await srcDecoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);
        }

        // 2) Transcode: re-open as a fresh decoder for the encoder seed (encoder takes ownership),
        //    then replace pixel data and reset orientation since we baked the rotation in.
        var destBuffer = new InMemoryRandomAccessStream();
        using (var srcStream = await file.OpenAsync(FileAccessMode.Read))
        {
            var decoder = await BitmapDecoder.CreateAsync(srcStream);
            var encoder = await BitmapEncoder.CreateForTranscodingAsync(destBuffer, decoder);
            encoder.SetSoftwareBitmap(cropped);

            // Reset orientation tag — pixels are now in display orientation.
            try
            {
                var props = new BitmapPropertySet
                {
                    { "System.Photo.Orientation", new BitmapTypedValue((ushort)1, Windows.Foundation.PropertyType.UInt16) }
                };
                await encoder.BitmapProperties.SetPropertiesAsync(props);
            }
            catch
            {
                // Some formats don't support property setting; non-fatal.
            }

            await encoder.FlushAsync();
        }
        cropped.Dispose();

        // 3) Atomic-ish replace: stream over the source file.
        destBuffer.Seek(0);
        using (var outStream = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            outStream.Size = 0;
            await RandomAccessStream.CopyAsync(destBuffer.GetInputStreamAt(0), outStream);
            await outStream.FlushAsync();
        }
        destBuffer.Dispose();

        return (bounds.Width, bounds.Height);
    }

    private static BitmapBounds ClampBounds(BitmapBounds b, uint maxWidth, uint maxHeight)
    {
        uint x = Math.Min(b.X, maxWidth);
        uint y = Math.Min(b.Y, maxHeight);
        uint w = Math.Min(b.Width, maxWidth - x);
        uint h = Math.Min(b.Height, maxHeight - y);
        return new BitmapBounds { X = x, Y = y, Width = w, Height = h };
    }
}
