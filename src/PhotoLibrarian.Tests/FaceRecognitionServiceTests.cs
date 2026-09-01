using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class FaceRecognitionServiceTests
{
    [Fact]
    public void FindBestMatch_UsesConfirmedFaceEmbeddings()
    {
        var service = new FaceRecognitionService { SimilarityThreshold = 0.8f };
        var knownFaces = new[]
        {
            new FaceRegion
            {
                PersonId = 10,
                PersonName = "Alex",
                Embedding = [1, 0]
            },
            new FaceRegion
            {
                PersonId = 20,
                PersonName = "Sam",
                Embedding = [0, 1]
            }
        };

        var match = service.FindBestMatch([0.95f, 0.05f], knownFaces);

        Assert.NotNull(match);
        Assert.Equal(10, match.PersonId);
        Assert.Equal("Alex", match.PersonName);
        Assert.True(match.Similarity > 0.99f);
    }
}
