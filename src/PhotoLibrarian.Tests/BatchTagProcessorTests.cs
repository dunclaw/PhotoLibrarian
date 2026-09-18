using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class BatchTagProcessorTests
{
    [Fact]
    public async Task ProcessLibraryAsync_WaitsForIdleAndStoresEachPrediction()
    {
        var images = new[] { CreateImage(1), CreateImage(2) };
        var store = new FakeStore(images);
        var provider = new FakeModelProvider();
        var tagger = new FakeTagger(
            [new TagPrediction("dog", 0.8f)]);
        var gate = new CountingActivityGate();
        var processor = new BatchTagProcessor(
            store,
            provider,
            tagger,
            gate);
        BatchTagProgressEventArgs? completion = null;
        processor.Progress += (_, progress) =>
        {
            if (progress.IsComplete)
            {
                completion = progress;
            }
        };

        var result = await processor.ProcessLibraryAsync(
            CreateApprovedSettings(),
            TestContext.Current.CancellationToken);

        Assert.Equal(new BatchTagResult(2, 0, 2), result);
        Assert.Equal(3, gate.WaitCount);
        Assert.Equal(
            AutoTagModelCatalog.MobileNetV2ProfileId,
            provider.ProfileId);
        Assert.Equal(
            AutoTagModelCatalog.MobileNetV2ProfileId,
            tagger.LoadedProfileId);
        Assert.Equal(2, store.Saved.Count);
        Assert.All(
            store.Saved,
            saved => Assert.Equal(
                new GeneratedImageTag("dog", 0.8f),
                Assert.Single(saved.Tags)));
        Assert.All(
            store.Saved,
            saved => Assert.Equal(
                AutoTagModelCatalog.MobileNetV2.PipelineVersion,
                saved.ScanVersion));
        Assert.NotNull(completion);
        Assert.Equal(2, completion.TagsAdded);
    }

    [Fact]
    public async Task ProcessLibraryAsync_BlocksUnapprovedProfile()
    {
        var store = new FakeStore([CreateImage(1)]);
        var processor = new BatchTagProcessor(
            store,
            new FakeModelProvider(),
            new FakeTagger([]),
            new CountingActivityGate());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessLibraryAsync(
                new AutoTaggingSettings(
                    true,
                    AutoTagModelCatalog.MobileNetV2ProfileId),
                TestContext.Current.CancellationToken));

        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task ProcessLibraryAsync_ReportsFailureAndContinues()
    {
        var store = new FakeStore(
            [CreateImage(1), CreateImage(2)]);
        var processor = new BatchTagProcessor(
            store,
            new FakeModelProvider(),
            new FakeTagger(
                [new TagPrediction("cat", 0.9f)],
                failurePath: store.Images[0].FilePath),
            new CountingActivityGate());

        var result = await processor.ProcessLibraryAsync(
            CreateApprovedSettings(
                AutoTagModelCatalog.EfficientNetLite4ProfileId),
            TestContext.Current.CancellationToken);

        Assert.Equal(new BatchTagResult(2, 1, 1), result);
        Assert.Single(store.Saved);
        Assert.Equal(2, store.Saved[0].Image.Id);
    }

    private static AutoTaggingSettings CreateApprovedSettings(
        string profileId =
            AutoTagModelCatalog.MobileNetV2ProfileId)
    {
        var definition = AutoTagModelCatalog.ForId(profileId);
        return new AutoTaggingSettings(
            true,
            profileId,
            definition.DefaultMaximumTags,
            definition.DefaultConfidenceThreshold,
            null,
            new Dictionary<string, AutoTagQualityStatus>
            {
                [definition.ApprovalKey] =
                    AutoTagQualityStatus.Approved
            });
    }

    private static ImageEntry CreateImage(long id) => new()
    {
        Id = id,
        FilePath = $@"C:\Photos\photo-{id}.jpg",
        FileName = $"photo-{id}.jpg",
        FileSize = 100,
        DateModified = DateTime.UtcNow,
        DateIndexed = DateTime.UtcNow
    };

    private sealed class FakeStore(
        IReadOnlyList<ImageEntry> images) : IAutoTagStore
    {
        public IReadOnlyList<ImageEntry> Images { get; } = images;
        public List<(
            ImageEntry Image,
            IReadOnlyCollection<GeneratedImageTag> Tags,
            string ScanVersion)> Saved { get; } = [];

        public Task<List<ImageEntry>> GetImagesNeedingAutoTagsAsync(
            string scanVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Images.ToList());

        public Task<bool> TryReplaceAutoTagsAsync(
            ImageEntry expectedImage,
            IReadOnlyCollection<GeneratedImageTag> tags,
            string scanVersion,
            CancellationToken cancellationToken = default)
        {
            Saved.Add((expectedImage, tags, scanVersion));
            return Task.FromResult(true);
        }
    }

    private sealed class FakeModelProvider : IAutoTagModelProvider
    {
        public string? ProfileId { get; private set; }

        public Task EnsureModelAsync(
            string profileId,
            string? modelDirectory,
            CancellationToken cancellationToken = default)
        {
            ProfileId = profileId;
            return Task.CompletedTask;
        }

        public string GetAssetPath(
            string profileId,
            AutoTagAssetDefinition asset,
            string? modelDirectory) =>
            Path.Combine(
                modelDirectory ?? Path.GetTempPath(),
                profileId,
                asset.FileName);
    }

    private sealed class FakeTagger(
        IReadOnlyList<TagPrediction> predictions,
        string? failurePath = null) : IAutoTagger
    {
        public string? LoadedProfileId { get; private set; }

        public void LoadModel(
            string profileId,
            string? modelDirectory)
        {
            LoadedProfileId = profileId;
        }

        public Task<IReadOnlyList<TagPrediction>> PredictTagsAsync(
            string imagePath,
            string profileId,
            string? modelDirectory,
            int maximumTags,
            float confidenceThreshold,
            CancellationToken cancellationToken = default)
        {
            if (imagePath == failurePath)
            {
                throw new InvalidDataException("Unreadable image");
            }

            return Task.FromResult(predictions);
        }
    }

    private sealed class CountingActivityGate :
        IBackgroundActivityGate
    {
        public int WaitCount { get; private set; }

        public Task WaitForIdleAsync(
            CancellationToken cancellationToken = default)
        {
            WaitCount++;
            return Task.CompletedTask;
        }
    }
}
