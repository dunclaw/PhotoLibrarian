// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using System.Diagnostics;
using System.Numerics;
using Windows.Graphics.DirectX;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class RasterCreativeEffectTests
{
    [Fact]
    public void SourcePrimitiveBypassesNeutralAndReportsInvalidInputsWithoutReturningAnImage()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = Bitmap(device, Gray(7, 5, (_, _) => 128), 7, 5);
        Assert.True(SourceDetailEffect.TryCreate(source, new(7, 5), 0, 0, out var neutral, out var error), error);
        Assert.Same(source, neutral);
        foreach (var amount in new[] { double.NaN, double.PositiveInfinity, -1.01, 1.01 })
        {
            Assert.False(SourceDetailEffect.TryCreate(source, new(7, 5), amount, 0, out var image, out error));
            Assert.Null(image);
            Assert.NotEmpty(error);
            Assert.False(SourceDetailEffect.TryCreate(source, new(7, 5), 0, amount, out image, out error));
            Assert.Null(image);
        }
        Assert.False(SourceDetailEffect.TryCreate(source, new(0, 5), 0, 0, out _, out error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void SignedDehazeFollowsFlatVeilEquationAndLeavesWhiteHighlightsUntouched()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        var input = Gray(17, 11, (_, _) => 128);
        var positive = Render(device, input, 17, 11, 1, 0);
        var negative = Render(device, input, 17, 11, -1, 0);
        // For C=128/255, local veil=C/2: removal=(C-veil)/(1-veil), addition=C+(1-C)/4.
        Assert.InRange(positive[0], (byte)85, (byte)87);
        Assert.InRange(negative[0], (byte)159, (byte)161);
        var white = Gray(7, 5, (_, _) => 255);
        Assert.Equal(white, Render(device, white, 7, 5, 1, 0));
        Assert.Equal(white, Render(device, white, 7, 5, -1, 0));
    }

    [Fact]
    public void TextureChangesFineBandsInBothDirectionsButDoesNotSharpenStrongStepsOrAmplifyTinyNoise()
    {
        const int width = 61, height = 17;
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        var fine = Gray(width, height, (x, _) => (byte)Math.Round(128 + 16 * Math.Sin(x * Math.PI / 3)));
        var positive = Render(device, fine, width, height, 0, 1);
        var negative = Render(device, fine, width, height, 0, -1);
        var originalRms = Rms(fine, width, height, 4);
        Assert.True(Rms(positive, width, height, 4) > originalRms * 1.04);
        Assert.True(Rms(negative, width, height, 4) < originalRms * 0.96);

        var edge = Gray(width, height, (x, _) => x < width / 2 ? (byte)64 : (byte)192);
        Assert.Equal(edge, Render(device, edge, width, height, 0, 1));
        var tinyNoise = Gray(width, height, (x, y) => (byte)(128 + ((x + y) % 2 == 0 ? 1 : -1)));
        Assert.Equal(tinyNoise, Render(device, tinyNoise, width, height, 0, 1));
    }

    [Fact]
    public void FineBandTextureIsDistinctFromClarityAndEdgeSharpening()
    {
        const int width = 61, height = 17;
        var pixels = Gray(width, height, (x, _) =>
            (byte)Math.Round((x < width / 2 ? 70 : 180) + 12 * Math.Sin(x * Math.PI / 3)));
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = Bitmap(device, pixels, width, height);
        var texture = Render(device, pixels, width, height, 0, 1);
        foreach (var parameters in new[] { new EditParameters { Clarity = 1 }, new EditParameters { Sharpness = 1 } })
        {
            var graph = EditEffectGraph.Build(source, new(width, height), parameters);
            try
            {
                using var target = new CanvasRenderTarget(device, width, height, 96);
                using (var session = target.CreateDrawingSession()) session.DrawImage(graph);
                var other = target.GetPixelBytes();
                var differences = 0;
                for (var i = 0; i < other.Length; i += 4)
                    if (Math.Abs(other[i] - texture[i]) > 2) differences++;
                if (parameters.Clarity != 0)
                {
                    Assert.True(differences > width * height / 10,
                        $"Texture differs at only {differences}/{width * height} pixels from Clarity.");
                }
                else
                {
                    // Sharpening changes the two sides of the strong edge; Texture's edge gate does not.
                    foreach (var x in new[] { width / 2 - 1, width / 2 })
                    {
                        var index = (height / 2 * width + x) * 4;
                        Assert.Equal(pixels[index], texture[index]);
                        Assert.True(Math.Abs(other[index] - texture[index]) > 2);
                    }
                }
            }
            finally { (graph as IDisposable)?.Dispose(); }
        }
    }

    [Fact]
    public void SourceEffectsPreserveAlphaAndNeverWriteHiddenOrUnpremultipliedColors()
    {
        const int width = 13, height = 9;
        var pixels = Gray(width, height, (x, _) => (byte)(100 + x * 3));
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = (byte)((i / 4 % 5) * 51);
            for (var c = 0; c < 3; c++) pixels[i + c] = (byte)(pixels[i + c] * alpha / 255);
            pixels[i + 3] = alpha;
        }
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        foreach (var (dehaze, texture) in new[] { (1d, 0d), (-1d, 0d), (0d, 1d), (0d, -1d), (1d, 1d) })
        {
            var output = Render(device, pixels, width, height, dehaze, texture);
            for (var i = 0; i < output.Length; i += 4)
            {
                Assert.Equal(pixels[i + 3], output[i + 3]);
                for (var c = 0; c < 3; c++) Assert.InRange(output[i + c], (byte)0, output[i + 3]);
            }
        }
    }

    [Fact]
    public void EightPixelSourceHaloMatchesWholeImageAcrossTileSeams()
    {
        const int width = 47, height = 29;
        var pixels = Gray(width, height, (x, y) => (byte)(80 + (x * 7 + y * 3) % 100));
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        var whole = Render(device, pixels, width, height, 0.8, 0.7);
        foreach (var tileWidth in new[] { 7, 19 })
        {
            for (var startY = 0; startY < height; startY += 11)
            for (var start = 0; start < width; start += tileWidth)
            {
                var end = Math.Min(width, start + tileWidth);
                var endY = Math.Min(height, startY + 11);
                var left = Math.Max(0, start - SourceDetailEffect.Halo);
                var right = Math.Min(width, end + SourceDetailEffect.Halo);
                var top = Math.Max(0, startY - SourceDetailEffect.Halo);
                var bottom = Math.Min(height, endY + SourceDetailEffect.Halo);
                var input = new byte[(right - left) * (bottom - top) * 4];
                for (var y = top; y < bottom; y++)
                    Array.Copy(pixels, (y * width + left) * 4, input,
                        (y - top) * (right - left) * 4, (right - left) * 4);
                var tile = Render(device, input, right - left, bottom - top, 0.8, 0.7);
                for (var y = startY; y < endY; y++)
                for (var x = start; x < end; x++)
                for (var c = 0; c < 4; c++)
                    Assert.InRange(Math.Abs(whole[(y * width + x) * 4 + c] -
                        tile[((y - top) * (right - left) + x - left) * 4 + c]), 0, c == 3 ? 0 : 1);
            }
        }
    }

    [Theory]
    [InlineData(4000, 3000)]
    [InlineData(6000, 4000)]
    public void SourceResolutionPrimitiveCompletesWithoutAnImageSizeCutoff(int width, int height)
    {
        var pixels = Gray(width, height, (x, y) => (byte)(100 + (x + y) % 60));
        using var device = new CanvasDevice();
        var stopwatch = Stopwatch.StartNew();
        var output = Render(device, pixels, width, height, 0.7, 0.5);
        stopwatch.Stop();
        Assert.Equal(pixels.Length, output.Length);
        Assert.Equal((byte)255, output[^1]);
        using var process = Process.GetCurrentProcess();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Source detail {width}x{height}: {stopwatch.Elapsed.TotalMilliseconds:F1} ms " +
            $"including upload/readback; process peak working set {process.PeakWorkingSet64 / 1048576d:F1} MiB.");
    }

    private static CanvasBitmap Bitmap(CanvasDevice device, byte[] pixels, int width, int height) =>
        CanvasBitmap.CreateFromBytes(device, pixels, width, height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);

    private static byte[] Render(CanvasDevice device, byte[] pixels, int width, int height, double dehaze, double texture)
    {
        using var source = Bitmap(device, pixels, width, height);
        Assert.True(SourceDetailEffect.TryCreate(source, new(width, height), dehaze, texture,
            out var image, out var error), error);
        try
        {
            using var target = new CanvasRenderTarget(device, width, height, 96);
            using (var session = target.CreateDrawingSession()) session.DrawImage(image);
            return target.GetPixelBytes();
        }
        finally
        {
            if (!ReferenceEquals(image, source)) (image as IDisposable)?.Dispose();
        }
    }

    private static byte[] Gray(int width, int height, Func<int, int, byte> value)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = value(x, y);
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    private static double Rms(byte[] pixels, int width, int height, int border)
    {
        double squares = 0;
        var count = 0;
        for (var y = border; y < height - border; y++)
        for (var x = border; x < width - border; x++)
        {
            var delta = pixels[(y * width + x) * 4] - 128d;
            squares += delta * delta;
            count++;
        }
        return Math.Sqrt(squares / count);
    }
}

