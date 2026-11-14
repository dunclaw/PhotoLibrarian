// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using PhotoLibrarian.Core.Models;
using System.Numerics;
using Windows.Foundation;

namespace PhotoLibrarian.Services;

/// <summary>Applies hue-local mixing and tonal color grading in perceptual OkLab.</summary>
internal static class ColorAdjustmentEffect
{
    private static readonly Lazy<byte[]> Shader = new(() =>
    {
        using var stream = typeof(ColorAdjustmentEffect).Assembly.GetManifestResourceStream(
            "PhotoLibrarian.Shaders.ColorAdjustments.cso")
            ?? throw new InvalidOperationException("The color adjustment shader is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    });

    internal static ICanvasImage Create(ICanvasImage source, Vector2 sourceSize, ColorAdjustments adjustments)
    {
        adjustments.Validate();
        if (adjustments.IsDefault) return source;

        var effect = new PixelShaderEffect(Shader.Value)
        {
            Source1 = source,
            Source1Mapping = SamplerCoordinateMapping.Offset,
            Source1BorderMode = EffectBorderMode.Hard,
            Source1Interpolation = CanvasImageInterpolation.Linear,
            MaxSamplerOffset = 1
        };
        for (var index = 0; index < ColorAdjustments.MixerBandCount; index++)
        {
            var band = adjustments.MixerBands[index];
            effect.Properties[$"Band{index}"] = new Vector4(
                (float)band.HueShiftDegrees,
                (float)band.Saturation,
                (float)band.Luminance,
                0);
        }
        effect.Properties["ShadowGrade"] = ToGrade(adjustments.Shadows);
        effect.Properties["MidtoneGrade"] = ToGrade(adjustments.Midtones);
        effect.Properties["HighlightGrade"] = ToGrade(adjustments.Highlights);
        effect.Properties["Blending"] = (float)adjustments.Blending;
        effect.Properties["Balance"] = (float)adjustments.Balance;

        return new CropEffect
        {
            Source = effect,
            SourceRectangle = new Rect(0, 0, sourceSize.X, sourceSize.Y)
        };
    }

    private static Vector4 ToGrade(ColorGradingWheel wheel)
    {
        var hue = (float)(wheel.HueDegrees * Math.PI / 180);
        var chroma = (float)(wheel.Amount * 0.08);
        return new Vector4(MathF.Cos(hue) * chroma, MathF.Sin(hue) * chroma, 0, 0);
    }
}
