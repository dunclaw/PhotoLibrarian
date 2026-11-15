// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Diagnostics;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class DefringeFeasibilityExperimentTests(ITestOutputHelper output)
{
    private const double DesaturationStrength = 0.8;

    [Fact]
    public void EdgeGuidedDesaturationReducesSyntheticFringeButDamagesRealColorBoundaries()
    {
        const int width = 64;
        const int height = 48;
        var fringe = CreateFringeFixture(width, height);
        var fringeOriginal = (byte[])fringe.Clone();
        var disabled = Apply(fringe, width, height, strength: 0);
        var corrected = Apply(fringe, width, height);
        var grayEdge = CreateGrayEdgeFixture(width, height);
        var neutralControl = Apply(grayEdge, width, height);
        var coloredObject = CreateColoredObjectFixture(width, height);
        var objectCorrected = Apply(coloredObject, width, height);

        var fringeChromaBefore = ChromaAt(fringe, width, width / 2, height / 2);
        var fringeChromaAfter = ChromaAt(corrected, width, width / 2, height / 2);
        var affectedObjectPixels = CountChangedPixelsInRegion(
            coloredObject, objectCorrected, width, left: 24, top: 16, regionWidth: 16, regionHeight: 16);
        var objectChromaBefore = MeanChromaInRegion(
            coloredObject, width, left: 24, top: 16, regionWidth: 16, regionHeight: 16);
        var objectChromaAfter = MeanChromaInRegion(
            objectCorrected, width, left: 24, top: 16, regionWidth: 16, regionHeight: 16);

        output.WriteLine($"Synthetic purple fringe chroma: {fringeChromaBefore:F1} -> {fringeChromaAfter:F1} " +
            $"({fringeChromaAfter / fringeChromaBefore:P0} remaining)");
        output.WriteLine($"Neutral gray edge changed pixels: {CountChangedPixels(grayEdge, neutralControl)}");
        output.WriteLine($"Real purple object boundary changed: {affectedObjectPixels}/256 pixels " +
            $"({affectedObjectPixels / 256.0:P1}); mean object chroma {objectChromaBefore:F1} -> {objectChromaAfter:F1}");

        Assert.Equal(fringe, disabled);
        Assert.Equal(0, CountChangedPixels(grayEdge, neutralControl));
        Assert.True(fringeChromaAfter < fringeChromaBefore * 0.3);
        Assert.True(affectedObjectPixels > 0);
        Assert.True(objectChromaAfter < objectChromaBefore);
        Assert.Equal(fringeOriginal, fringe);
    }

    [Fact]
    public void TransparentAlphaAndImageBordersArePreservedWithoutClipping()
    {
        const int width = 64;
        const int height = 48;
        var source = CreateTransparentBorderFixture(width, height);
        var sourceOriginal = (byte[])source.Clone();
        var outputPixels = Apply(source, width, height);
        var leftBorderBefore = ChromaAt(source, width, 0, height / 2);
        var leftBorderAfter = ChromaAt(outputPixels, width, 0, height / 2);
        var rightBorderBefore = ChromaAt(source, width, width - 1, height / 2);
        var rightBorderAfter = ChromaAt(outputPixels, width, width - 1, height / 2);
        var transparentPixels = CountTransparentPixels(outputPixels);

        Assert.Equal(source.Length, outputPixels.Length);
        AssertAlphaPreserved(source, outputPixels);
        Assert.True(leftBorderAfter < leftBorderBefore);
        Assert.True(rightBorderAfter < rightBorderBefore);
        AssertTransparentPixelsRemainClear(outputPixels);
        Assert.Equal(sourceOriginal, source);
        output.WriteLine($"Half-alpha border fringe chroma, left/right: " +
            $"{leftBorderBefore:F1}->{leftBorderAfter:F1}, {rightBorderBefore:F1}->{rightBorderAfter:F1}");
        output.WriteLine($"Alpha delta: 0 across {width * height} pixels; " +
            $"fully transparent pixels retained clear: {transparentPixels}");
    }

    [Fact]
    public void SourceResolutionProcessingPreservesDimensionsAndReportsBoundedThroughput()
    {
        const int width = 2048;
        const int height = 1536;
        var source = CreateFringeFixture(width, height);
        var original = (byte[])source.Clone();
        var stopwatch = Stopwatch.StartNew();
        var outputPixels = Apply(source, width, height);
        stopwatch.Stop();

        Assert.Equal(checked(width * height * 4), outputPixels.Length);
        Assert.Equal(original, source);
        Assert.True(ChromaAt(outputPixels, width, width / 2, height / 2) <
            ChromaAt(source, width, width / 2, height / 2));
        AssertAlphaPreserved(source, outputPixels);
        output.WriteLine($"Synthetic source-resolution fixture: {width}x{height}, " +
            $"{stopwatch.Elapsed.TotalMilliseconds:F1} ms, " +
            $"{(width * (double)height / stopwatch.Elapsed.TotalSeconds / 1_000_000):F2} MP/s");
    }

    private static byte[] Apply(
        byte[] source,
        int width,
        int height,
        double strength = DesaturationStrength)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (width <= 0 || height <= 0 || source.Length != checked(width * height * 4))
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(strength) || strength is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(strength));

        var outputPixels = (byte[])source.Clone();
        if (strength == 0)
            return outputPixels;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width + x) * 4;
                var alpha = source[index + 3];
                if (alpha == 0)
                    continue;

                var pixel = ReadStraightBgra(source, index);
                var chroma = Math.Max(pixel.Red, Math.Max(pixel.Green, pixel.Blue)) -
                    Math.Min(pixel.Red, Math.Min(pixel.Green, pixel.Blue));
                if (chroma < 30 || LocalLuminanceRange(source, width, height, x, y) < 48)
                    continue;

                var luminance = 0.2126 * pixel.Red + 0.7152 * pixel.Green + 0.0722 * pixel.Blue;
                WritePremultipliedBgra(outputPixels, index, alpha,
                    Blend(pixel.Blue, luminance, strength),
                    Blend(pixel.Green, luminance, strength),
                    Blend(pixel.Red, luminance, strength));
            }
        }

        return outputPixels;
    }

    private static double LocalLuminanceRange(byte[] pixels, int width, int height, int x, int y)
    {
        var minimum = double.MaxValue;
        var maximum = double.MinValue;
        for (var dy = -1; dy <= 1; dy++)
        {
            var sampleY = Math.Clamp(y + dy, 0, height - 1);
            for (var dx = -1; dx <= 1; dx++)
            {
                var sampleX = Math.Clamp(x + dx, 0, width - 1);
                var index = (sampleY * width + sampleX) * 4;
                if (pixels[index + 3] == 0)
                    continue;

                var pixel = ReadStraightBgra(pixels, index);
                var luminance = 0.2126 * pixel.Red + 0.7152 * pixel.Green + 0.0722 * pixel.Blue;
                minimum = Math.Min(minimum, luminance);
                maximum = Math.Max(maximum, luminance);
            }
        }

        return maximum - minimum;
    }

    private static double Blend(double channel, double luminance, double strength) =>
        channel + (luminance - channel) * strength;

    private static (double Blue, double Green, double Red) ReadStraightBgra(byte[] pixels, int index)
    {
        var alpha = pixels[index + 3] / 255.0;
        return (
            Math.Clamp(pixels[index] / alpha, 0, 255),
            Math.Clamp(pixels[index + 1] / alpha, 0, 255),
            Math.Clamp(pixels[index + 2] / alpha, 0, 255));
    }

    private static void WritePremultipliedBgra(
        byte[] pixels,
        int index,
        byte alpha,
        double blue,
        double green,
        double red)
    {
        pixels[index] = Premultiply(blue, alpha);
        pixels[index + 1] = Premultiply(green, alpha);
        pixels[index + 2] = Premultiply(red, alpha);
    }

    private static byte Premultiply(double channel, byte alpha) =>
        (byte)Math.Clamp(Math.Round(channel * alpha / 255, MidpointRounding.AwayFromZero), 0, alpha);

    private static byte[] CreateFringeFixture(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        var boundary = width / 2;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var gray = (byte)(x < boundary ? 40 : 220);
                SetPixel(pixels, width, x, y, gray, gray, gray, 255);
            }
            SetPixel(pixels, width, boundary, y, 190, 30, 190, 255);
        }

        return pixels;
    }

    private static byte[] CreateGrayEdgeFixture(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var gray = (byte)(x < width / 2 ? 40 : 220);
            SetPixel(pixels, width, x, y, gray, gray, gray, 255);
        }

        return pixels;
    }

    private static byte[] CreateColoredObjectFixture(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var inside = x is >= 24 and < 40 && y is >= 16 and < 32;
            if (inside)
                SetPixel(pixels, width, x, y, 190, 30, 190, 255);
            else
                SetPixel(pixels, width, x, y, 220, 220, 220, 255);
        }

        return pixels;
    }

    private static byte[] CreateTransparentBorderFixture(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (y == 0)
                SetPixel(pixels, width, x, y, 0, 0, 0, 0);
            else
                SetPixel(pixels, width, x, y, 220, 220, 220, 255);
        }

        for (var y = 1; y < height; y++)
        {
            SetPixel(pixels, width, 0, y, 190, 30, 190, 128);
            SetPixel(pixels, width, width - 1, y, 190, 30, 190, 128);
        }

        return pixels;
    }

    private static void SetPixel(
        byte[] pixels,
        int width,
        int x,
        int y,
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        var index = (y * width + x) * 4;
        pixels[index] = Premultiply(blue, alpha);
        pixels[index + 1] = Premultiply(green, alpha);
        pixels[index + 2] = Premultiply(red, alpha);
        pixels[index + 3] = alpha;
    }

    private static double ChromaAt(byte[] pixels, int width, int x, int y)
    {
        var pixel = ReadStraightBgra(pixels, (y * width + x) * 4);
        return Math.Max(pixel.Red, Math.Max(pixel.Green, pixel.Blue)) -
            Math.Min(pixel.Red, Math.Min(pixel.Green, pixel.Blue));
    }

    private static double MeanChromaInRegion(
        byte[] pixels,
        int width,
        int left,
        int top,
        int regionWidth,
        int regionHeight)
    {
        var chroma = 0.0;
        for (var y = top; y < top + regionHeight; y++)
        for (var x = left; x < left + regionWidth; x++)
            chroma += ChromaAt(pixels, width, x, y);
        return chroma / (regionWidth * regionHeight);
    }

    private static int CountChangedPixelsInRegion(
        byte[] before,
        byte[] after,
        int width,
        int left,
        int top,
        int regionWidth,
        int regionHeight)
    {
        var count = 0;
        for (var y = top; y < top + regionHeight; y++)
        for (var x = left; x < left + regionWidth; x++)
        {
            var index = (y * width + x) * 4;
            if (before[index] != after[index] ||
                before[index + 1] != after[index + 1] ||
                before[index + 2] != after[index + 2])
                count++;
        }

        return count;
    }

    private static int CountChangedPixels(byte[] before, byte[] after)
    {
        var count = 0;
        for (var index = 0; index < before.Length; index += 4)
        {
            if (before[index] != after[index] ||
                before[index + 1] != after[index + 1] ||
                before[index + 2] != after[index + 2])
                count++;
        }

        return count;
    }

    private static void AssertAlphaPreserved(byte[] before, byte[] after)
    {
        for (var index = 0; index < before.Length; index += 4)
            Assert.Equal(before[index + 3], after[index + 3]);
    }

    private static int CountTransparentPixels(byte[] pixels)
    {
        var count = 0;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index + 3] == 0)
                count++;
        }

        return count;
    }

    private static void AssertTransparentPixelsRemainClear(byte[] pixels)
    {
        for (var index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index + 3] != 0)
                continue;
            Assert.Equal((byte)0, pixels[index]);
            Assert.Equal((byte)0, pixels[index + 1]);
            Assert.Equal((byte)0, pixels[index + 2]);
        }
    }
}
