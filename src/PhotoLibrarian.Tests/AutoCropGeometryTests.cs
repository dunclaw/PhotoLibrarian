using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class AutoCropGeometryTests
{
    [Fact]
    public void GetLargestRectangle_ZeroAngleKeepsOriginalDimensions()
    {
        var crop = AutoCropGeometry.GetLargestRectangle(400, 300, 0);

        Assert.Equal(400, crop.Width);
        Assert.Equal(300, crop.Height);
    }

    [Fact]
    public void GetLargestRectangle_SquareAtFortyFiveDegreesHasKnownSize()
    {
        var crop = AutoCropGeometry.GetLargestRectangle(100, 100, 45);
        var expected = 100 / Math.Sqrt(2);

        Assert.Equal(expected, crop.Width, 8);
        Assert.Equal(expected, crop.Height, 8);
    }

    [Theory]
    [InlineData(400, 300, 5)]
    [InlineData(400, 300, 22.5)]
    [InlineData(400, 300, 45)]
    [InlineData(300, 400, -17)]
    public void GetLargestRectangle_StaysPositiveAndInsideSource(
        double width,
        double height,
        double angle)
    {
        var crop = AutoCropGeometry.GetLargestRectangle(width, height, angle);

        Assert.InRange(crop.Width, double.Epsilon, width);
        Assert.InRange(crop.Height, double.Epsilon, height);
        Assert.True(AreCornersInsideRotatedSource(width, height, crop, angle));
        Assert.True(AutoCropGeometry.GetCoverScale(width, height, angle) >= 1);
    }

    private static bool AreCornersInsideRotatedSource(
        double sourceWidth,
        double sourceHeight,
        AutoCropGeometry.CropSize crop,
        double rotationDegrees)
    {
        var radians = -rotationDegrees * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var halfSourceWidth = sourceWidth / 2;
        var halfSourceHeight = sourceHeight / 2;

        foreach (var x in new[] { -crop.Width / 2, crop.Width / 2 })
        foreach (var y in new[] { -crop.Height / 2, crop.Height / 2 })
        {
            var sourceX = (x * cos) - (y * sin);
            var sourceY = (x * sin) + (y * cos);
            if (Math.Abs(sourceX) > halfSourceWidth + 1e-8
                || Math.Abs(sourceY) > halfSourceHeight + 1e-8)
                return false;
        }

        return true;
    }
}
