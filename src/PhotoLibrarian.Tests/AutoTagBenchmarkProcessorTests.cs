using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class AutoTagBenchmarkProcessorTests
{
    [Fact]
    public async Task RunAsync_ReturnsExamplesWithoutPersistingTags()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(folder, "sample.jpg"),
                "fake image",
                TestContext.Current.CancellationToken);
            var processor = new AutoTagBenchmarkProcessor(
                new FakeModelProvider(),
                new FakeTagger());
            var settings = new AutoTaggingSettings(
                false,
                AutoTagModelCatalog.MobileNetV2ProfileId);

            var result = await processor.RunAsync(
                folder,
                settings,
                cancellationToken:
                    TestContext.Current.CancellationToken);

            Assert.Equal(1, result.Processed);
            Assert.Equal(0, result.Failed);
            Assert.Single(result.Examples);
            Assert.Equal("dog", Assert.Single(
                result.Examples[0].Predictions).Tag);
            Assert.DoesNotContain(
                typeof(PhotoLibrarian.Core.Data.IAutoTagStore),
                typeof(AutoTagBenchmarkProcessor)
                    .GetConstructors()
                    .Single()
                    .GetParameters()
                    .Select(parameter => parameter.ParameterType));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class FakeModelProvider : IAutoTagModelProvider
    {
        public Task EnsureModelAsync(
            string profileId,
            string? modelDirectory,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public string GetAssetPath(
            string profileId,
            AutoTagAssetDefinition asset,
            string? modelDirectory) =>
            asset.FileName;
    }

    private sealed class FakeTagger : IAutoTagger
    {
        public void LoadModel(
            string profileId,
            string? modelDirectory)
        {
        }

        public Task<IReadOnlyList<TagPrediction>> PredictTagsAsync(
            string imagePath,
            string profileId,
            string? modelDirectory,
            int maximumTags,
            float confidenceThreshold,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TagPrediction>>(
                [new TagPrediction("dog", 0.8f)]);
    }
}