public sealed class PostCropEffectReferenceTests
{
    [Fact]
    public void ValidationRejectsInvalidSettingsPixelsAndCoordinatesAndCancellationIsExplicit()
    {
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, -0.01, 1.01 })
        {
            Assert.False(new PostCropEffectSettings { GrainAmount = value }.TryValidate(out var error));
            Assert.NotEmpty(error);
        }
        Assert.False(Evaluate(new() { Feather = double.NaN }, 100, 80, 50, 40, out _));
        Assert.False(Evaluate(new(), double.MaxValue, 0, 50, 40, out _));
        Assert.False(Evaluate(new(), 100, 80, -1, 40, out _));
        Assert.False(PostCropEffectReference.TryEvaluate(new(0.5f, 0, 0, 0.1f), new(),
            100, 80, 50, 40, 1, 1, out _, out _));
        Assert.False(Evaluate(new() { GrainAmount = 1 }, double.MaxValue, 1,
            double.MaxValue / 2, 0.5, out _));
        Assert.False(Evaluate(new() { VignetteAmount = -1 }, double.Epsilon, double.Epsilon,
            0, 0, out _));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.False(PostCropEffectReference.TryEvaluate(new(0.5f, 0.5f, 0.5f, 1), new(),
            100, 80, 50, 40, 1, 1, out _, out var cancellationError, cancelled.Token));
        Assert.Contains("cancelled", cancellationError);
    }

    [Fact]
    public void VignettePreservesCenterSupportsBothSignsAndProtectsBrightEdges()
    {
        Assert.True(Evaluate(new() { VignetteAmount = -1 }, 100, 80, 50, 40, out var center));
        Assert.Equal(new Vector4(0.5f, 0.5f, 0.5f, 1), center);
        Assert.True(Evaluate(new() { VignetteAmount = -1 }, 100, 80, 0, 0, out var dark));
        Assert.InRange(dark.X, 0.0999f, 0.1001f);
        Assert.True(Evaluate(new() { VignetteAmount = 1 }, 100, 80, 0, 0, out var bright));
        Assert.InRange(bright.X, 0.8999f, 0.9001f);
        var white = new Vector4(1);
        Assert.True(PostCropEffectReference.TryEvaluate(white, new() { VignetteAmount = -1, HighlightPriority = 1 },
            100, 80, 0, 0, 1, 1, out var protectedWhite, out _));
        Assert.Equal(white, protectedWhite);
        Assert.True(Evaluate(new() { VignetteAmount = -1, Feather = 1 }, 100, 80, 5, 40, out var soft));
        Assert.True(Evaluate(new() { VignetteAmount = -1, Feather = 0 }, 100, 80, 5, 40, out var hard));
        Assert.True(soft.X < hard.X);
        Assert.True(Evaluate(new() { VignetteAmount = -1, Roundness = 1 }, 200, 80, 10, 40, out var round));
        Assert.True(Evaluate(new() { VignetteAmount = -1, Roundness = 0 }, 200, 80, 10, 40, out var ellipse));
        Assert.True(round.X < ellipse.X);
    }

    [Fact]
    public void GrainIsSeededBoundedNearlyZeroMeanAndUsesGlobalCoordinatesAcrossPartitions()
    {
        var settings = new PostCropEffectSettings { GrainAmount = 1, GrainSize = 0.7, Seed = uint.MaxValue };
        var whole = new Vector4[101 * 79];
        double sum = 0, squares = 0;
        for (var y = 0; y < 79; y++)
        for (var x = 0; x < 101; x++)
        {
            Assert.True(Evaluate(settings, 101, 79, x + 0.5, y + 0.5, out var sample));
            whole[y * 101 + x] = sample;
            var delta = sample.X - 0.5;
            sum += delta;
            squares += delta * delta;
            Assert.InRange(Math.Abs(delta), 0, 0.07);
        }
        Assert.InRange(Math.Abs(sum / whole.Length), 0, 0.002);
        Assert.InRange(Math.Sqrt(squares / whole.Length), 0.003, 0.05);
        foreach (var tileWidth in new[] { 7, 29 })
        for (var origin = 0; origin < 101; origin += tileWidth)
        for (var y = 0; y < 79; y++)
        for (var localX = 0; localX < Math.Min(tileWidth, 101 - origin); localX++)
        {
            var globalX = origin + localX;
            Assert.True(Evaluate(settings, 101, 79, globalX + 0.5, y + 0.5, out var sample));
            Assert.Equal(whole[y * 101 + globalX], sample);
        }
        Assert.True(Evaluate(settings with { Seed = 42 }, 101, 79, 15.5, 20.5, out var changed));
        Assert.NotEqual(whole[20 * 101 + 15], changed);
        Assert.True(Evaluate(settings with { GrainSize = 0.1 }, 101, 79, 15.5, 20.5, out var smaller));
        Assert.NotEqual(whole[20 * 101 + 15], smaller);
        Assert.True(Evaluate(settings with { Roughness = 0 }, 101, 79, 15.5, 20.5, out var smoother));
        Assert.NotEqual(whole[20 * 101 + 15], smoother);
    }

    [Fact]
    public void RelativeGrainCoordinatesAndIntegratedFootprintsAreResolutionIndependent()
    {
        var settings = new PostCropEffectSettings { GrainAmount = 1, GrainSize = 0.5, Roughness = 0.7 };
        Assert.True(Evaluate(settings, 4000, 3000, 1200, 1000, out var full, 10, 10));
        Assert.True(Evaluate(settings, 400, 300, 120, 100, out var reduced, 1, 1));
        Assert.Equal(full, reduced);
        // The analytic 2x2 box average must match the average of four equal sub-footprints.
        Assert.True(Evaluate(settings, 4000, 3000, 1200, 1000, out var average, 2, 2));
        var sum = Vector4.Zero;
        foreach (var dx in new[] { -0.5, 0.5 })
        foreach (var dy in new[] { -0.5, 0.5 })
        {
            Assert.True(Evaluate(settings, 4000, 3000, 1200 + dx, 1000 + dy, out var sub));
            sum += sub / 4;
        }
        Assert.InRange(Vector4.Distance(average, sum), 0, 0.000001f);
    }

    [Fact]
    public void NeutralAndTransparencyAreExactAndAllActiveEffectsPreservePremultiplication()
    {
        var input = new Vector4(0.12f, 0.2f, 0.08f, 0.25f);
        Assert.True(PostCropEffectReference.TryEvaluate(input, new(), 100, 80, 0.5, 0.5, 1, 1,
            out var neutral, out _));
        Assert.Equal(input, neutral);
        foreach (var amount in new[] { -1d, 1d })
        {
            var settings = new PostCropEffectSettings { VignetteAmount = amount, GrainAmount = 1 };
            Assert.True(PostCropEffectReference.TryEvaluate(input, settings, 100, 80, 0.5, 0.5, 1, 1,
                out var output, out _));
            Assert.Equal(input.W, output.W);
            Assert.InRange(output.X, 0, input.W);
            Assert.InRange(output.Y, 0, input.W);
            Assert.InRange(output.Z, 0, input.W);
            Assert.True(PostCropEffectReference.TryEvaluate(Vector4.Zero, settings, 100, 80, 0.5, 0.5, 1, 1,
                out var transparent, out _));
            Assert.Equal(Vector4.Zero, transparent);
        }
    }

    private static bool Evaluate(PostCropEffectSettings settings, double width, double height,
        double x, double y, out Vector4 result, double footprintWidth = 1, double footprintHeight = 1) =>
        PostCropEffectReference.TryEvaluate(new(0.5f, 0.5f, 0.5f, 1), settings,
            width, height, x, y, footprintWidth, footprintHeight, out result, out _);
}
