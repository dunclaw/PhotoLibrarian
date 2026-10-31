// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using System.Numerics;
using Windows.Graphics.DirectX;
using Xunit;

namespace PhotoLibrarian.Tests;

[Collection(WaveletNoiseReductionExperimentCollection.Name)]
public sealed class WaveletNoiseReductionExperimentTests
{
    [Fact]
    public void NeutralBypassesAndStrengthIsBounded()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateBitmap(device, new byte[4], 1, 1);
        Assert.Same(source, WaveletNoiseReductionExperiment.Create(source, new(1, 1), 0));
        Assert.Same(source, WaveletNoiseReductionExperiment.Create(source, new(1, 1), -1));

        using var graph = (CropEffect)WaveletNoiseReductionExperiment.Create(source, new(1, 1), 2);
        Assert.Equal(0.06f, Assert.IsType<PixelShaderEffect>(graph.Source).Properties["Threshold"]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WaveletNoiseReductionExperiment.Create(source, new(1, 1), double.NaN));
    }

    [Fact]
    public void AlphaAndImageBoundsArePreserved()
    {
        const int width = 8;
        const int height = 7;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var alpha = (byte)((x + y) % 3 == 0 ? 0 : (x + y) % 3 == 1 ? 128 : 255);
            var value = (byte)(x * 31);
            var index = (y * width + x) * 4;
            pixels[index] = (byte)(value * alpha / 255);
            pixels[index + 1] = (byte)(value * alpha / 255);
            pixels[index + 2] = (byte)(value * alpha / 255);
            pixels[index + 3] = alpha;
        }

        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = CreateBitmap(device, pixels, width, height);
        using var graph = (CropEffect)WaveletNoiseReductionExperiment.Create(
            source, new(width, height), 0.75);
        Assert.Equal(new Windows.Foundation.Rect(0, 0, width, height), graph.GetBounds(device));
        var output = Render(device, source, width, height, graph);
        for (var pixel = 0; pixel < width * height; pixel++)
            Assert.Equal(pixels[pixel * 4 + 3], output[pixel * 4 + 3]);
    }

    [Fact]
    public void WaveletCandidateIsComparedWithBilateralAtMatchedFlatNoiseReduction()
    {
        const int width = 96;
        const int height = 72;
        var (clean, noisy) = CreateNoisyTexture(width, height);
        using var device = new CanvasDevice();
        using var source = CreateBitmap(device, noisy, width, height);
        var inputNoiseError = FlatError(noisy, clean, width, height);
        var candidateResults = new List<(double Strength, byte[] Pixels, double Error)>();
        var nonLocalMeansResults = new List<(double Strength, byte[] Pixels, double Error)>();
        var baselineResults = new List<(double Strength, byte[] Pixels, double Error)>();
        var report = new System.Text.StringBuilder();

        for (var step = 1; step <= 100; step++)
        {
            var strength = step / 100.0;
            var bilateral = Render(device, source, width, height,
                EditEffectGraph.Build(source, new(width, height), new EditParameters
                {
                    NoiseReduction = strength
                }));
            baselineResults.Add((strength, bilateral, FlatError(bilateral, clean, width, height)));
        }

        for (var step = 1; step <= 10; step++)
        {
            var strength = step / 10.0;
            var wavelet = Render(device, source, width, height,
                WaveletNoiseReductionExperiment.Create(source, new(width, height), strength));
            candidateResults.Add((strength, wavelet, FlatError(wavelet, clean, width, height)));
        }

        foreach (var strength in new[] { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 })
        {
            var nonLocalMeans = ApplyNonLocalMeans(noisy, width, height, strength);
            nonLocalMeansResults.Add((
                strength, nonLocalMeans, FlatError(nonLocalMeans, clean, width, height)));
        }

        report.AppendLine($"Input flat-region MSE: {inputNoiseError:F3}");
        AppendMatchedResults(report, "Wavelet", candidateResults, baselineResults, clean, width, height);
        AppendMatchedResults(
            report, "Non-local means", nonLocalMeansResults, baselineResults, clean, width, height);
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());

        AssertMatchedCandidates(candidateResults, baselineResults, inputNoiseError, clean, width, height);
        AssertMatchedCandidates(
            nonLocalMeansResults, baselineResults, inputNoiseError, clean, width, height);
    }

    private static void AppendMatchedResults(
        System.Text.StringBuilder report,
        string candidateName,
        List<(double Strength, byte[] Pixels, double Error)> candidates,
        List<(double Strength, byte[] Pixels, double Error)> baselines,
        byte[] clean,
        int width,
        int height)
    {
        foreach (var candidate in candidates)
        {
            var matched = baselines.MinBy(item => Math.Abs(item.Error - candidate.Error));
            var candidateTextureError = TextureError(candidate.Pixels, clean, width, height);
            var baselineTextureError = TextureError(matched.Pixels, clean, width, height);
            var candidatePatternError = MeanSquaredError(
                candidate.Pixels, clean, width, height, width / 3, width * 2 / 3);
            var baselinePatternError = MeanSquaredError(
                matched.Pixels, clean, width, height, width / 3, width * 2 / 3);
            var candidateFineTextureError = MeanSquaredError(
                candidate.Pixels, clean, width, height, width * 2 / 3, width);
            var baselineFineTextureError = MeanSquaredError(
                matched.Pixels, clean, width, height, width * 2 / 3, width);
            report.AppendLine(
                $"{candidateName} setting {candidate.Strength:F1}: flat MSE {candidate.Error:F3}, texture MSE {candidateTextureError:F3}; " +
                $"bilateral strength {matched.Strength:F2}: flat MSE {matched.Error:F3}, texture MSE {baselineTextureError:F3}; " +
                $"pattern band {candidatePatternError:F3}/{baselinePatternError:F3}, " +
                $"fine band {candidateFineTextureError:F3}/{baselineFineTextureError:F3}");
        }
    }

    private static void AssertMatchedCandidates(
        List<(double Strength, byte[] Pixels, double Error)> candidates,
        List<(double Strength, byte[] Pixels, double Error)> baselines,
        double inputNoiseError,
        byte[] clean,
        int width,
        int height)
    {
        foreach (var candidate in candidates)
        {
            var matched = baselines.MinBy(item => Math.Abs(item.Error - candidate.Error));
            Assert.True(candidate.Error < inputNoiseError);
            Assert.InRange(Math.Abs(matched.Error - candidate.Error), 0, inputNoiseError * 0.02);
            Assert.True(
                TextureError(candidate.Pixels, clean, width, height) >=
                TextureError(matched.Pixels, clean, width, height),
                "A candidate that loses on aggregate texture error must not be accepted.");

            var candidatePatternError = MeanSquaredError(
                candidate.Pixels, clean, width, height, width / 3, width * 2 / 3);
            var baselinePatternError = MeanSquaredError(
                matched.Pixels, clean, width, height, width / 3, width * 2 / 3);
            var candidateFineTextureError = MeanSquaredError(
                candidate.Pixels, clean, width, height, width * 2 / 3, width);
            var baselineFineTextureError = MeanSquaredError(
                matched.Pixels, clean, width, height, width * 2 / 3, width);
            Assert.False(
                candidatePatternError < baselinePatternError &&
                candidateFineTextureError < baselineFineTextureError,
                "A candidate must improve both texture bands to pass this screen.");
        }
    }

    private static CanvasBitmap CreateBitmap(CanvasDevice device, byte[] pixels, int width, int height) =>
        CanvasBitmap.CreateFromBytes(
            device, pixels, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);

    private static byte[] Render(
        CanvasDevice device, CanvasBitmap source, int width, int height, ICanvasImage graph)
    {
        try
        {
            using var target = new CanvasRenderTarget(device, width, height, 96);
            using (var session = target.CreateDrawingSession()) session.DrawImage(graph);
            return target.GetPixelBytes();
        }
        finally
        {
            if (!ReferenceEquals(graph, source)) ((IDisposable)graph).Dispose();
        }
    }

    private static (byte[] Clean, byte[] Noisy) CreateNoisyTexture(int width, int height)
    {
        var clean = new byte[width * height * 4];
        var noisy = new byte[clean.Length];
        var random = new Random(119);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var rgb = x < width / 3
                ? (R: 128, G: 128, B: 128)
                : x < width * 2 / 3
                    ? ((x + y) % 4 < 2 ? (R: 164, G: 104, B: 78) : (R: 92, G: 152, B: 178))
                    : ((x / 3 + y / 2) % 2 == 0 ? (R: 220, G: 195, B: 140) : (R: 42, G: 55, B: 72));
            var offset = (y * width + x) * 4;
            clean[offset] = (byte)rgb.B;
            clean[offset + 1] = (byte)rgb.G;
            clean[offset + 2] = (byte)rgb.R;
            clean[offset + 3] = 255;
            noisy[offset] = AddNoise(rgb.B, random);
            noisy[offset + 1] = AddNoise(rgb.G, random);
            noisy[offset + 2] = AddNoise(rgb.R, random);
            noisy[offset + 3] = 255;
        }

        return (clean, noisy);
    }

    private static byte AddNoise(int value, Random random) =>
        (byte)Math.Clamp(value + random.Next(-22, 23), 0, 255);

    private static byte[] ApplyNonLocalMeans(byte[] pixels, int width, int height, double smoothing)
    {
        const int searchRadius = 2;
        const int patchRadius = 1;
        const double noiseVariance = 168.67;
        var output = new byte[pixels.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            double red = 0, green = 0, blue = 0, weightSum = 0;
            for (var searchY = -searchRadius; searchY <= searchRadius; searchY++)
            for (var searchX = -searchRadius; searchX <= searchRadius; searchX++)
            {
                double squaredDifference = 0;
                for (var patchY = -patchRadius; patchY <= patchRadius; patchY++)
                for (var patchX = -patchRadius; patchX <= patchRadius; patchX++)
                {
                    var source = PixelIndex(
                        Math.Clamp(x + patchX, 0, width - 1),
                        Math.Clamp(y + patchY, 0, height - 1),
                        width);
                    var candidate = PixelIndex(
                        Math.Clamp(x + searchX + patchX, 0, width - 1),
                        Math.Clamp(y + searchY + patchY, 0, height - 1),
                        width);
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var difference = pixels[source + channel] - pixels[candidate + channel];
                        squaredDifference += difference * difference;
                    }
                }

                var correctedDistance = Math.Max(0, squaredDifference / 27.0 - 2 * noiseVariance);
                var weight = Math.Exp(-correctedDistance / (smoothing * smoothing));
                var sample = PixelIndex(
                    Math.Clamp(x + searchX, 0, width - 1),
                    Math.Clamp(y + searchY, 0, height - 1),
                    width);
                blue += weight * pixels[sample];
                green += weight * pixels[sample + 1];
                red += weight * pixels[sample + 2];
                weightSum += weight;
            }

            var target = PixelIndex(x, y, width);
            output[target] = (byte)Math.Clamp((int)Math.Round(blue / weightSum), 0, 255);
            output[target + 1] = (byte)Math.Clamp((int)Math.Round(green / weightSum), 0, 255);
            output[target + 2] = (byte)Math.Clamp((int)Math.Round(red / weightSum), 0, 255);
            output[target + 3] = pixels[target + 3];
        }

        return output;
    }

    private static int PixelIndex(int x, int y, int width) => (y * width + x) * 4;

    private static double FlatError(byte[] image, byte[] clean, int width, int height) =>
        MeanSquaredError(image, clean, width, height, 0, width / 3);

    private static double TextureError(byte[] image, byte[] clean, int width, int height) =>
        MeanSquaredError(image, clean, width, height, width / 3, width);

    private static double MeanSquaredError(
        byte[] image, byte[] clean, int width, int height, int left, int right)
    {
        double sum = 0;
        for (var y = 0; y < height; y++)
        for (var x = left; x < right; x++)
        for (var channel = 0; channel < 3; channel++)
        {
            var index = (y * width + x) * 4 + channel;
            var delta = image[index] - clean[index];
            sum += delta * delta;
        }

        return sum / ((right - left) * height * 3);
    }
}
