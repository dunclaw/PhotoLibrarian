using PhotoLibrarian.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ImageRotationTests
{
    [Theory]
    [InlineData(90)]
    [InlineData(-90)]
    public void QuarterTurnSwapsOutputDimensions(double angle)
    {
        var extent = EditEffectGraph.ComputeOutputExtent(640, 480, angle);

        Assert.Equal(480u, extent.Width);
        Assert.Equal(640u, extent.Height);
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("photo.jpeg")]
    [InlineData("photo.png")]
    public void RotationSupportsCommonImageFormats(string filePath)
    {
        Assert.True(ImageEditRenderer.IsSupported(filePath));
    }
}
