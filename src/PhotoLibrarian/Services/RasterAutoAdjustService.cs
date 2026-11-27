// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;

namespace PhotoLibrarian.Services;

internal static class RasterAutoAdjustService
{
    internal static bool Supports(ImageEntry image) =>
        image.MediaType == MediaType.Image &&
        ImageEditRenderer.IsSupported(image.FilePath) &&
        ImageEditService.SupportsEmbeddedRecipe(image.FilePath);

    internal static async Task<(EditParameters? Recipe, string? SkipReason)> AnalyzeAsync(
        string path, CancellationToken cancellationToken)
    {
        // Use the same oriented sRGB source as recipe rendering, but only sample it for statistics.
        var decoded = await RasterImageDecoder.DecodeAsync(path, maximumDimension: 1024, cancellationToken);
        if (decoded.Pixels is not { } pixels)
            return (null, decoded.Profile.Message ?? PhotoContextMenuStrings.Get("BatchAutoAdjustUnsupported"));
        if (!UseStraightColors(pixels))
            return (null, PhotoContextMenuStrings.Get("BatchAutoAdjustNoVisiblePixels"));

        cancellationToken.ThrowIfCancellationRequested();
        var recipe = ImageEditService.ReadEditParameters(path);
        return ViewerQuickEditService.ApplyAutoAdjust(recipe, pixels) ? (recipe, null) : (null, null);
    }

    private static bool UseStraightColors(byte[] pixels)
    {
        var hasVisiblePixels = false;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var alpha = pixels[offset + 3];
            if (alpha == 0) continue;
            hasVisiblePixels = true;
            if (alpha == 255) continue;
            for (var channel = offset; channel < offset + 3; channel++)
                pixels[channel] = (byte)Math.Min(255, (pixels[channel] * 255 + alpha / 2) / alpha);
        }
        return hasVisiblePixels;
    }
}
