using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Services;

/// <summary>
/// Builds a compact luminance histogram from BGRA8 pixels and applies the editor's
/// tone controls to the sampled luminance values.
/// </summary>
public static class ImageHistogram
{
    public const int BinCount = 64;

    public static float[] Calculate(byte[] bgraPixels, EditParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(bgraPixels);

        var bins = new float[BinCount];
        for (var offset = 0; offset + 3 < bgraPixels.Length; offset += 4)
        {
            var blue = bgraPixels[offset] / 255f;
            var green = bgraPixels[offset + 1] / 255f;
            var red = bgraPixels[offset + 2] / 255f;
            var luminance = (0.2126f * red) + (0.7152f * green) + (0.0722f * blue);
            var adjusted = AdjustLuminance(luminance, parameters);
            var bin = Math.Clamp((int)(adjusted * BinCount), 0, BinCount - 1);
            bins[bin]++;
        }

        var maximum = bins.Max();
        if (maximum > 0)
        {
            for (var i = 0; i < bins.Length; i++)
                bins[i] /= maximum;
        }

        return bins;
    }

    private static float AdjustLuminance(float value, EditParameters p)
    {
        value = Math.Clamp(value + (float)p.Exposure * 0.35f, 0, 1);
        value = Math.Clamp((value - 0.5f) * (1f + (float)p.Contrast) + 0.5f, 0, 1);
        value = Math.Clamp(value + (float)p.Brightness * 0.5f, 0, 1);

        var shadowWeight = 1f - value;
        var highlightWeight = value;
        value += (float)p.Shadows * shadowWeight * 0.35f;
        value += (float)p.Highlights * highlightWeight * 0.35f;

        var span = Math.Max((float)(p.WhitePoint - p.BlackPoint), 0.0001f);
        value = Math.Clamp((value - (float)p.BlackPoint) / span, 0, 1);

        var gamma = p.Midtones > 0
            ? (float)(Math.Log(0.5) / Math.Log(p.Midtones))
            : 1f;
        return Math.Clamp(MathF.Pow(value, 1f / gamma), 0, 1);
    }
}
