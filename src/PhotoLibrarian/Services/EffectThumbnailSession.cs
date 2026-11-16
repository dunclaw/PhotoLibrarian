// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Numerics;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace PhotoLibrarian.Services;

internal sealed record EffectThumbnail(PhotoEffect Effect, int Width, int Height, byte[] Pixels);

/// <summary>Latest request wins; only one bounded decode/render may run at a time.</summary>
internal sealed class EffectThumbnailSession
{
    internal const int MaximumDimension = 160;
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private readonly Func<string, EditParameters, CancellationToken, Task<IReadOnlyList<EffectThumbnail>>> _render;
    private CancellationTokenSource? _request;

    internal EffectThumbnailSession() : this(RenderAsync) { }

    internal EffectThumbnailSession(
        Func<string, EditParameters, CancellationToken, Task<IReadOnlyList<EffectThumbnail>>> render)
    {
        _render = render;
    }

    internal void Invalidate() => _request?.Cancel();

    internal async Task<IReadOnlyList<EffectThumbnail>?> RequestAsync(string path, EditParameters parameters)
    {
        Invalidate();
        using var request = new CancellationTokenSource();
        _request = request;
        var snapshot = parameters.Clone();
        try
        {
            await Task.Delay(120, request.Token);
            await _renderGate.WaitAsync(request.Token);
            try
            {
                var result = await Task.Run(() => _render(path, snapshot, request.Token), request.Token);
                // A codec/render can finish after cancellation without observing its token.
                request.Token.ThrowIfCancellationRequested();
                return result;
            }
            finally { _renderGate.Release(); }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { return null; }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
        }
    }

    private static async Task<IReadOnlyList<EffectThumbnail>> RenderAsync(
        string path, EditParameters parameters, CancellationToken token) =>
        await RenderThumbnailsAsync(path, parameters, Enum.GetValues<PhotoEffect>(), MaximumDimension, token);

    internal static async Task<EffectThumbnail> RenderRecipeThumbnailAsync(
        string path, EditParameters parameters, int size, CancellationToken token)
    {
        var thumbnails = await RenderThumbnailsAsync(path, parameters.Clone(), [parameters.Effect], size, token);
        return thumbnails[0];
    }

    internal static EffectThumbnail RenderRecipeThumbnailFromPixels(
        string path,
        byte[] sourcePixels,
        int width,
        int height,
        EditParameters parameters,
        int maximumDimension,
        CancellationToken token)
    {
        if (width <= 0 || height <= 0 ||
            sourcePixels.Length != checked(width * height * 4))
            throw new InvalidDataException("The developed preview has invalid pixel dimensions.");

        if (parameters.RetouchStrokes.Count > 0)
        {
            var retouch = ScaleRetouchStrokes(
                parameters.RetouchStrokes, width, height, width, height);
            var result = SpotRetouchProcessor.Apply(sourcePixels, width, height, retouch, token);
            if (!result.Succeeded)
                throw new InvalidDataException(result.Message ?? "The retouch preview could not be rendered.");
        }

        using var device = new CanvasDevice();
        using var source = CanvasBitmap.CreateFromBytes(
            device, sourcePixels, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96,
            CanvasAlphaMode.Premultiplied);
        var extent = EditEffectGraph.ComputeOutputExtent((uint)width, (uint)height, parameters.RotationAngle);
        var outputScale = Math.Min(1f, (float)maximumDimension / Math.Max(extent.Width, extent.Height));
        var outputWidth = Math.Max(1, (int)Math.Round(extent.Width * outputScale));
        var outputHeight = Math.Max(1, (int)Math.Round(extent.Height * outputScale));
        var transform = Matrix3x2.CreateTranslation(extent.Offset) * Matrix3x2.CreateScale(outputScale);
        using var target = new CanvasRenderTarget(device, outputWidth, outputHeight, 96);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(ImageEditRenderer.GetBackgroundColor(path));
            ImageEditRenderPass.Draw(session, source, parameters, new Vector2(width, height), transform);
        }

