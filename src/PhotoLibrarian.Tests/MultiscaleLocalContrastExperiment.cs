// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using System.Numerics;
using Windows.Foundation;

namespace PhotoLibrarian.Tests;

internal static class MultiscaleLocalContrastExperiment
{
    private static readonly Lazy<byte[]> Shader = new(() =>
    {
        using var stream = typeof(MultiscaleLocalContrastExperiment).Assembly.GetManifestResourceStream(
            "PhotoLibrarian.Tests.MultiscaleLocalContrastExperiment.cso")
            ?? throw new InvalidOperationException("The local-contrast experiment shader is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    });

    private static readonly (float Radius, float Gain)[] Scales =
    [
        (2, 0.15f),
        (8, 0.10f),
        (24, 0.05f)
    ];

    internal static ICanvasImage Create(ICanvasImage source, Vector2 sourceSize, double amount)
    {
        if (!double.IsFinite(amount))
            throw new ArgumentOutOfRangeException(nameof(amount), "Local-contrast amount must be finite.");
        if (!float.IsFinite(sourceSize.X) || !float.IsFinite(sourceSize.Y) ||
            sourceSize.X <= 0 || sourceSize.Y <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSize), "Source dimensions must be finite and positive.");
        }

        var strength = (float)Math.Clamp(amount, 0, 1);
        if (strength == 0) return source;

        ICanvasImage current = source;
        foreach (var (radius, gain) in Scales)
        {
            var blur = new CropEffect
            {
                Source = new GaussianBlurEffect
                {
                    Source = current,
                    BlurAmount = radius,
                    BorderMode = EffectBorderMode.Hard,
                    Optimization = EffectOptimization.Balanced
                },
                SourceRectangle = new Rect(0, 0, sourceSize.X, sourceSize.Y)
            };
            var effect = new PixelShaderEffect(Shader.Value)
            {
                Source1 = current,
                Source1Mapping = SamplerCoordinateMapping.Offset,
                Source1BorderMode = EffectBorderMode.Hard,
                Source2 = blur,
                Source2Mapping = SamplerCoordinateMapping.Offset,
                Source2BorderMode = EffectBorderMode.Hard,
                MaxSamplerOffset = 1
            };
            effect.Properties["Gain"] = gain * strength;
            current = new CropEffect
            {
                Source = effect,
                SourceRectangle = new Rect(0, 0, sourceSize.X, sourceSize.Y)
            };
        }

        return current;
    }
}
