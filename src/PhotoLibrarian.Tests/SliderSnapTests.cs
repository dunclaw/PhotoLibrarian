using PhotoLibrarian.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class SliderSnapTests
{
    [Theory]
    [InlineData(0.03, 0.0)]
    [InlineData(-0.04, 0.0)]
    [InlineData(0.05, 0.05)]
    [InlineData(-0.5, -0.5)]
    public void Apply_SnapsOnlyNearNeutral(double value, double expected)
    {
        var threshold = SliderSnap.ThresholdFor(-1, 1);

        Assert.Equal(expected, SliderSnap.Apply(value, 0, threshold));
    }

    [Fact]
    public void Apply_UsesNonZeroNeutral()
    {
        var threshold = SliderSnap.ThresholdFor(0.1, 0.9);

        Assert.Equal(0.5, SliderSnap.Apply(0.51, 0.5, threshold));
        Assert.Equal(0.53, SliderSnap.Apply(0.53, 0.5, threshold));
    }

    [Fact]
    public void ThresholdFor_IsSmallFractionOfRange()
    {
        Assert.Equal(0.04, SliderSnap.ThresholdFor(-1, 1), 10);
    }
}
