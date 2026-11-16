// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;

namespace PhotoLibrarian.Services;

internal sealed record EditRecipePreview(int Width, int Height, byte[] Pixels);

internal sealed class EditRecipePreviewSession : IDisposable
{
    private const int PreviewDimension = 384;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RawPreviewSession _rawPreview = new();
    private CancellationTokenSource? _request;

    internal async Task<EditRecipePreview?> RequestAsync(string path, EditParameters parameters)
    {
        Invalidate();
        using var request = new CancellationTokenSource();
        _request = request;
        var snapshot = parameters.Clone();
        try
        {
            await Task.Delay(120, request.Token);
            await _gate.WaitAsync(request.Token);
            try
            {
                var preview = RawToneMappingService.IsRaw(path)
                    ? await RenderRawAsync(path, snapshot, request.Token)
                    : await RenderRasterAsync(path, snapshot, request.Token);
                request.Token.ThrowIfCancellationRequested();
                return preview;
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (ReferenceEquals(_request, request)) _request = null;
        }
    }

    internal void Invalidate()
    {
        _request?.Cancel();
        _rawPreview.Invalidate();
    }

    public void Dispose()
    {
        Invalidate();
        _rawPreview.Dispose();
    }

    private static async Task<EditRecipePreview> RenderRasterAsync(
        string path, EditParameters parameters, CancellationToken token)
    {
        var thumbnail = await EffectThumbnailSession.RenderRecipeThumbnailAsync(
            path, parameters, PreviewDimension, token);
        return new(thumbnail.Width, thumbnail.Height, thumbnail.Pixels);
    }

    private async Task<EditRecipePreview> RenderRawAsync(
        string path, EditParameters parameters, CancellationToken token)
    {
        var raw = await _rawPreview.RenderAsync(path, parameters, token);
        var pixels = await Task.Run(() => raw.ToPreviewBgra8(token), token);
        var thumbnail = await Task.Run(() => EffectThumbnailSession.RenderRecipeThumbnailFromPixels(
            path, pixels, raw.Width, raw.Height, parameters, PreviewDimension, token), token);
        return new(thumbnail.Width, thumbnail.Height, thumbnail.Pixels);
    }
}
