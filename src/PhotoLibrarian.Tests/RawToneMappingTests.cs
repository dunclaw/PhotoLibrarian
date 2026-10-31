// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Diagnostics;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class RawToneMappingTests
{
    [Fact]
    public async Task NeutralRampIsFiniteBoundedMonotonicAndMatchesReferenceTransfer()
    {
        var values = Enumerable.Range(0, 4097).Select(i => i / 1024f).ToArray();
        var source = Neutral(values);
        var result = await RawToneMappingService.RenderAsync(source, values.Length, 1, new EditParameters());
        var pixels = result.LinearRgb.ToArray();
        var bytes = result.ToPreviewBgra8();
        for (var i = 0; i < values.Length; i++)
        {
            var expected = Shoulder(values[i], 4);
            Assert.InRange(Math.Abs(pixels[i * 3] - expected), 0, 0.00001);
            Assert.InRange(Math.Abs(pixels[i * 3 + 1] - expected), 0, 0.00001);
            Assert.InRange(Math.Abs(pixels[i * 3 + 2] - expected), 0, 0.00001);
            Assert.Equal(255, bytes[i * 4 + 3]);
            if (i > 0) Assert.True(pixels[i * 3] >= pixels[(i - 1) * 3]);
        }
        Assert.Equal(255, bytes[^2]);
        var midGray = await RawToneMappingService.RenderAsync(Neutral([0.18f]), 1, 1, new EditParameters());
        Assert.Equal(109, midGray.ToPreviewBgra8()[0]);
        Assert.Equal("IEC 61966-2-1 sRGB", RawToneMappingService.OutputTransfer);
    }

    [Theory]
    [InlineData(-8, 1)]
    [InlineData(0, 4)]
    [InlineData(8, 16)]
    public async Task ExtremeFiniteSamplesRemainBoundedAndExposureIsInStops(double ev, double white)
    {
        float[] values = [0, float.Epsilon, 0.18f, 1, 2, 4, float.MaxValue];
        var result = await RawToneMappingService.RenderAsync(Neutral(values), values.Length, 1,
            new EditParameters { RawExposureEv = ev, RawWhitePoint = white });
        var pixels = result.LinearRgb.ToArray();
        for (var i = 0; i < values.Length; i++)
        {
            Assert.InRange(Math.Abs(pixels[i * 3] - Shoulder(values[i] * Math.Pow(2, ev), white)), 0, 0.00001);
            if (i > 0) Assert.True(pixels[i * 3] >= pixels[(i - 1) * 3]);
        }
        float[] signed = [-float.MaxValue, float.MaxValue, -float.MaxValue, -1, -1, -1];
        var signedResult = await RawToneMappingService.RenderAsync(signed, 2, 1, new EditParameters());
        Assert.All(signedResult.LinearRgb.ToArray(), value => Assert.True(float.IsFinite(value) && value is >= 0 and <= 1));
    }

    [Fact]
    public async Task UnclippedHeadroomSurvivesUntilShoulderButIdenticalClippedSamplesCannotBeRecovered()
    {
        var source = Neutral([1, 2, 3, 4, 4]);
        var before = source.ToArray();
        var result = await RawToneMappingService.RenderAsync(source, 5, 1,
            new EditParameters { RawExposureEv = -1 });
        var pixels = result.LinearRgb.ToArray();
        Assert.True(pixels[0] < pixels[3] && pixels[3] < pixels[6] && pixels[6] < pixels[9]);
        Assert.Equal(pixels[9], pixels[12]);
        Assert.Equal(before, source);
    }

    [Fact]
    public async Task ColorChartUsesPinnedD65MatrixAndClipsGamutOnlyAfterLuminanceMapping()
    {
        float[] chart =
        [
            0.18f, 0.18f, 0.18f, 0.3f, 0.2f, 0.1f, 0.1f, 0.3f, 0.2f,
            0.2f, 0.1f, 0.3f, 1, 0, 0, 0, 1, 0, 0, 0, 1, 2, 1.5f, 1
        ];
        var result = await RawToneMappingService.RenderAsync(chart, 8, 1, new EditParameters());
        var pixels = result.LinearRgb.ToArray();
        for (var i = 0; i < chart.Length; i += 3)
        {
            var expected = ConvertPixel(chart[i], chart[i + 1], chart[i + 2],
                Shoulder(Luminance(chart[i], chart[i + 1], chart[i + 2]), 4));
            for (var channel = 0; channel < 3; channel++)
                Assert.InRange(Math.Abs(pixels[i + channel] - expected[channel]), 0, 0.000001);
        }
        Assert.All(pixels, value => Assert.True(float.IsFinite(value) && value is >= 0 and <= 1));
        Assert.Equal(0, pixels[4 * 3 + 1]); // Out-of-sRGB ProPhoto red is clipped, not silently hue-reconstructed.
    }

    [Fact]
    public async Task RejectsMalformedRecipeGeometryAndSamplesExplicitlyAndHonorsCancellation()
    {
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, -9, 9 })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RawToneMappingService.RenderAsync(
                Neutral([1]), 1, 1, new EditParameters { RawExposureEv = bad }));
        foreach (var bad in new[] { double.NaN, double.NegativeInfinity, 0, 17 })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RawToneMappingService.RenderAsync(
                Neutral([1]), 1, 1, new EditParameters { RawWhitePoint = bad }));
        await Assert.ThrowsAsync<ArgumentException>(() => RawToneMappingService.RenderAsync(
            Neutral([1]), 2, 1, new EditParameters()));
        await Assert.ThrowsAsync<ArgumentException>(() => RawToneMappingService.RenderAsync(
            Neutral([1]), 6001, 4000, new EditParameters()));
        foreach (var bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            await Assert.ThrowsAsync<InvalidDataException>(() => RawToneMappingService.RenderAsync(
                Neutral([bad]), 1, 1, new EditParameters()));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RawToneMappingService.RenderAsync(
            Neutral([1]), 1, 1, new EditParameters(), cancelled.Token));
    }

    [Fact]
    public async Task PinnedLibRawForwardMatrixRoundTripsKnownSrgbPatchesWithoutSecondAdaptation()
    {
        double[][] patches = [[0.18, 0.18, 0.18], [0.5, 0.2, 0.1], [0.1, 0.5, 0.2], [0.2, 0.1, 0.5]];
        var input = new float[patches.Length * 3];
        for (var i = 0; i < patches.Length; i++)
        {
            var p = patches[i];
            // Independent forward coefficients from pinned src/tables/colorconst.cpp.
            input[i * 3] = (float)(0.529317 * p[0] + 0.330092 * p[1] + 0.140588 * p[2]);
            input[i * 3 + 1] = (float)(0.098368 * p[0] + 0.873465 * p[1] + 0.028169 * p[2]);
            input[i * 3 + 2] = (float)(0.016879 * p[0] + 0.117663 * p[1] + 0.865457 * p[2]);
        }
        var result = await RawToneMappingService.RenderAsync(input, patches.Length, 1, new EditParameters());
        var pixels = result.LinearRgb.ToArray();
        for (var i = 0; i < patches.Length; i++)
        {
            var p = patches[i];
            var y = 0.2126729 * p[0] + 0.7151522 * p[1] + 0.0721750 * p[2];
            for (var channel = 0; channel < 3; channel++)
                Assert.InRange(Math.Abs(pixels[i * 3 + channel] - p[channel] * Shoulder(y, 4) / y), 0, 0.000001);
        }
    }

    [Fact]
    public async Task CancellationBoundsActiveMappingAndEncoding()
    {
        const int width = 2048, height = 1536;
        var source = new float[width * height * 3];
        Array.Fill(source, 0.18f);
        using var mapCancellation = new CancellationTokenSource();
        mapCancellation.CancelAfter(1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RawToneMappingService.RenderAsync(source, width, height, new EditParameters(), mapCancellation.Token));
        var result = await RawToneMappingService.RenderAsync(source, width, height, new EditParameters());
        using var encodeCancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var encoding = Task.Run(() =>
        {
            started.SetResult();
            return result.ToPreviewBgra8(encodeCancellation.Token);
        });
        await started.Task;
        encodeCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => encoding);
    }

    [Fact]
    public async Task RasterParametersNeverChangeRawReferenceAndRepeatedRenderingIsDeterministic()
    {
        var source = Neutral([0, 0.01f, 0.18f, 1, 2, 4]);
        var baseline = await RawToneMappingService.RenderAsync(source, 6, 1, new EditParameters());
        var raster = await RawToneMappingService.RenderAsync(source, 6, 1, new EditParameters
        {
            Exposure = 1, Contrast = 0.5, NoiseReduction = 1, Sharpness = 1, RotationAngle = 90,
            Temperature = 1, WhitePoint = 0.5
        });
        Assert.Equal(6, raster.Width);
        Assert.Equal(1, raster.Height);
        Assert.Equal(baseline.LinearRgb.ToArray(), raster.LinearRgb.ToArray());
        Assert.Equal(baseline.ToPreviewBgra8(), raster.ToPreviewBgra8());
    }

    [Fact]
    public async Task CompareGlobalWithEdgeAwareLogBaseDetailOnGeneratedAnalysisFixtures()
    {
        const int width = 128, height = 64;
        foreach (var name in new[] { "highlights", "ramp", "color-chart", "texture", "shadows", "edge" })
        {
            var source = Fixture(name, width, height);
            var watch = Stopwatch.StartNew();
            var global = await RawToneMappingService.RenderAsync(source, width, height, new EditParameters());
            watch.Stop();
            var globalMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            var local = LocalCandidate(source, width, height);
            watch.Stop();
            var globalValues = global.LinearRgb.ToArray();
            var globalStats = Statistics(globalValues);
            var localStats = Statistics(local);
            var difference = Math.Sqrt(globalValues.Zip(local, (a, b) => (double)(a - b) * (a - b)).Average());
            Console.WriteLine($"RAW analysis {name}: global/local ms={globalMs:F3}/{watch.Elapsed.TotalMilliseconds:F3}; " +
                $"range={globalStats.Range:F6}/{localStats.Range:F6}; stddev={globalStats.Deviation:F6}/{localStats.Deviation:F6}; " +
                $"local-v-global linear RGB RMS={difference:F6}");
            Assert.All(local, value => Assert.True(float.IsFinite(value) && value is >= 0 and <= 1));
            if (name == "edge")
            {
                var near = (height / 2 * width + width / 2 - 1) * 3;
                var far = (height / 2 * width + width / 4) * 3;
                var globalHalo = Math.Abs(globalValues[near] - globalValues[far]);
                var localHalo = Math.Abs(local[near] - local[far]);
                Console.WriteLine($"RAW edge near/far dark-side deviation: global={globalHalo:F6}, local={localHalo:F6}");
                Assert.Equal(0, globalHalo);
                Assert.True(localHalo > 0.001);
            }
            if (name == "shadows" || name == "texture")
                Assert.True(localStats.Deviation > globalStats.Deviation);
        }
    }

    [Theory]
    [InlineData(1024, 768)]
    [InlineData(4000, 3000)]
    [InlineData(6000, 4000)]
    public async Task ReportsFullResolutionLatencyAndSampledPeakWorkingSet(int width, int height)
    {
        // Warm JIT/worker before measuring. Input stays alive throughout mapping and preview encoding.
        await RawToneMappingService.RenderAsync(Neutral([0.18f]), 1, 1, new EditParameters());
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var start = process.WorkingSet64;
        var peak = start;
        using var stop = new CancellationTokenSource();
        var sample = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                process.Refresh();
                peak = Math.Max(peak, process.WorkingSet64);
                await Task.Delay(2);
            }
        });
        var source = new float[width * height * 3];
        Array.Fill(source, 0.18f);
        var watch = Stopwatch.StartNew();
        var result = await RawToneMappingService.RenderAsync(source, width, height, new EditParameters());
        var renderMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var bytes = result.ToPreviewBgra8();
        watch.Stop();
        stop.Cancel();
        await sample;
        process.Refresh();
        peak = Math.Max(peak, process.WorkingSet64);
        Console.WriteLine($"RAW FP32 {width}x{height}: map={renderMs:F1} ms; encode={watch.Elapsed.TotalMilliseconds:F1} ms; " +
            $"sampled peak process working set={peak / (1024d * 1024):F1} MiB; growth={(peak - start) / (1024d * 1024):F1} MiB; " +
            $"live frame payload={(source.LongLength * 4 + result.LinearRgb.Length * 4L + bytes.LongLength) / (1024d * 1024):F1} MiB.");
        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.Equal(width * height * 4, bytes.Length);
        GC.KeepAlive(source);
        GC.KeepAlive(result);
    }

    private static float[] Neutral(float[] values)
    {
        var result = new float[values.Length * 3];
        for (var i = 0; i < values.Length; i++)
            result[i * 3] = result[i * 3 + 1] = result[i * 3 + 2] = values[i];
        return result;
    }

    private static double Shoulder(double luminance, double white) =>
        luminance >= white ? 1 : luminance * (1 + luminance / (white * white)) / (1 + luminance);

    private static double Luminance(double r, double g, double b) =>
        0.26836494690413276 * r + 0.71510893400282061 * g + 0.016525610495629867 * b;

    private static float[] ConvertPixel(double r, double g, double b, double mapped)
    {
        var y = Luminance(r, g, b);
        if (y <= 0) return [0, 0, 0];
        r = r / y * mapped; g = g / y * mapped; b = b / y * mapped;
        return
        [
            (float)Math.Clamp(2.0341926072954801 * r - 0.72741981962025604 * g - 0.30676553702329973 * b, 0, 1),
            (float)Math.Clamp(-0.2288107628343688 * r + 1.231729226050609 * g - 0.0029216160285968689 * b, 0, 1),
            (float)Math.Clamp(-0.0085649272363156857 * r - 0.15327259100038765 * g + 1.1618389609260644 * b, 0, 1)
        ];
    }

    private static float[] Fixture(string name, int width, int height)
    {
        var result = new float[width * height * 3];
        var random = new Random(113);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = name switch
                {
                    "highlights" => x < width / 3 ? 1f : x < width * 2 / 3 ? 2f : 4f,
                    "ramp" => x / (width - 1f) * 4,
                    "color-chart" => 0.3f,
                    "texture" => 0.18f + ((x + y) % 2 == 0 ? -0.03f : 0.03f),
                    "shadows" => 0.01f + (float)(random.NextDouble() - 0.5) * 0.004f,
                    "edge" => x < width / 2 ? 0.18f : 0.7f,
                    _ => throw new ArgumentException(name)
                };
                var i = (y * width + x) * 3;
                result[i] = value;
                result[i + 1] = name == "color-chart" ? (x / 16 % 3 + 1) * 0.1f : value;
                result[i + 2] = name == "color-chart" ? (y / 16 % 3 + 1) * 0.1f : value;
            }
        }
        return result;
    }

    // Analysis-only alternative: 7x7 bilateral log-luminance base (sigma 2 pixels,
    // 0.75 natural-log units), same shoulder on the base, unit-gain log detail.
    // Deliberately not a second shipping mode.
    private static float[] LocalCandidate(float[] source, int width, int height)
    {
        var logs = new double[width * height];
        for (var i = 0; i < logs.Length; i++)
            logs[i] = Math.Log(Math.Max(1e-8, Luminance(source[i * 3], source[i * 3 + 1], source[i * 3 + 2])));
        var output = new float[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                double weighted = 0, weightSum = 0;
                for (var dy = -3; dy <= 3; dy++)
                {
                    for (var dx = -3; dx <= 3; dx++)
                    {
                        var neighbor = Math.Clamp(y + dy, 0, height - 1) * width + Math.Clamp(x + dx, 0, width - 1);
                        var delta = logs[neighbor] - logs[index];
                        var weight = Math.Exp(-(dx * dx + dy * dy) / 8.0 - delta * delta / (2 * 0.75 * 0.75));
                        weighted += logs[neighbor] * weight;
                        weightSum += weight;
                    }
                }
                var baseLog = weighted / weightSum;
                var mapped = Math.Clamp(Shoulder(Math.Exp(baseLog), 4) * Math.Exp(logs[index] - baseLog), 0, 1);
                var pixel = ConvertPixel(source[index * 3], source[index * 3 + 1], source[index * 3 + 2], mapped);
                pixel.CopyTo(output, index * 3);
            }
        }
        return output;
    }

    private static (double Range, double Deviation) Statistics(float[] pixels)
    {
        var mean = pixels.Average(value => (double)value);
        var variance = pixels.Average(value => ((double)value - mean) * (value - mean));
        return (pixels.Max() - pixels.Min(), Math.Sqrt(variance));
    }
}
