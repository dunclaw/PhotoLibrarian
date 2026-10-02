using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Services;

/// <summary>
/// Derives conservative editor adjustments from the image's luminance and color distributions.
/// </summary>
public static class AutoEnhanceAnalyzer
{
    private const int HistogramBinCount = 256;
    private const int MaximumSamples = 1_000_000;

    public static EditParameters Analyze(byte[] bgraPixels)
    {
        ArgumentNullException.ThrowIfNull(bgraPixels);
        if (bgraPixels.Length % 4 != 0)
            throw new ArgumentException("Pixel data must contain complete BGRA pixels.", nameof(bgraPixels));

        var pixelCount = bgraPixels.Length / 4;
        if (pixelCount == 0)
            throw new ArgumentException("At least one pixel is required.", nameof(bgraPixels));

        var sampleStep = Math.Max(1, (pixelCount + MaximumSamples - 1) / MaximumSamples);
        var luminanceHistogram = new int[HistogramBinCount];
        var sampleCount = 0;
        var averageSaturation = 0d;
        var averageChroma = 0d;
        var colorSampleCount = 0;
        var localContrastTotal = 0d;
        var localContrastCount = 0;

        for (var pixel = 0; pixel < pixelCount; pixel += sampleStep)
        {
            var offset = pixel * 4;
            if (bgraPixels[offset + 3] == 0)
                continue;

            var blue = bgraPixels[offset] / 255d;
            var green = bgraPixels[offset + 1] / 255d;
            var red = bgraPixels[offset + 2] / 255d;
            var luminance = (0.2126d * red) + (0.7152d * green) + (0.0722d * blue);
            var bin = Math.Clamp((int)Math.Round(luminance * (HistogramBinCount - 1)), 0, HistogramBinCount - 1);
            luminanceHistogram[bin]++;
            sampleCount++;

            if (pixel + 1 < pixelCount)
            {
                var nextOffset = offset + 4;
                if (bgraPixels[nextOffset + 3] != 0)
                {
                    var nextBlue = bgraPixels[nextOffset] / 255d;
                    var nextGreen = bgraPixels[nextOffset + 1] / 255d;
                    var nextRed = bgraPixels[nextOffset + 2] / 255d;
                    var nextLuminance = (0.2126d * nextRed) + (0.7152d * nextGreen) + (0.0722d * nextBlue);
                    localContrastTotal += Math.Abs(luminance - nextLuminance);
                    localContrastCount++;
                }
            }

            var maximum = Math.Max(red, Math.Max(green, blue));
            var minimum = Math.Min(red, Math.Min(green, blue));
            if (maximum > 0)
            {
                averageSaturation += (maximum - minimum) / maximum;
                averageChroma += maximum - minimum;
                colorSampleCount++;
            }
        }

        if (sampleCount == 0)
            return new EditParameters();

        var low = Percentile(luminanceHistogram, sampleCount, 0.10);
        var median = Percentile(luminanceHistogram, sampleCount, 0.50);
        var high = Percentile(luminanceHistogram, sampleCount, 0.90);
        var range = high - low;

        var exposureStops = Math.Clamp(Math.Log2(0.45 / Math.Max(median, 1d / 255d)), -0.4, 0.4);
        if (high > 0)
            exposureStops = Math.Min(exposureStops, Math.Log2(0.995 / high));

        var contrast = Math.Clamp((0.78 - range) * 0.35, -0.08, 0.14);
        var shadows = Math.Clamp((0.12 - low) * 0.5, 0, 0.12);
        var highlights = -Math.Clamp((high - 0.88) * 0.6, 0, 0.12);
        var meanLocalContrast = localContrastCount == 0 ? 0 : localContrastTotal / localContrastCount;
        var clarity = Math.Clamp((0.045 - meanLocalContrast) * 1.5, 0, 0.10);
        var meanChroma = colorSampleCount == 0 ? 0 : averageChroma / colorSampleCount;
        var saturation = meanChroma < 0.02
            ? 0
            : Math.Clamp((0.32 - averageSaturation / colorSampleCount) * 0.25, 0, 0.08);

        return new EditParameters
        {
            Exposure = exposureStops / 2,
            Contrast = contrast,
            Highlights = highlights,
            Shadows = shadows,
            Saturation = saturation,
            Clarity = clarity
        };
    }

    private static double Percentile(int[] histogram, int sampleCount, double percentile)
    {
        var target = (int)Math.Ceiling(sampleCount * percentile);
        var cumulative = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            cumulative += histogram[i];
            if (cumulative >= target)
                return i / (double)(HistogramBinCount - 1);
        }

        return 1;
    }
}
