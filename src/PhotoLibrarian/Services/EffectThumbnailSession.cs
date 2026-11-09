// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
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
        using var source = CanvasBitmap.CreateFromBytes(device, data.DetachPixelData(),
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
            result.Add(new EffectThumbnail(effect, outputWidth, outputHeight, target.GetPixelBytes()));
        }
        return result;
    }
}
