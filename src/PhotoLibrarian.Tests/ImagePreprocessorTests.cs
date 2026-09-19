using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ImagePreprocessorTests
{
    [Fact]
    public async Task UnitNchwLetterbox_PreservesAspectRatioAndPads()
    {
        var imagePath = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}.png");
        try
        {
            await WicTestImage.CreateAsync(imagePath, 4, 2);

            var tensor = await ImagePreprocessor
                .PreprocessImageUnitNchwLetterboxAsync(
                    imagePath,
                    4,
                    TestContext.Current.CancellationToken);

            Assert.Equal([1, 3, 4, 4], tensor.Dimensions.ToArray());
            Assert.Equal(0f, tensor[0, 0, 0, 0]);
            Assert.InRange(tensor[0, 0, 1, 0], 0.99f, 1f);
            Assert.Equal(0f, tensor[0, 1, 1, 0]);
            Assert.Equal(0f, tensor[0, 2, 1, 0]);
            Assert.Equal(0f, tensor[0, 0, 3, 0]);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }
}
