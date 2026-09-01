using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class FaceAlignmentTests
{
    [Fact]
    public void SolveSimilarityTransform_RecoversScaleRotationAndTranslation()
    {
        var source = new (double X, double Y)[]
        {
            (0, 0),
            (1, 0),
            (0, 1),
            (1, 1),
            (2, 2)
        };
        var target = source
            .Select(point => (
                X: 2 * point.X - point.Y + 10,
                Y: point.X + 2 * point.Y + 20))
            .ToArray();

        var transform = ImagePixelData.SolveSimilarityTransform(source, target);

        Assert.Equal(2, transform.A, 8);
        Assert.Equal(1, transform.B, 8);
        Assert.Equal(10, transform.TranslateX, 8);
        Assert.Equal(20, transform.TranslateY, 8);
    }
}
