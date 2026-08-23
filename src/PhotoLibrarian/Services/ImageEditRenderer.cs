using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using PhotoLibrarian.Core.Models;
using System.Numerics;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PhotoLibrarian.Services;

/// <summary>
/// Bakes editor adjustments into the pixels of an image file.
///
/// Pipeline:
///   1) Decode the source at full resolution with EXIF orientation applied, so what is rendered
///      matches what the user sees.
///   2) Run the same effect graph the preview uses into a CanvasRenderTarget.
///   3) Read the rendered pixels back and transcode them over the original file, seeding the
///      encoder from the source decoder so metadata (EXIF, XMP) survives, then reset
///      System.Photo.Orientation to 1 because the orientation is now baked into the pixels —
///      the same flow crop uses.
///
/// Callers are responsible for backing the original up first — see
/// <see cref="Core.Services.ImageEditService.ApplyEditsAsync"/>.
/// </summary>
public static class ImageEditRenderer
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp" };

    /// <summary>Formats that keep an alpha channel, where rotation corners stay transparent.</summary>
    private static readonly HashSet<string> AlphaExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".tif", ".tiff" };

    public static bool IsSupported(string filePath) =>
        SupportedExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>
    /// Decodes an image into a Win2D bitmap with its EXIF orientation applied. Win2D's own
    /// loaders ignore orientation, which would show (and bake) sideways pixels for phone photos.
    /// </summary>
    public static async Task<CanvasBitmap> LoadOrientedAsync(ICanvasResourceCreator device, string filePath)
    {
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        return CanvasBitmap.CreateFromSoftwareBitmap(device, bitmap);
    }

    /// <summary>
    /// Renders <paramref name="parameters"/> into the pixels of <paramref name="filePath"/>,
    /// replacing the file in place. Returns the new (width, height) of the file on disk.
    /// </summary>
    public static async Task<(uint Width, uint Height)> RenderToFileAsync(
        string filePath, EditParameters parameters)
    {
        if (!IsSupported(filePath))
            throw new NotSupportedException($"Editing not supported for {Path.GetExtension(filePath)}");

        var device = CanvasDevice.GetSharedDevice();

        byte[] pixels;
        uint outWidth;
        uint outHeight;

        using (var source = await LoadOrientedAsync(device, filePath))
        {
            var sourceWidth = source.SizeInPixels.Width;
            var sourceHeight = source.SizeInPixels.Height;

            var (offset, width, height) =
                EditEffectGraph.ComputeOutputExtent(sourceWidth, sourceHeight, parameters.RotationAngle);
            outWidth = width;
            outHeight = height;

            var effect = EditEffectGraph.Build(
                source, new Vector2(sourceWidth, sourceHeight), parameters);
            try
            {
                using var target = new CanvasRenderTarget(device, width, height, 96f);
                using (var session = target.CreateDrawingSession())
                {
                    // Rotation exposes corners that the source doesn't cover. Formats without an
                    // alpha channel would flatten transparency to black, so fill them with white.
                    session.Clear(AlphaExtensions.Contains(Path.GetExtension(filePath))
                        ? Microsoft.UI.Colors.Transparent
                        : Microsoft.UI.Colors.White);
                    session.DrawImage(effect, offset);
                }
                pixels = target.GetPixelBytes();
            }
            finally
            {
                // Effects hold no unmanaged file handles; disposing the outermost one releases the
                // D2D resources we created here (the source bitmap is owned by the using above).
                if (!ReferenceEquals(effect, source)) (effect as IDisposable)?.Dispose();
            }
        }

        await EncodeOverFileAsync(filePath, pixels, outWidth, outHeight);
        return (outWidth, outHeight);
    }

    /// <summary>
    /// Writes <paramref name="pixels"/> (BGRA8, premultiplied) over the file, transcoding from the
    /// existing decoder so metadata survives the round trip.
    /// </summary>
    private static async Task EncodeOverFileAsync(string filePath, byte[] pixels, uint width, uint height)
    {
        var file = await StorageFile.GetFileFromPathAsync(filePath);

        using var rendered = SoftwareBitmap.CreateCopyFromBuffer(
            CryptographicBuffer.CreateFromByteArray(pixels),
            BitmapPixelFormat.Bgra8,
            (int)width,
            (int)height,
            BitmapAlphaMode.Premultiplied);

        var destBuffer = new InMemoryRandomAccessStream();
        using (var srcStream = await file.OpenAsync(FileAccessMode.Read))
        {
            var decoder = await BitmapDecoder.CreateAsync(srcStream);
            var encoder = await BitmapEncoder.CreateForTranscodingAsync(destBuffer, decoder);
            encoder.SetSoftwareBitmap(rendered);

            // The orientation is baked into the pixels now, so the tag has to say "top-left".
            try
            {
                await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
                {
                    { "System.Photo.Orientation", new BitmapTypedValue((ushort)1, Windows.Foundation.PropertyType.UInt16) }
                });
            }
            catch
            {
                // Some formats don't support property setting; non-fatal.
            }

            await encoder.FlushAsync();
        }

        destBuffer.Seek(0);
        using (var outStream = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            outStream.Size = 0;
            await RandomAccessStream.CopyAsync(destBuffer.GetInputStreamAt(0), outStream);
            await outStream.FlushAsync();
        }
        destBuffer.Dispose();
    }
}
