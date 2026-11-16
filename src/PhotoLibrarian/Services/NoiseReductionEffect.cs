using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using System.Numerics;
using Windows.Foundation;

namespace PhotoLibrarian.Services;

/// <summary>A bounded 5-by-5 bilateral filter, evaluated in source pixels before tone and sharpening.</summary>
public static class NoiseReductionEffect
{
    private static readonly Lazy<byte[]> Shader = new(() =>
    {
        using var stream = typeof(NoiseReductionEffect).Assembly.GetManifestResourceStream(
            "PhotoLibrarian.Shaders.NoiseReduction.cso")
            ?? throw new InvalidOperationException("The noise reduction shader is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    });

    public static ICanvasImage Create(ICanvasImage source, Vector2 sourceSize, double amount)
    {
        if (!double.IsFinite(amount))
            throw new ArgumentOutOfRangeException(nameof(amount), "Noise reduction must be finite.");

        var strength = (float)Math.Clamp(amount, 0, 1);
        if (strength == 0) return source;

        var effect = new PixelShaderEffect(Shader.Value)
        {
            Source1 = source,
            Source1Mapping = SamplerCoordinateMapping.Offset,
            Source1BorderMode = EffectBorderMode.Hard,
            Source1Interpolation = CanvasImageInterpolation.NearestNeighbor,
            MaxSamplerOffset = 2
        };
        effect.Properties["Strength"] = strength;
        return new CropEffect
        {
            Source = effect,
            SourceRectangle = new Rect(0, 0, sourceSize.X, sourceSize.Y)
        };
    }
}