        token.ThrowIfCancellationRequested();
        var (croppedWidth, croppedHeight, croppedPixels) = ApplyCrop(
            target.GetPixelBytes(), outputWidth, outputHeight, parameters.Crop);
        return new(parameters.Effect, croppedWidth, croppedHeight, croppedPixels);
    }

    private static async Task<IReadOnlyList<EffectThumbnail>> RenderThumbnailsAsync(
        string path, EditParameters parameters, IReadOnlyList<PhotoEffect> effects, int maximumDimension,
        CancellationToken token)
    {
        if (!ImageEditRenderer.IsSupported(path))
            throw new NotSupportedException("Effect thumbnails require a supported raster photo.");
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var scale = Math.Min(1d, (double)maximumDimension / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var width = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
        var height = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform { ScaledWidth = width, ScaledHeight = height },
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        token.ThrowIfCancellationRequested();
        if (decoder.OrientedPixelWidth != decoder.PixelWidth)
            (width, height) = (height, width);
        using var device = new CanvasDevice();
        var sourcePixels = data.DetachPixelData();
        if (parameters.RetouchStrokes.Count > 0)
        {
            var retouch = ScaleRetouchStrokes(
                parameters.RetouchStrokes,
                (int)decoder.OrientedPixelWidth,
                (int)decoder.OrientedPixelHeight,
                (int)width,
                (int)height);
            var retouchResult = SpotRetouchProcessor.Apply(
                sourcePixels, (int)width, (int)height, retouch, token);
            if (!retouchResult.Succeeded)
                throw new InvalidDataException(
                    retouchResult.Message ?? "The retouch preview could not be rendered.");
        }
        using var source = CanvasBitmap.CreateFromBytes(device, sourcePixels,
            (int)width, (int)height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        var extent = EditEffectGraph.ComputeOutputExtent(width, height, parameters.RotationAngle);
        var outputScale = Math.Min(1f, (float)maximumDimension / Math.Max(extent.Width, extent.Height));
        var outputWidth = Math.Max(1, (int)Math.Round(extent.Width * outputScale));
        var outputHeight = Math.Max(1, (int)Math.Round(extent.Height * outputScale));
        var transform = Matrix3x2.CreateTranslation(extent.Offset) * Matrix3x2.CreateScale(outputScale);
        var result = new List<EffectThumbnail>();
        foreach (var effect in effects)
        {
            token.ThrowIfCancellationRequested();
            parameters.Effect = effect;
            using var target = new CanvasRenderTarget(device, outputWidth, outputHeight, 96);
            using (var session = target.CreateDrawingSession())
            {
                session.Clear(ImageEditRenderer.GetBackgroundColor(path));
                ImageEditRenderPass.Draw(session, source, parameters, new Vector2(width, height), transform);
            }
            var (croppedWidth, croppedHeight, croppedPixels) = ApplyCrop(
                target.GetPixelBytes(), outputWidth, outputHeight, parameters.Crop);
            result.Add(new EffectThumbnail(effect, croppedWidth, croppedHeight, croppedPixels));
        }
        return result;
    }

    private static (int Width, int Height, byte[] Pixels) ApplyCrop(
        byte[] pixels,
        int width,
        int height,
        CropRect? crop)
    {
        if (crop is null) return (width, height, pixels);
        if (!double.IsFinite(crop.Left) || !double.IsFinite(crop.Top) ||
            !double.IsFinite(crop.Width) || !double.IsFinite(crop.Height) ||
            crop.Left < 0 || crop.Top < 0 || crop.Width <= 0 || crop.Height <= 0 ||
            crop.Left + crop.Width > 1 || crop.Top + crop.Height > 1)
            throw new InvalidDataException("Crop must be inside the transformed photo.");

        var left = Math.Clamp((int)Math.Round(crop.Left * width), 0, width - 1);
        var top = Math.Clamp((int)Math.Round(crop.Top * height), 0, height - 1);
        var right = Math.Clamp((int)Math.Round((crop.Left + crop.Width) * width), left + 1, width);
        var bottom = Math.Clamp((int)Math.Round((crop.Top + crop.Height) * height), top + 1, height);
        var outputWidth = right - left;
        var outputHeight = bottom - top;
        var output = new byte[checked(outputWidth * outputHeight * 4)];
        for (var y = 0; y < outputHeight; y++)
            Buffer.BlockCopy(pixels, ((top + y) * width + left) * 4,
                output, y * outputWidth * 4, outputWidth * 4);
        return (outputWidth, outputHeight, output);
    }

    private static IReadOnlyList<RetouchStroke> ScaleRetouchStrokes(
        IReadOnlyList<RetouchStroke> strokes,
        int sourceWidth,
        int sourceHeight,
        int previewWidth,
        int previewHeight)
    {
        var scaleX = previewWidth / (double)sourceWidth;
        var scaleY = previewHeight / (double)sourceHeight;
        var radiusScale = (scaleX + scaleY) / 2;
        var result = new List<RetouchStroke>(strokes.Count);
        foreach (var stroke in strokes)
        {
            if (stroke.ImageWidth != sourceWidth || stroke.ImageHeight != sourceHeight)
                throw new InvalidDataException("Retouch recipe dimensions do not match the source photo.");
            var points = stroke.Points.Select(point => new RetouchPoint(
                Math.Clamp((int)Math.Round(point.X * scaleX), 0, previewWidth - 1),
                Math.Clamp((int)Math.Round(point.Y * scaleY), 0, previewHeight - 1))).ToArray();
            var radius = Math.Clamp((int)Math.Round(stroke.Radius * radiusScale),
                2, SpotRetouchProcessor.MaxRadius);
            result.Add(new RetouchStroke(previewWidth, previewHeight, radius, points));
        }
        return result;
    }
}
