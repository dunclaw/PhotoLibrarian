// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PhotoLibrarian.Services;

internal static class RecipeThumbnailService
{
    private static readonly SemaphoreSlim RenderGate = new(2, 2);

    internal static async Task<byte[]?> GetThumbnailStreamAsync(string path, int size, CancellationToken token = default)
    {
        if (!ImageEditRenderer.IsSupported(path))
            return await WindowsThumbnailService.GetThumbnailStreamAsync(path, size, token);
        var recipe = await Task.Run(() =>
        {
            var sidecar = Path.ChangeExtension(path, ".xmp");
            if (!File.Exists(sidecar)) return ImageEditService.ReadEditParameters(path);
            using var stream = File.OpenRead(sidecar);
            return EditParametersSerializer.ReadFromXmp(XmpCore.XmpMetaFactory.Parse(stream));
        }, token);
        if (!recipe.HasAdjustments)
            return await WindowsThumbnailService.GetThumbnailStreamAsync(path, size, token);

        await RenderGate.WaitAsync(token);
        try
        {
            return await Task.Run(async () =>
            {
                var thumbnail = await EffectThumbnailSession.RenderRecipeThumbnailAsync(path, recipe, size, token);
                using var stream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)thumbnail.Width, (uint)thumbnail.Height, 96, 96, thumbnail.Pixels);
                await encoder.FlushAsync();
                token.ThrowIfCancellationRequested();
                var bytes = new byte[checked((int)stream.Size)];
                stream.Seek(0);
                await stream.AsStreamForRead().ReadExactlyAsync(bytes, token);
                return bytes;
            }, token);
        }
        finally { RenderGate.Release(); }
    }

    internal static (DateTime Modified, long Length) GetRecipeStamp(string path)
    {
        var sidecar = new FileInfo(Path.ChangeExtension(path, ".xmp"));
        return sidecar.Exists ? (sidecar.LastWriteTimeUtc, sidecar.Length) : default;
    }
}
