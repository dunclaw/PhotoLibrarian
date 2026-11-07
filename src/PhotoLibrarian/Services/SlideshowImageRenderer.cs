// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage;

namespace PhotoLibrarian.Services;

/// <summary>Renders a slideshow image in memory without modifying its source file.</summary>
public static class SlideshowImageRenderer
{
    private const double MaximumPixelDimension = 3840;
    private const double MaximumPixelCount = 8_294_400;

    public static async Task<SoftwareBitmapSource> RenderAsync(
        string filePath,
        SlideshowTheme theme,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var file = await StorageFile.GetFileFromPathAsync(filePath);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);

        var sourceWidth = (double)decoder.OrientedPixelWidth;
        var sourceHeight = (double)decoder.OrientedPixelHeight;
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new InvalidDataException($"The image has invalid dimensions: {filePath}");

        var scale = Math.Min(1d, Math.Min(
            MaximumPixelDimension / Math.Max(sourceWidth, sourceHeight),
            Math.Sqrt(MaximumPixelCount / (sourceWidth * sourceHeight))));
        var width = (uint)Math.Max(1, Math.Round(sourceWidth * scale));
        var height = (uint)Math.Max(1, Math.Round(sourceHeight * scale));
        var transform = new BitmapTransform
        {
            ScaledWidth = width,
            ScaledHeight = height,
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        cancellationToken.ThrowIfCancellationRequested();
        var sourcePixels = pixelData.DetachPixelData();
        var renderedPixels = await Task.Run(
            () => RenderPixels(sourcePixels, width, height, theme, cancellationToken),
            cancellationToken);
        var softwareBitmap = await Task.Run(
            () => SoftwareBitmap.CreateCopyFromBuffer(
                CryptographicBuffer.CreateFromByteArray(renderedPixels),
                BitmapPixelFormat.Bgra8,
                checked((int)width),
                checked((int)height),
                BitmapAlphaMode.Premultiplied),
            cancellationToken);
        using (softwareBitmap)
        {
            var imageSource = new SoftwareBitmapSource();
            await imageSource.SetBitmapAsync(softwareBitmap);
            cancellationToken.ThrowIfCancellationRequested();
            return imageSource;
        }
    }

    private static byte[] RenderPixels(
        byte[] sourcePixels,
        uint width,
        uint height,
        SlideshowTheme theme,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var device = CanvasDevice.GetSharedDevice();
        using var source = CanvasBitmap.CreateFromBytes(
            device,
            sourcePixels,
            checked((int)width),
            checked((int)height),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            96f,
            CanvasAlphaMode.Premultiplied);
        using var target = new CanvasRenderTarget(device, width, height, 96f);
        using (var drawingSession = target.CreateDrawingSession())
            drawingSession.DrawImage(SlideshowEffectGraph.Build(source, theme));

        cancellationToken.ThrowIfCancellationRequested();
        return target.GetPixelBytes();
    }
}
