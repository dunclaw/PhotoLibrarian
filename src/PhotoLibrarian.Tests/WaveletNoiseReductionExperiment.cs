// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using System.Numerics;
using Windows.Foundation;

namespace PhotoLibrarian.Tests;

internal static class WaveletNoiseReductionExperiment
{
    private static readonly Lazy<byte[]> Shader = new(() =>
    {
        using var stream = typeof(WaveletNoiseReductionExperiment).Assembly.GetManifestResourceStream(
            "PhotoLibrarian.Tests.WaveletNoiseReductionExperiment.cso")
            ?? throw new InvalidOperationException("The wavelet denoising experiment shader is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    });

    internal static ICanvasImage Create(ICanvasImage source, Vector2 sourceSize, double amount)
    {
        if (!double.IsFinite(amount))
            throw new ArgumentOutOfRangeException(nameof(amount), "Wavelet denoising must be finite.");

        var strength = (float)Math.Clamp(amount, 0, 1);
        if (strength == 0) return source;

        var effect = new PixelShaderEffect(Shader.Value)
        {
            Source1 = source,
            Source1Mapping = SamplerCoordinateMapping.Offset,
            Source1BorderMode = EffectBorderMode.Soft,
            Source1Interpolation = CanvasImageInterpolation.NearestNeighbor,
            MaxSamplerOffset = 1
        };
        effect.Properties["Threshold"] = strength * 0.06f;
        return new CropEffect
        {
            Source = effect,
            SourceRectangle = new Rect(0, 0, sourceSize.X, sourceSize.Y)
        };
    }
}
