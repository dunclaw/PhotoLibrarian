// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using Windows.Graphics.DirectX;
using Xunit;

namespace PhotoLibrarian.Tests;

[Collection(WaveletNoiseReductionExperimentCollection.Name)]
public sealed class MultiscaleLocalContrastExperimentTests
{
    [Fact]
    public async Task CandidateAndClarityAreComparedAcrossGeneratedDetailFixtures()
    {
        const int width = 192;
        const int height = 128;
        const double clarityStrength = 0.5;
        const double candidateStrength = 1.0;
        using var device = CanvasDevice.GetSharedDevice();
        var outputDirectory = Environment.GetEnvironmentVariable(
            "PHOTO_LIBRARIAN_LOCAL_CONTRAST_OUTPUT_DIR");
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
        var report = new System.Text.StringBuilder();

        foreach (var kind in RenderQualityFixtures.Kinds)
        {
            if (kind == RenderQualityFixtureKind.KnownBlur)
                continue;

            var input = RenderQualityFixtures.CreatePixels(
                kind,
                width,
                height,
                alpha: false);
            using var source = CreateBitmap(device, input, width, height);
            var sourcePixels = source.GetPixelBytes();
            var clarity = Render(
                device,
                source,
                width,
                height,
                EditEffectGraph.Build(
                    source,
                    new(width, height),
                    new EditParameters { Clarity = clarityStrength }));
            var candidate = Render(
                device,
                source,
                width,
                height,
                MultiscaleLocalContrastExperiment.Create(source, new(width, height), candidateStrength));

            var alphaDrift = MaxAlphaDelta(sourcePixels, candidate);
            var measurement =
                $"{kind} (Clarity {clarityStrength:F2}, candidate max {candidateStrength:F2}): " +
                $"high-pass RMS clarity/candidate=" +
                $"{HighPassRms(clarity, width, height):F3}/{HighPassRms(candidate, width, height):F3}; " +
                $"clarity change={MeanAbsoluteRgbDifference(sourcePixels, clarity):F3}; " +
                $"candidate change={MeanAbsoluteRgbDifference(sourcePixels, candidate):F3}; " +
                $"chroma delta clarity={MeanChromaDelta(sourcePixels, clarity):F3}; " +
                $"candidate={MeanChromaDelta(sourcePixels, candidate):F3}; " +
                $"max alpha delta={alphaDrift}; " +
                $"clipped RGB input/clarity/candidate=" +
                $"{CountClipped(sourcePixels)}/{CountClipped(clarity)}/{CountClipped(candidate)}";
            report.AppendLine(measurement);
            TestContext.Current.TestOutputHelper?.WriteLine(measurement);
            if (kind == RenderQualityFixtureKind.FlatNoise)
            {
                var noiseReport =
                    $"Flat luminance standard deviation (byte scale): " +
                    $"input={LuminanceStandardDeviation(sourcePixels):F2}; " +
                    $"clarity={LuminanceStandardDeviation(clarity):F2}; " +
                    $"candidate={LuminanceStandardDeviation(candidate):F2}";
                report.AppendLine(noiseReport);
                TestContext.Current.TestOutputHelper?.WriteLine(noiseReport);
            }
            if (kind == RenderQualityFixtureKind.Edge)
            {
                var haloReport =
                    $"Step-edge halo overshoot (byte scale): " +
                    $"input={EdgeHaloMagnitude(sourcePixels, width, height)}; " +
                    $"clarity={EdgeHaloMagnitude(clarity, width, height)}; " +
                    $"candidate={EdgeHaloMagnitude(candidate, width, height)}";
                report.AppendLine(haloReport);
                TestContext.Current.TestOutputHelper?.WriteLine(haloReport);
            }
            if (kind == RenderQualityFixtureKind.DetailScene)
            {
                for (var section = 0; section < 4; section++)
                {
                    var left = section * (width / 4);
                    var right = section == 3 ? width : (section + 1) * (width / 4);
                    var sectionReport =
                        $"Detail section {section + 1} high-pass RMS: " +
                        $"clarity={HighPassRms(clarity, width, height, left, right):F3}; " +
                        $"candidate={HighPassRms(candidate, width, height, left, right):F3}";
                    report.AppendLine(sectionReport);
                    TestContext.Current.TestOutputHelper?.WriteLine(sectionReport);
                }
            }

            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                var comparison = JoinHorizontally(sourcePixels, clarity, candidate, width, height);
                await RenderQualityFixtures.WriteImageAsync(
                    Path.Combine(outputDirectory, $"{kind}.png"),
                    width * 3,
                    height,
                    comparison);
            }
        }
        using var alphaSource = CreateBitmap(
            device,
            RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.DetailScene, 11, 9, alpha: true),
            11,
            9);
        var alphaInput = alphaSource.GetPixelBytes();
        var alphaCandidate = Render(
            device,
            alphaSource,
            11,
            9,
            MultiscaleLocalContrastExperiment.Create(alphaSource, new(11, 9), candidateStrength));
        var alphaFixtureDrift = MaxAlphaDelta(alphaInput, alphaCandidate);
        report.AppendLine($"Transparent fixture maximum alpha delta: {alphaFixtureDrift}");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Transparent fixture maximum alpha delta: {alphaFixtureDrift}");
        WriteReport(outputDirectory, "quality-metrics.txt", report.ToString());
        Assert.Same(alphaSource, MultiscaleLocalContrastExperiment.Create(alphaSource, new(11, 9), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MultiscaleLocalContrastExperiment.Create(alphaSource, new(11, 9), double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MultiscaleLocalContrastExperiment.Create(alphaSource, new(0, 9), 0.5));
    }

    [Fact]
    public void SourceResolutionRenderCostsAreMeasured()
    {
        using var device = CanvasDevice.GetSharedDevice();
        var outputDirectory = Environment.GetEnvironmentVariable(
            "PHOTO_LIBRARIAN_LOCAL_CONTRAST_OUTPUT_DIR");
        var report = new System.Text.StringBuilder();
        foreach (var (width, height) in new[] { (1024, 1024), (4000, 3000), (6000, 4000) })
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var pixels = new byte[checked(width * height * 4)];
            for (var index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = 128;
                pixels[index + 1] = 128;
                pixels[index + 2] = 128;
                pixels[index + 3] = 255;
            }

            using var source = CreateBitmap(device, pixels, width, height);
            report.Append(Measure(device, source, width, height, "Clarity", () =>
                EditEffectGraph.Build(
                    source,
                    new(width, height),
                    new EditParameters { Clarity = 0.5 })));
            report.Append(Measure(device, source, width, height, "Multiscale", () =>
                MultiscaleLocalContrastExperiment.Create(source, new(width, height), 0.5)));
        }
        WriteReport(outputDirectory, "performance-metrics.txt", report.ToString());
    }

    private static string Measure(
        CanvasDevice device,
        CanvasBitmap source,
        int width,
        int height,
        string name,
        Func<ICanvasImage> createGraph)
    {
        var graph = createGraph();
        try
        {
            using (var warmup = new CanvasRenderTarget(device, width, height, 96))
            {
                using (var session = warmup.CreateDrawingSession())
                    session.DrawImage(graph);
                _ = warmup.GetPixelBytes();
            }

            var timings = new List<double>(3);
            for (var iteration = 0; iteration < 3; iteration++)
            {
                using var target = new CanvasRenderTarget(device, width, height, 96);
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                using (var session = target.CreateDrawingSession())
                    session.DrawImage(graph);
                _ = target.GetPixelBytes();
                timings.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            var median = timings.Order().ElementAt(timings.Count / 2);
            var workingSetMb = Environment.WorkingSet / 1_000_000.0;
            var measurement =
                $"{name} {width}x{height}: warm render/readback median {median:F1} ms; " +
                $"process working set {workingSetMb:F0} MB";
            TestContext.Current.TestOutputHelper?.WriteLine(measurement);
            return measurement + Environment.NewLine;
        }
        finally
        {
            if (!ReferenceEquals(graph, source))
                ((IDisposable)graph).Dispose();
        }
    }

    private static CanvasBitmap CreateBitmap(CanvasDevice device, byte[] pixels, int width, int height) =>
        CanvasBitmap.CreateFromBytes(
            device, pixels, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);

    private static byte[] Render(
        CanvasDevice device,
        CanvasBitmap source,
        int width,
        int height,
        ICanvasImage graph)
    {
        try
        {
            using var target = new CanvasRenderTarget(device, width, height, 96);
            using (var session = target.CreateDrawingSession())
                session.DrawImage(graph);
            return target.GetPixelBytes();
        }
        finally
        {
            if (!ReferenceEquals(graph, source))
                ((IDisposable)graph).Dispose();
        }
    }

    private static int MaxAlphaDelta(byte[] input, byte[] output)
    {
        var maximum = 0;
        for (var index = 3; index < input.Length; index += 4)
            maximum = Math.Max(maximum, Math.Abs(input[index] - output[index]));
        return maximum;
    }

    private static double HighPassRms(
        byte[] image, int width, int height, int left = 0, int right = 0)
    {
        right = right == 0 ? width : right;
        double squaredSum = 0;
        var sampleCount = 0;
        const int radius = 4;
        for (var y = radius; y < height - radius; y++)
        for (var x = Math.Max(left, radius); x < Math.Min(right, width - radius); x++)
        {
            var center = Luminance(image, PixelIndex(x, y, width));
            double neighborhood = 0;
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
                neighborhood += Luminance(image, PixelIndex(x + dx, y + dy, width));

            var detail = center - neighborhood / ((radius * 2 + 1) * (radius * 2 + 1));
            squaredSum += detail * detail;
            sampleCount++;
        }

        return Math.Sqrt(squaredSum / sampleCount);
    }

    private static double MeanAbsoluteRgbDifference(byte[] input, byte[] output)
    {
        double sum = 0;
        for (var index = 0; index < input.Length; index += 4)
        for (var channel = 0; channel < 3; channel++)
            sum += Math.Abs(input[index + channel] - output[index + channel]);
        return sum / (input.Length / 4 * 3);
    }

    private static double MeanChromaDelta(byte[] input, byte[] output)
    {
        double sum = 0;
        for (var index = 0; index < input.Length; index += 4)
        {
            var inputRedGreen = input[index + 2] - input[index + 1];
            var outputRedGreen = output[index + 2] - output[index + 1];
            var inputGreenBlue = input[index + 1] - input[index];
            var outputGreenBlue = output[index + 1] - output[index];
            sum += Math.Abs(inputRedGreen - outputRedGreen);
            sum += Math.Abs(inputGreenBlue - outputGreenBlue);
        }

        return sum / (input.Length / 2);
    }

    private static double LuminanceStandardDeviation(byte[] image)
    {
        double sum = 0;
        double squaredSum = 0;
        var pixelCount = image.Length / 4;
        for (var index = 0; index < image.Length; index += 4)
        {
            var value = image[index + 2] * 0.2126 + image[index + 1] * 0.7152 + image[index] * 0.0722;
            sum += value;
            squaredSum += value * value;
        }

        var mean = sum / pixelCount;
        return Math.Sqrt(Math.Max(0, squaredSum / pixelCount - mean * mean));
    }

    private static int EdgeHaloMagnitude(byte[] image, int width, int height)
    {
        var maximum = 0;
        var edge = width / 2;
        for (var y = 0; y < height; y++)
        for (var x = Math.Max(0, edge - 16); x < Math.Min(width, edge + 16); x++)
        {
            var value = image[PixelIndex(x, y, width)];
            var overshoot = x < edge ? value - 24 : 232 - value;
            maximum = Math.Max(maximum, overshoot);
        }

        return maximum;
    }

    private static int CountClipped(byte[] image)
    {
        var count = 0;
        for (var index = 0; index < image.Length; index += 4)
        for (var channel = 0; channel < 3; channel++)
        {
            if (image[index + channel] == 255)
                count++;
        }

        return count;
    }

    private static double Luminance(byte[] image, int index) =>
        (0.2126 * image[index + 2] + 0.7152 * image[index + 1] + 0.0722 * image[index]) / 255.0;

    private static int PixelIndex(int x, int y, int width) => (y * width + x) * 4;

    private static byte[] JoinHorizontally(
        byte[] first, byte[] second, byte[] third, int width, int height)
    {
        var output = new byte[checked(width * 3 * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var sourceIndex = PixelIndex(x, y, width);
            var outputIndex = PixelIndex(x, y, width * 3);
            Array.Copy(first, sourceIndex, output, outputIndex, 4);
            Array.Copy(second, sourceIndex, output, outputIndex + width * 4, 4);
            Array.Copy(third, sourceIndex, output, outputIndex + width * 8, 4);
        }

        return output;
    }

    private static void WriteReport(string? outputDirectory, string fileName, string contents)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            return;

        File.WriteAllText(Path.Combine(outputDirectory, fileName), contents);
    }
}
