// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using System.Numerics;
using Windows.Foundation;

namespace PhotoLibrarian.Services;

/// <summary>
/// Isolated GPU post-output primitive. The caller supplies already composited/cropped
/// pixels and an axis-aligned mapping from local effect pixels to final output pixels.
/// No geometry, viewport transform, recipe persistence or editor activation is performed here.
/// </summary>
internal static class PostCropEffect
{
    private static readonly Lazy<byte[]> Shader = new(() =>
    {
        using var stream = typeof(PostCropEffect).Assembly.GetManifestResourceStream(
            "PhotoLibrarian.Shaders.PostCrop.cso")
            ?? throw new InvalidOperationException("The post-crop shader is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    });

    internal static bool TryCreate(ICanvasImage source, Vector2 sourceSize,
        PostCropEffectSettings settings, Vector2 outputSize, Vector2 outputOrigin,
        Vector2 pixelToOutputScale, out ICanvasImage? image, out string error)
    {
        image = null;
        if (!settings.TryValidate(out error)) return false;
        if (!Positive(sourceSize) || !Positive(outputSize) || !Positive(pixelToOutputScale) ||
            !float.IsFinite(outputOrigin.X) || !float.IsFinite(outputOrigin.Y))
        {
            error = "Effect dimensions, final output origin and pixel scale must be finite, with positive dimensions and scale.";
            return false;
        }
        if (settings.GrainAmount > 0)
        {
            var cell = Math.Min(outputSize.X, outputSize.Y) * (0.0005 + 0.0095 * settings.GrainSize);
            var frequency = settings.Roughness > 0 ? 2 : 1;
            var left = outputOrigin.X / cell * frequency;
            var top = outputOrigin.Y / cell * frequency;
            var right = (outputOrigin.X + (double)sourceSize.X * pixelToOutputScale.X) / cell * frequency;
            var bottom = (outputOrigin.Y + (double)sourceSize.Y * pixelToOutputScale.Y) / cell * frequency;
            var footprintX = pixelToOutputScale.X / cell * frequency;
            var footprintY = pixelToOutputScale.Y / cell * frequency;
            if (!LatticeRange(left, right, footprintX) || !LatticeRange(top, bottom, footprintY))
            {
                error = "The requested grain coordinates or pixel footprint exceed GPU lattice precision.";
                return false;
            }
        }
        error = "";
        if (settings.VignetteAmount == 0 && settings.GrainAmount == 0)
        {
            image = source;
            return true;
        }
        var axes = Vector2.Lerp(outputSize, new Vector2(Math.Min(outputSize.X, outputSize.Y)),
            (float)settings.Roundness) * 0.5f;
        if (!Positive(axes))
        {
            error = "The requested vignette axes cannot be represented by the GPU.";
            return false;
        }
        var effect = new PixelShaderEffect(Shader.Value)
        {
            Source1 = source,
            Source1Mapping = SamplerCoordinateMapping.Offset,
            Source1BorderMode = EffectBorderMode.Hard,
            Source1Interpolation = CanvasImageInterpolation.NearestNeighbor,
            MaxSamplerOffset = 1
        };
        effect.Properties["OutputSize"] = outputSize;
        effect.Properties["OutputOrigin"] = outputOrigin;
        effect.Properties["PixelToOutputScale"] = pixelToOutputScale;
        effect.Properties["Vignette"] = new Vector4((float)settings.VignetteAmount,
            (float)settings.Feather, (float)settings.Roundness, (float)settings.HighlightPriority);
        effect.Properties["Grain"] = new Vector3((float)settings.GrainAmount,
            (float)settings.GrainSize, (float)settings.Roughness);
        // Two exactly representable halves avoid converting a full uint seed through float.
        effect.Properties["SeedParts"] = new Vector2(settings.Seed & 0xffff, settings.Seed >> 16);
        image = new CropEffect
        {
            Source = effect,
            SourceRectangle = new Rect(0, 0, sourceSize.X, sourceSize.Y)
        };
        return true;
    }

    private static bool Positive(Vector2 value) => float.IsFinite(value.X) &&
        float.IsFinite(value.Y) && value.X > 0 && value.Y > 0;

    private static bool LatticeRange(double first, double last, double footprint)
    {
        // These test actual shader integer/float representability, not image dimensions.
        if (!double.IsFinite(first) || !double.IsFinite(last) || !double.IsFinite(footprint) ||
            first - footprint <= int.MinValue || last + footprint >= int.MaxValue)
            return false;
        if ((double)(float)first - (float)footprint <= int.MinValue ||
            (double)(float)last + (float)footprint >= int.MaxValue)
            return false;
        var half = (float)(footprint / 2);
        return (float)first - half < (float)first + half &&
            (float)last - half < (float)last + half;
    }
}
