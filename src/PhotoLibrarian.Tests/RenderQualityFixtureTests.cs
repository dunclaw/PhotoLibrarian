using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class RenderQualityFixtureTests
{
    [Fact]
    public void GeneratedQualityFixturesAreSeededAndDistinct()
    {
        var outputs = RenderQualityFixtures.Kinds
            .Select(kind => RenderQualityFixtures.CreatePixels(kind, 37, 23))
            .ToArray();

        for (var index = 0; index < outputs.Length; index++)
        {
            Assert.Equal(outputs[index], RenderQualityFixtures.CreatePixels(
                RenderQualityFixtures.Kinds[index], 37, 23));
        }

        Assert.Equal(outputs.Length, outputs.Select(Convert.ToHexString).Distinct().Count());
        var alphaPixels = RenderQualityFixtures.CreatePixels(
            RenderQualityFixtureKind.Gradient, 17, 9, alpha: true);
        Assert.Contains(
            Enumerable.Range(0, alphaPixels.Length / 4).Select(index => alphaPixels[index * 4 + 3]),
            alpha => alpha < 255);
    }
}
