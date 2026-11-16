// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PhotoLibrarian.Services;

/// <summary>Read-only, source-resolution loading shared with the full-photo viewer.</summary>
internal static class ViewerImageLoader
{
    internal static async Task<BitmapImage> LoadRasterAsync(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (ImageEditRenderer.IsSupported(path))
        {
            var recipe = ImageEditService.ReadEditParameters(path);
            if (recipe.HasAdjustments)
            {
                var rendered = await ViewerEffectService.RenderAsync(path, recipe);
                token.ThrowIfCancellationRequested();
                return await CreateBitmapAsync(rendered, token);
            }
        }
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
        using var stream = await file.OpenReadAsync().AsTask(token);
        var bitmap = new BitmapImage { AutoPlay = false };
        await bitmap.SetSourceAsync(stream).AsTask(token);
        token.ThrowIfCancellationRequested();
        return bitmap;
    }

    internal static async Task<BitmapImage> LoadPhotoAsync(string path, CancellationToken token)
    {
        if (!RawToneMappingService.IsRaw(path))
            return await LoadRasterAsync(path, token);

        using var preview = new RawPreviewSession();
        var recipe = ImageEditService.ReadEditParameters(path);
        var raw = await preview.RenderAsync(path, recipe, token);
        var pixels = await Task.Run(() => raw.ToPreviewBgra8(token), token);
        return await CreateBitmapAsync(
            new ImageEditRenderer.RenderedPixels((uint)raw.Width, (uint)raw.Height, pixels), token);
    }

    internal static async Task<BitmapImage> CreateBitmapAsync(
        ImageEditRenderer.RenderedPixels rendered, CancellationToken token = default)
    {
        using var stream = await Task.Run(async () =>
        {
            var buffer = new InMemoryRandomAccessStream();
            try
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, buffer).AsTask(token);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    rendered.Width, rendered.Height, 96, 96, rendered.Pixels);
                await encoder.FlushAsync().AsTask(token);
                buffer.Seek(0);
                return buffer;
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
        }, token);
        var bitmap = new BitmapImage { AutoPlay = false };
        await bitmap.SetSourceAsync(stream).AsTask(token);
        token.ThrowIfCancellationRequested();
        return bitmap;
    }
}
