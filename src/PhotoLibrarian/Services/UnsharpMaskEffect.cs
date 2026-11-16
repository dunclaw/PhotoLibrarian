// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using System.Numerics;
using Windows.Foundation;

namespace PhotoLibrarian.Services;

/// <summary>Builds a source-pixel unsharp mask with a bounded, thresholded pixel shader.</summary>
public static class UnsharpMaskEffect
{
    private static readonly Lazy<byte[]> Shader = new(() =>
    {
        using var stream = typeof(UnsharpMaskEffect).Assembly.GetManifestResourceStream(
            "PhotoLibrarian.Shaders.UnsharpMask.cso")
            ?? throw new InvalidOperationException("The unsharp-mask shader is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    });

    public static ICanvasImage Create(
        ICanvasImage source,
        Vector2 sourceSize,
        double amount,
        double radius,
        double threshold)
    {
        if (!double.IsFinite(amount))
            throw new ArgumentOutOfRangeException(nameof(amount), "Sharpening amount must be finite.");
        if (!double.IsFinite(radius))
            throw new ArgumentOutOfRangeException(nameof(radius), "Sharpening radius must be finite.");
        if (!double.IsFinite(threshold))
            throw new ArgumentOutOfRangeException(nameof(threshold), "Sharpening threshold must be finite.");

        var strength = (float)Math.Clamp(amount, 0, 2);
        if (strength == 0) return source;

        var blurAmount = (float)Math.Clamp(radius, 0.5, 5);
        var noiseThreshold = (float)Math.Clamp(threshold, 0, 0.25);
        var blur = new CropEffect
        {
            Source = new GaussianBlurEffect
            {
                Source = source,
                BlurAmount = blurAmount,
                BorderMode = EffectBorderMode.Hard,
                Optimization = EffectOptimization.Balanced
            },
            SourceRectangle = new Rect(0, 0, sourceSize.X, sourceSize.Y)
        };
        var effect = new PixelShaderEffect(Shader.Value)
        {
            Source1 = source,
            Source1Mapping = SamplerCoordinateMapping.Offset,
            Source1BorderMode = EffectBorderMode.Hard,
            Source2 = blur,
            Source2Mapping = SamplerCoordinateMapping.Offset,
            Source2BorderMode = EffectBorderMode.Hard,
            MaxSamplerOffset = 1
        };
        effect.Properties["Amount"] = strength;
        effect.Properties["Threshold"] = noiseThreshold;
        return new CropEffect
        {
            Source = effect,
            SourceRectangle = new Rect(0, 0, sourceSize.X, sourceSize.Y)
        };
    }
}
