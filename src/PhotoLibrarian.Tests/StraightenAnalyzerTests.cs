using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class StraightenAnalyzerTests
{
    [Fact]
    public void AnalyzeGrayscale_ProposesCorrectionForTiltedHorizontalLine()
    {
        const double tilt = 9;
        var pixels = CreateLineImage(
            180,
            120,
            x => 60 + Math.Tan(tilt * Math.PI / 180) * (x - 90));

        var result = StraightenAnalyzer.AnalyzeGrayscale(pixels, 180, 120);

        Assert.True(result.HasResult);
        Assert.InRange(result.CorrectionDegrees, -10, -8);
        Assert.InRange(result.Confidence, 0.2, 1);
    }

    [Fact]
    public void AnalyzeGrayscale_ProposesCorrectionForTiltedVerticalLine()
    {
        const double tiltFromVertical = 7;
        var pixels = CreateVerticalLineImage(
            140,
            180,
            y => 70 + Math.Tan(tiltFromVertical * Math.PI / 180) * (y - 90));

        var result = StraightenAnalyzer.AnalyzeGrayscale(pixels, 140, 180);

        Assert.True(result.HasResult);
        Assert.InRange(result.CorrectionDegrees, 6, 8);
    }

    [Fact]
    public void AnalyzeGrayscale_ReturnsNoResultForImageWithoutEdges()
    {
        var result = StraightenAnalyzer.AnalyzeGrayscale(new byte[120 * 80], 120, 80);

        Assert.False(result.HasResult);
        Assert.Equal(0, result.CorrectionDegrees);
        Assert.Equal(0, result.Confidence);
    }

    [Fact]
    public void AnalyzeGrayscale_PrefersRepeatedAxisLinesOverDiagonalDistractor()
    {
        const int width = 240;
        const int height = 180;
        const double tilt = -6;
        var pixels = new byte[width * height];

        foreach (var centerY in new[] { 45, 90, 135 })
        {
            DrawHorizontalLine(
                pixels,
                width,
                height,
                x => centerY + Math.Tan(tilt * Math.PI / 180) * (x - (width / 2.0)),
                180);
        }

        DrawHorizontalLine(
            pixels,
            width,
            height,
            x => 20 + (0.55 * x),
            255);

        var result = StraightenAnalyzer.AnalyzeGrayscale(pixels, width, height);

        Assert.True(
            result.HasResult,
            $"Expected repeated lines to win; correction={result.CorrectionDegrees:F2}, confidence={result.Confidence:F2}.");
        Assert.InRange(result.CorrectionDegrees, 5, 7);
    }

    [Fact]
    public void AnalyzeGrayscale_RejectsCompetingAxes()
    {
        const int width = 220;
        const int height = 160;
        var pixels = new byte[width * height];

        DrawHorizontalLine(
            pixels,
            width,
            height,
            x => 45 + Math.Tan(8 * Math.PI / 180) * (x - (width / 2.0)),
            255);
        DrawHorizontalLine(
            pixels,
            width,
            height,
            x => 115 + Math.Tan(-8 * Math.PI / 180) * (x - (width / 2.0)),
            255);

        var result = StraightenAnalyzer.AnalyzeGrayscale(pixels, width, height);

        Assert.False(result.HasResult);
    }

    [Fact]
    public void AnalyzeGrayscale_ReturnsNoResultForDeterministicTexture()
    {
        const int width = 160;
        const int height = 120;
        var random = new Random(42);
        var pixels = new byte[width * height];
        random.NextBytes(pixels);

        var result = StraightenAnalyzer.AnalyzeGrayscale(pixels, width, height);

        Assert.False(
            result.HasResult,
            $"Unexpected texture result: correction={result.CorrectionDegrees:F2}, confidence={result.Confidence:F2}, edges={result.EdgeCount}.");
    }

    [Fact]
    public void AnalyzeGrayscale_FindsLowContrastHorizonThroughFineTexture()
    {
        const int width = 240;
        const int height = 180;
        const double tilt = -3;
        var random = new Random(1620);
        var pixels = new byte[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var horizonY = 62 + Math.Tan(tilt * Math.PI / 180) * (x - (width / 2.0));
                var baseValue = y < horizonY ? 52 : 92;
                var texture = random.Next(-18, 19);
                if (y >= horizonY)
                    texture += ((y + x) % 5) - 2;
                pixels[(y * width) + x] = (byte)Math.Clamp(baseValue + texture, 0, 255);
            }
        }

        var result = StraightenAnalyzer.AnalyzeGrayscale(pixels, width, height);

        Assert.True(result.HasResult);
        Assert.InRange(result.CorrectionDegrees, 2, 4);
    }

    private static byte[] CreateLineImage(int width, int height, Func<int, double> getY)
    {
        var pixels = new byte[width * height];
        DrawHorizontalLine(pixels, width, height, getY, 255);
        return pixels;
    }

    private static void DrawHorizontalLine(
        byte[] pixels,
        int width,
        int height,
        Func<int, double> getY,
        byte value)
    {
        for (var x = 4; x < width - 4; x++)
        {
            var centerY = (int)Math.Round(getY(x));
            for (var offset = -1; offset <= 1; offset++)
            {
                var y = centerY + offset;
                if (y >= 0 && y < height)
                    pixels[(y * width) + x] = value;
            }
        }
    }

    private static byte[] CreateVerticalLineImage(int width, int height, Func<int, double> getX)
    {
        var pixels = new byte[width * height];
        for (var y = 4; y < height - 4; y++)
        {
            var centerX = (int)Math.Round(getX(y));
            for (var offset = -1; offset <= 1; offset++)
            {
                var x = centerX + offset;
                if (x >= 0 && x < width)
                    pixels[(y * width) + x] = 255;
            }
        }
        return pixels;
    }
}
