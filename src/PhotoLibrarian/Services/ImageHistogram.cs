// Copyright (c) Duncan Lawler. All Rights Reserved.
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
        var curves = parameters.ToneCurves;
        var hasCurves = !curves.IsIdentity;
        float[]? redCurve = null, greenCurve = null, blueCurve = null;
        if (hasCurves)
        {
            curves.Validate();
            float[] MakeTable(IReadOnlyList<ToneCurvePoint> channel)
            {
                var table = new float[256];
                for (var i = 0; i < table.Length; i++)
                {
                    var value = AdjustLuminance(i / 255f, parameters);
                    table[i] = (float)ToneCurves.Evaluate(channel, ToneCurves.Evaluate(curves.Rgb, value));
                }
                return table;
            }
            redCurve = MakeTable(curves.Red);
            greenCurve = MakeTable(curves.Green);
            blueCurve = MakeTable(curves.Blue);
        }
        // The histogram is a preview, not an image-processing limit. Sample across large
        // sources so a pointer drag does not scan tens of millions of pixels on the UI thread.
        var pixelCount = bgraPixels.Length / 4;
        var stride = Math.Max(1, (int)Math.Ceiling(pixelCount / 262144d));
        for (var pixel = 0; pixel < pixelCount; pixel += stride)
        {
            var offset = pixel * 4;
            var blue = bgraPixels[offset] / 255f;
            var green = bgraPixels[offset + 1] / 255f;
            var red = bgraPixels[offset + 2] / 255f;
            float adjusted;
            if (hasCurves)
            {
                adjusted = 0.2126f * redCurve![bgraPixels[offset + 2]] +
                    0.7152f * greenCurve![bgraPixels[offset + 1]] +
                    0.0722f * blueCurve![bgraPixels[offset]];
            }
            else
                adjusted = AdjustLuminance((0.2126f * red) + (0.7152f * green) + (0.0722f * blue), parameters);
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
