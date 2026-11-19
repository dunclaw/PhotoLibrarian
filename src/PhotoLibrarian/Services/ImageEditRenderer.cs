// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Numerics;
using Windows.Graphics.DirectX;
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

    internal static Windows.UI.Color GetBackgroundColor(string filePath) =>
        AlphaExtensions.Contains(Path.GetExtension(filePath))
            ? Microsoft.UI.Colors.Transparent
            : Microsoft.UI.Colors.White;

    /// <summary>
    /// Decodes an image into an sRGB Win2D bitmap with its EXIF orientation applied. Win2D's own
    /// loaders ignore orientation, which would show (and bake) sideways pixels for phone photos.
    /// Uploading at 96 DPI keeps Win2D's DIP geometry aligned with source-pixel coordinates even
    /// when the file carries a rounded or non-96-DPI value.
    /// </summary>
    public static async Task<CanvasBitmap> LoadOrientedAsync(ICanvasResourceCreator device, string filePath)
    {
        var loaded = await TryLoadOrientedAsync(device, filePath);
        if (loaded.Bitmap is null) throw new InvalidDataException(loaded.Profile.Message);
        return loaded.Bitmap;
    }

    internal readonly record struct RasterBitmapLoad(CanvasBitmap? Bitmap, RasterColorProfile Profile);

    internal static async Task<RasterBitmapLoad> TryLoadOrientedAsync(
        ICanvasResourceCreator device, string filePath, CancellationToken cancellationToken = default)
    {
        if (RawToneMappingService.IsRaw(filePath))
            throw new NotSupportedException("RAW must use the scene-linear development and SDR preview pipeline, not the raster decoder.");
        var decoded = await RasterImageDecoder.DecodeAsync(filePath, cancellationToken: cancellationToken);
        if (!decoded.Profile.IsSupported) return new(null, decoded.Profile);

        var bitmap = CanvasBitmap.CreateFromBytes(
            device,
            decoded.Pixels!,
            checked((int)decoded.Width),
            checked((int)decoded.Height),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            96f,
            CanvasAlphaMode.Premultiplied);
        return new(bitmap, decoded.Profile);
    }

    /// <summary>
    /// Renders <paramref name="parameters"/> into the pixels of <paramref name="filePath"/>,
    /// replacing the file in place. Returns the new (width, height) of the file on disk.
    /// </summary>
    public static async Task<(uint Width, uint Height)> RenderToFileAsync(
        string filePath, EditParameters parameters)
    {
        return await RenderToFileAsync(
            filePath,
            parameters,
            CanvasDevice.GetSharedDevice(),
            autoCropRotation: false);
    }

    internal static Task<(uint Width, uint Height)> RenderToFileAsync(
        string filePath,
        EditParameters parameters,
        ICanvasResourceCreator device) =>
        RenderToFileAsync(filePath, parameters, device, autoCropRotation: false);

    /// <summary>
    /// Bakes a straighten rotation and crops to the largest centered rectangle containing no
    /// exposed rotation corners.
    /// </summary>
    public static async Task<(uint Width, uint Height)> RenderStraightenedAsync(
        string filePath,
        double angleDegrees)
    {
        return await RenderToFileAsync(
            filePath,
            new EditParameters { RotationAngle = angleDegrees },
            CanvasDevice.GetSharedDevice(),
            autoCropRotation: true);
    }

    /// <summary>
    /// Removes red-dominant pupil pixels inside a user-selected image region.
    /// </summary>
    public static async Task<int> RemoveRedEyeAsync(string filePath, RedEyeBounds bounds)
    {
        if (!IsSupported(filePath))
            throw new NotSupportedException($"Editing not supported for {Path.GetExtension(filePath)}");

        var device = CanvasDevice.GetSharedDevice();
        var loaded = await TryLoadOrientedAsync(device, filePath);
        if (loaded.Bitmap is null) throw new InvalidDataException(loaded.Profile.Message);
        using var source = loaded.Bitmap;
        var width = (int)source.SizeInPixels.Width;
        var height = (int)source.SizeInPixels.Height;
        var pixels = source.GetPixelBytes();
        var changed = RedEyeRemovalProcessor.Apply(pixels, width, height, bounds);
        if (changed == 0) return 0;

        await EncodeOverFileAsync(filePath, pixels, (uint)width, (uint)height, loaded.Profile);
        return changed;
    }

    /// <summary>
    /// Bakes a quarter-turn rotation into the image pixels and resets EXIF orientation.
    /// </summary>
    public static Task<(uint Width, uint Height)> RenderRotatedAsync(
        string filePath,
        bool clockwise) =>
        RenderToFileAsync(
            filePath,
            new EditParameters { RotationAngle = clockwise ? 90 : -90 });

    private static async Task<(uint Width, uint Height)> RenderToFileAsync(
        string filePath,
        EditParameters parameters,
        ICanvasResourceCreator device,
        bool autoCropRotation)
    {
        var (rendered, profile) = await RenderRasterAsync(device, filePath, parameters, autoCropRotation);
        await EncodeOverFileAsync(filePath, rendered.Pixels, rendered.Width, rendered.Height, profile);
        return (rendered.Width, rendered.Height);
    }

    internal static async Task<RenderedPixels> RenderToPixelsAsync(
        string filePath,
        EditParameters parameters,
        bool autoCropRotation = false)
        => await RenderToPixelsAsync(
            CanvasDevice.GetSharedDevice(),
            filePath,
            parameters,
            autoCropRotation);

    internal static async Task<RenderedPixels> RenderToPixelsAsync(
        ICanvasResourceCreator device,
        string filePath,
        EditParameters parameters,
        bool autoCropRotation = false,
        CancellationToken cancellationToken = default)
        => (await RenderRasterAsync(device, filePath, parameters, autoCropRotation, cancellationToken)).Rendered;

    private static async Task<(RenderedPixels Rendered, RasterColorProfile Profile)> RenderRasterAsync(
        ICanvasResourceCreator device,
        string filePath,
        EditParameters parameters,
        bool autoCropRotation,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported(filePath))
            throw new NotSupportedException($"Editing not supported for {Path.GetExtension(filePath)}");

        var loaded = await TryLoadOrientedAsync(device, filePath, cancellationToken);
        if (loaded.Bitmap is null) throw new InvalidDataException(loaded.Profile.Message);
        using var source = loaded.Bitmap;
        var sourceWidth = source.SizeInPixels.Width;
        var sourceHeight = source.SizeInPixels.Height;
        using var retouched = await CreateRetouchedAsync(device, source, parameters.RetouchStrokes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        Vector2 offset;
        uint outWidth;
        uint outHeight;
        if (autoCropRotation)
        {
            var crop = Core.Services.AutoCropGeometry.GetLargestRectangle(
                sourceWidth,
                sourceHeight,
                parameters.RotationAngle);
            outWidth = (uint)Math.Max(1, Math.Floor(crop.Width));
            outHeight = (uint)Math.Max(1, Math.Floor(crop.Height));
            offset = new Vector2(
                ((float)outWidth - sourceWidth) / 2f,
                ((float)outHeight - sourceHeight) / 2f);
        }
        else
        {
            var extent = EditEffectGraph.ComputeOutputExtent(
                sourceWidth,
                sourceHeight,
                parameters.RotationAngle);
            offset = extent.Offset;
            outWidth = extent.Width;
            outHeight = extent.Height;
        }

        using var target = new CanvasRenderTarget(device, outWidth, outHeight, 96f);
        using (var session = target.CreateDrawingSession())
        {
            // Rotation exposes corners that the source doesn't cover. Formats without an
            // alpha channel would flatten transparency to black, so fill them with white.
            session.Clear(GetBackgroundColor(filePath));
            ImageEditRenderPass.Draw(
                session,
                retouched ?? source,
                parameters,
                new Vector2(sourceWidth, sourceHeight),
                Matrix3x2.CreateTranslation(offset));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var pixels = target.GetPixelBytes();
        cancellationToken.ThrowIfCancellationRequested();
        return (new RenderedPixels(outWidth, outHeight, pixels), loaded.Profile);
    }

    internal readonly record struct RenderedPixels(uint Width, uint Height, byte[] Pixels);

    /// <summary>Source-resolution CPU stage shared by editor preview and export, before tone/detail/rotation.</summary>
    internal static async Task<CanvasBitmap?> CreateRetouchedAsync(
        ICanvasResourceCreator device, CanvasBitmap source, IReadOnlyList<RetouchStroke> strokes,
        CancellationToken cancellationToken = default)
    {
        var preview = await TryCreateRetouchedAsync(device, source, strokes, cancellationToken);
        // A saved recipe that cannot be replayed is a save failure, never an unretouched success.
        if (!preview.Result.Succeeded) throw new InvalidDataException(preview.Result.Message);
        return preview.Bitmap;
    }

    internal readonly record struct RetouchPreviewResult(CanvasBitmap? Bitmap, RetouchResult Result);

    internal static async Task<RetouchPreviewResult> TryCreateRetouchedAsync(
        ICanvasResourceCreator device, CanvasBitmap source, IReadOnlyList<RetouchStroke> strokes,
        CancellationToken cancellationToken = default)
    {
        if (strokes.Count == 0) return new(null, new(RetouchStatus.Applied));
        SpotRetouchProcessor.Validate(strokes);
        var width = checked((int)source.SizeInPixels.Width);
        var height = checked((int)source.SizeInPixels.Height);
        var pixels = source.GetPixelBytes();
        var result = await Task.Run(() => SpotRetouchProcessor.Apply(pixels, width, height, strokes, cancellationToken),
            cancellationToken);
        if (!result.Succeeded) return new(null, result);
        cancellationToken.ThrowIfCancellationRequested();
        var bitmap = CanvasBitmap.CreateFromBytes(device, pixels, width, height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        return new(bitmap, result);
    }

    /// <summary>
    /// Writes <paramref name="pixels"/> (BGRA8, premultiplied) over the file, transcoding from the
    /// existing decoder so metadata survives the round trip.
    /// </summary>
    private static async Task EncodeOverFileAsync(
        string filePath, byte[] pixels, uint width, uint height, RasterColorProfile profile)
    {
        // The metadata writer restores the source ICC tag; never put sRGB working bytes under it.
        var sourcePixels = await Task.Run(() => profile.FromSrgb(pixels, width, height));
        var file = await StorageFile.GetFileFromPathAsync(filePath);

        using var rendered = SoftwareBitmap.CreateCopyFromBuffer(
            CryptographicBuffer.CreateFromByteArray(sourcePixels),
            BitmapPixelFormat.Bgra8,
            (int)width,
            (int)height,
            BitmapAlphaMode.Premultiplied);

        using var destBuffer = new InMemoryRandomAccessStream();
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

        var staged = Path.Combine(Path.GetDirectoryName(filePath)!,
            $".{Path.GetFileNameWithoutExtension(filePath)}.{Guid.NewGuid():N}.bake{Path.GetExtension(filePath)}");
        try
        {
            destBuffer.Seek(0);
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await destBuffer.AsStreamForRead().CopyToAsync(output);
                await output.FlushAsync();
            }
            await Task.Run(() => MetadataWriterService.PreserveRasterColorProfileAsync(staged, profile));
            File.Replace(staged, filePath, destinationBackupFileName: null);
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }
}
