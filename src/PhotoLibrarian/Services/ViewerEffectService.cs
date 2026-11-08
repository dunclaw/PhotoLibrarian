// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;

namespace PhotoLibrarian.Services;

/// <summary>Keeps effects as portable pending recipes; never bakes over viewer photo pixels.</summary>
internal sealed class ViewerEffectService(OriginalBackupService backups)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task<ImageEditRenderer.RenderedPixels> SelectAsync(string path, PhotoEffect effect)
    {
        if (!ImageEditRenderer.IsSupported(path))
            throw new NotSupportedException("Photo effects require a supported raster photo.");
        if (!Enum.IsDefined(effect)) throw new ArgumentOutOfRangeException(nameof(effect));
        await _gate.WaitAsync();
        try
        {
            var sidecar = Path.ChangeExtension(path, ".xmp");
            if (File.Exists(sidecar))
            {
                using var stream = File.OpenRead(sidecar);
                _ = XmpCore.XmpMetaFactory.Parse(stream);
            }
            var parameters = ImageEditService.ReadEditParameters(path);
            parameters.Effect = effect;
            var rendered = await RenderAsync(path, parameters);
            await new ImageEditService(backups).SaveEditParametersAsync(path, parameters);
            return rendered;
        }
        finally { _gate.Release(); }
    }

    internal static Task<ImageEditRenderer.RenderedPixels> RenderAsync(string path, EditParameters parameters) =>
        Task.Run(async () =>
        {
            using var device = new CanvasDevice();
            return await ImageEditRenderer.RenderToPixelsAsync(device, path, parameters);
        });
}
