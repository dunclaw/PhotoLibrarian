using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class BatchTagProcessorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"PhotoLibrarian-batch-tags-{Guid.NewGuid():N}");

    public BatchTagProcessorTests() => Directory.CreateDirectory(_directory);

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
        Assert.All(store.Saved, saved => Assert.Null(saved.ScanVersion));
        Assert.All(
            store.MarkedVersions,
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

    private ImageEntry CreateImage(long id)
    {
        var path = Path.Combine(_directory, $"photo-{id}.raw");
        File.WriteAllBytes(path, [1, 2, 3]);
        var file = new FileInfo(path);
        return new ImageEntry
        {
            Id = id,
            FilePath = path,
            FileName = file.Name,
            FileSize = file.Length,
            DateModified = file.LastWriteTimeUtc,
            DateIndexed = DateTime.UtcNow,
            MediaType = MediaType.Image
        };
    }

    private sealed class FakeStore(
        IReadOnlyList<ImageEntry> images) : IAutoTagStore
    {
        public IReadOnlyList<ImageEntry> Images { get; } = images;
        public List<(
            ImageEntry Image,
            IReadOnlyCollection<GeneratedImageTag> Tags,
            string? ScanVersion)> Saved { get; } = [];
        public List<(long ImageId, string ScanVersion)> MarkedVersions { get; } = [];

        public Task<List<ImageTag>> GetTagsAsync(
            long imageId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<ImageTag>());

        public Task<List<ImageEntry>> GetImagesNeedingAutoTagsAsync(
            string scanVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Images.ToList());

        public Task<bool> TryReplaceAutoTagsIfUnchangedAsync(
            ImageEntry expectedImage,
            IReadOnlyCollection<GeneratedImageTag> tags,
            IReadOnlyCollection<ImageTag> expectedAutoTags,
            string? expectedScanVersion,
            CancellationToken cancellationToken = default)
        {
            Saved.Add((expectedImage, tags, null));
            return Task.FromResult(true);
        }

        public Task<bool> TryRestoreAutoTagsAsync(
            ImageEntry expectedImage,
            IReadOnlyCollection<ImageTag> expectedAutoTags,
            IReadOnlyCollection<ImageTag> previousAutoTags,
            string? previousScanVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task SetAutoTagScanVersionAsync(
            long imageId,
            string scanVersion,
            CancellationToken cancellationToken = default)
        {
            MarkedVersions.Add((imageId, scanVersion));
            return Task.CompletedTask;
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

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
