// Copyright (c) Duncan Lawler. All Rights Reserved.
using Windows.Graphics.Imaging;

namespace PhotoLibrarian.Core.Services;

public sealed record RasterImageDecodeResult(
    RasterColorProfile Profile, byte[]? Pixels = null, uint Width = 0, uint Height = 0,
    uint SourceWidth = 0, uint SourceHeight = 0);

/// <summary>Oriented, premultiplied BGRA8 sRGB working pixels for raster previews and rendering.</summary>
public static class RasterImageDecoder
{
    public static async Task<RasterImageDecodeResult> DecodeAsync(
        string path, uint maximumDimension = uint.MaxValue, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Keep profile and pixel reads on the same stable file version.
        await using var sourceLease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var profile = await Task.Run(() => RasterColorProfile.Read(path), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!profile.IsSupported) return new(profile);
        // Preserve the recipe-thumbnail decoder's existing platform-default resampling.
        var decoded = await WindowsImagingCodec.DecodeAsync(
            path, maximumDimension, cancellationToken, ColorManagementMode.DoNotColorManage,
            new BitmapTransform().InterpolationMode);
        var pixels = await Task.Run(() => profile.ToSrgb(decoded.Pixels, decoded.Width, decoded.Height),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(profile, pixels, decoded.Width, decoded.Height, decoded.SourceWidth, decoded.SourceHeight);
    }
}
