using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class RedEyeRemovalProcessorTests
{
    [Fact]
    public void Apply_DesaturatesRedDominantPixelsInsideSelection()
    {
        var pixels = new byte[]
        {
            20, 20, 240, 255,
            20, 20, 240, 255,
            20, 20, 240, 255,
            20, 20, 240, 255
        };

        var changed = RedEyeRemovalProcessor.Apply(
            pixels, 2, 2, new RedEyeBounds(0, 0, 1, 2));

        Assert.Equal(2, changed);
        Assert.Equal(20, pixels[2]);
        Assert.Equal(240, pixels[6]);
        Assert.Equal(240, pixels[10]);
    }

    [Fact]
    public void Apply_LeavesPixelsOutsideSelectionAndNeutralPixelsUnchanged()
    {
        var pixels = new byte[]
        {
            20, 20, 240, 255,
            100, 100, 100, 255
        };

        var changed = RedEyeRemovalProcessor.Apply(
            pixels, 2, 1, new RedEyeBounds(1, 0, 1, 1));

        Assert.Equal(0, changed);
        Assert.Equal(240, pixels[2]);
        Assert.Equal(100, pixels[6]);
    }
}
