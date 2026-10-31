// Copyright (c) Duncan Lawler. All Rights Reserved.

namespace PhotoLibrarian.Tests;

/// <summary>
/// Small, grayscale, test-only Richardson-Lucy reference. Periodic boundaries make
/// the symmetric convolution its own adjoint; this is not an editor render path.
/// </summary>
internal static class DeblurExperiment
{
    internal const int MaximumPixels = 256 * 256;
    internal const int MaximumIterations = 20;

    internal enum Kernel { HorizontalLine, VerticalLine, Disk }

    internal static double[] Convolve(double[] source, int width, int height, Kernel kernel)
    {
        Validate(source, width, height);
        var taps = Taps(kernel);
        return ConvolveCore(source, width, height, taps);
    }

    internal static double[] Restore(
        double[] observed, int width, int height, Kernel kernel, int iterations,
        CancellationToken cancellationToken = default)
    {
        Validate(observed, width, height);
        ArgumentOutOfRangeException.ThrowIfNegative(iterations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaximumIterations);
        var taps = Taps(kernel);
        cancellationToken.ThrowIfCancellationRequested();
        var estimate = (double[])observed.Clone();
        if (iterations == 0)
            return estimate;

        for (var i = 0; i < estimate.Length; i++)
            estimate[i] = Math.Max(estimate[i], 1e-6);

        var ratio = new double[estimate.Length];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blurred = ConvolveCore(estimate, width, height, taps);
            for (var i = 0; i < ratio.Length; i++)
                ratio[i] = observed[i] / Math.Max(blurred[i], 1e-6);
            var correction = ConvolveCore(ratio, width, height, taps);
            for (var i = 0; i < estimate.Length; i++)
                estimate[i] *= correction[i];
        }

        return estimate;
    }

    internal static double[] Scene(int width, int height)
    {
        var edge = RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Edge, (uint)width, (uint)height);
        var texture = RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.FineTexture, (uint)width, (uint)height);
        var gradient = RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Gradient, (uint)width, (uint)height);
        var result = new double[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                // Flat patches, a step, texture, and a ramp in one deterministic target.
                var pixels = y < height / 2 ? edge : x < width / 2 ? texture : gradient;
                result[i] = (pixels[i * 4] + pixels[i * 4 + 1] + pixels[i * 4 + 2]) / (3.0 * 255);
            }
        }
        return result;
    }

    internal static double[] AddNoise(double[] source)
    {
        var random = new Random(217);
        return source.Select(value => Math.Clamp(value + (random.NextDouble() * 2 - 1) * 0.04, 0, 1)).ToArray();
    }

    internal static byte[] ToBgra(double[] source)
    {
        var result = new byte[source.Length * 4];
        for (var i = 0; i < source.Length; i++)
        {
            var value = (byte)Math.Round(Math.Clamp(source[i], 0, 1) * 255);
            result[i * 4] = result[i * 4 + 1] = result[i * 4 + 2] = value;
            result[i * 4 + 3] = 255;
        }
        return result;
    }

    internal static double[] FromBgra(byte[] source) =>
        Enumerable.Range(0, source.Length / 4)
            .Select(i => (source[i * 4] + source[i * 4 + 1] + source[i * 4 + 2]) / (3.0 * 255)).ToArray();

    internal static double Psnr(double[] reference, double[] result)
    {
        var error = reference.Zip(result, (a, b) => (a - b) * (a - b)).Average();
        return error == 0 ? double.PositiveInfinity : -10 * Math.Log10(error);
    }

    internal static double Ssim(double[] reference, double[] result, int width, int height)
    {
        double total = 0;
        var blocks = 0;
        for (var y = 0; y < height; y += 8)
        {
            for (var x = 0; x < width; x += 8)
            {
                var a = new List<double>();
                var b = new List<double>();
                for (var dy = y; dy < Math.Min(y + 8, height); dy++)
                {
                    for (var dx = x; dx < Math.Min(x + 8, width); dx++)
                    {
                        a.Add(reference[dy * width + dx]);
                        b.Add(result[dy * width + dx]);
                    }
                }
                var meanA = a.Average();
                var meanB = b.Average();
                var varianceA = a.Average(value => (value - meanA) * (value - meanA));
                var varianceB = b.Average(value => (value - meanB) * (value - meanB));
                var covariance = a.Zip(b, (u, v) => (u - meanA) * (v - meanB)).Average();
                total += ((2 * meanA * meanB + 0.0001) * (2 * covariance + 0.0009)) /
                    ((meanA * meanA + meanB * meanB + 0.0001) * (varianceA + varianceB + 0.0009));
                blocks++;
            }
        }
        return total / blocks;
    }

    private static (int X, int Y)[] Taps(Kernel kernel) => kernel switch
    {
        Kernel.HorizontalLine => Enumerable.Range(-3, 7).Select(x => (x, 0)).ToArray(),
        Kernel.VerticalLine => Enumerable.Range(-3, 7).Select(y => (0, y)).ToArray(),
        Kernel.Disk => DiskTaps(),
        _ => throw new ArgumentOutOfRangeException(nameof(kernel))
    };

    private static (int X, int Y)[] DiskTaps()
    {
        var taps = new List<(int, int)>();
        for (var y = -2; y <= 2; y++)
            for (var x = -2; x <= 2; x++)
                if (x * x + y * y <= 4)
                    taps.Add((x, y));
        return taps.ToArray();
    }

    private static double[] ConvolveCore(
        double[] source, int width, int height, (int X, int Y)[] taps)
    {
        var result = new double[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double sum = 0;
                foreach (var tap in taps)
                {
                    var sx = ((x + tap.X) % width + width) % width;
                    var sy = ((y + tap.Y) % height + height) % height;
                    sum += source[sy * width + sx];
                }
                result[y * width + x] = sum / taps.Length;
            }
        }
        return result;
    }

    private static void Validate(double[] source, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels ||
            (long)width * height != source.Length)
            throw new ArgumentOutOfRangeException(nameof(width), "Experiment requires 1..65536 pixels and matching dimensions.");
        if (source.Any(value => !double.IsFinite(value) || value < 0 || value > 1))
            throw new ArgumentException("Observed samples must be finite and in [0, 1].", nameof(source));
    }
}
