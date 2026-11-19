// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using System.Numerics;
using Windows.Foundation;

namespace PhotoLibrarian.Services;

/// <summary>
/// Isolated raster primitives, not yet enabled in the editor or portable recipes.
/// Samples are in oriented source pixels; tiled callers must supply an eight-pixel halo.
/// </summary>
internal static class SourceDetailEffect
{
    internal const int Halo = 8;

    private static readonly Lazy<byte[]> Shader = new(() =>
    {
        using var stream = typeof(SourceDetailEffect).Assembly.GetManifestResourceStream(
            "PhotoLibrarian.Shaders.SourceDetail.cso")
            ?? throw new InvalidOperationException("The source detail shader is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    });

    internal static bool TryCreate(ICanvasImage source, Vector2 sourceSize,
        double dehaze, double texture, out ICanvasImage? image, out string error)
    {
        image = null;
        if (!float.IsFinite(sourceSize.X) || !float.IsFinite(sourceSize.Y) ||
            sourceSize.X <= 0 || sourceSize.Y <= 0)
        {
            error = "Source dimensions must be positive and finite.";
            return false;
        }
        if (!double.IsFinite(dehaze) || dehaze is < -1 or > 1 ||
            !double.IsFinite(texture) || texture is < -1 or > 1)
        {
            error = "Dehaze and Texture must be finite values between -1 and 1.";
            return false;
        }

        error = "";
        if (dehaze == 0 && texture == 0)
        {
            image = source;
            return true;
        }

        var effect = new PixelShaderEffect(Shader.Value)
        {
            Source1 = source,
            Source1Mapping = SamplerCoordinateMapping.Offset,
            Source1BorderMode = EffectBorderMode.Hard,
            Source1Interpolation = CanvasImageInterpolation.NearestNeighbor,
            MaxSamplerOffset = Halo
        };
        effect.Properties["Dehaze"] = (float)dehaze;
        effect.Properties["Texture"] = (float)texture;
        image = new CropEffect
        {
            Source = effect,
            SourceRectangle = new Rect(0, 0, sourceSize.X, sourceSize.Y)
        };
        return true;
    }
}
