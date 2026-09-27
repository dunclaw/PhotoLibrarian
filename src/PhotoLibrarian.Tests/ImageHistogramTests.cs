using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ImageHistogramTests
{
    [Fact]
    public void Calculate_UsesLuminanceAndNormalizesBins()
    {
        var pixels = new byte[]
        {
            0, 0, 0, 255,
            0, 0, 0, 255,
            255, 255, 255, 255,
            255, 0, 0, 255
        };

        var histogram = ImageHistogram.Calculate(pixels, new EditParameters());

        Assert.Equal(ImageHistogram.BinCount, histogram.Length);
        Assert.Equal(1f, histogram.Max());
        Assert.Equal(1f, histogram[0]);
        Assert.Equal(0.5f, histogram[^1]);
        Assert.Contains(histogram, value => value > 0 && value < 1);
    }

    [Fact]
    public void Calculate_ReflectsLiveLevels()
    {
        var pixels = new byte[4 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 128;
            pixels[i + 1] = 128;
            pixels[i + 2] = 128;
            pixels[i + 3] = 255;
        }

        var histogram = ImageHistogram.Calculate(
            pixels,
            new EditParameters { BlackPoint = 0.75, WhitePoint = 1.0 });

        Assert.Equal(1f, histogram[0]);
    }
}
