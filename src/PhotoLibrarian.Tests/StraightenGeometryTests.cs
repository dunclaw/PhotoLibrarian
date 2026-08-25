using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class StraightenGeometryTests
{
    [Theory]
    [InlineData(100, 10, -5.7106)]
    [InlineData(-100, -10, -5.7106)]
    [InlineData(100, -10, 5.7106)]
    public void GetGuideCorrection_LevelsGuideRegardlessOfDragDirection(
        double deltaX,
        double deltaY,
        double expected)
    {
        var correction = StraightenGeometry.GetGuideCorrection(deltaX, deltaY);

        Assert.Equal(expected, correction, 3);
    }

    [Fact]
    public void GetGuideCorrection_ClampsSteepGuideToManualRange()
    {
        var correction = StraightenGeometry.GetGuideCorrection(1, 10);

        Assert.Equal(-45, correction);
    }
}
