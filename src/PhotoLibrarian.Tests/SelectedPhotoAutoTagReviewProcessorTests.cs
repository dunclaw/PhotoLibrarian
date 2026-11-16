// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class SelectedPhotoAutoTagReviewProcessorTests
{
    [Fact]
    public async Task GenerateAsync_OnlyProcessesSelectedPhotosAndDoesNotSaveTags()
    {
        using var files = new TemporaryPhotos();
        var first = files.CreateImage("first.jpg");
        var second = files.CreateImage("second.jpg");
        var unselected = files.CreateImage("unselected.jpg");
        var video = files.CreateImage("video.mp4", MediaType.Video);
        var provider = new FakeModelProvider();
        var tagger = new FakeTagger(
            [new TagPrediction("dog", 0.82f)]);
        var processor = new SelectedPhotoAutoTagReviewProcessor(provider, tagger);

        var result = await processor.GenerateAsync(
            [first, second, video],
            CreateApprovedSettings(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsCanceled);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(
            [first.FilePath, second.FilePath],
            tagger.RequestedPaths);
        Assert.DoesNotContain(
            result.Items,
            item => item.Image.FilePath == unselected.FilePath);
        Assert.All(
            result.Items,
            item => Assert.Equal(
                new TagPrediction("dog", 0.82f),
                Assert.Single(item.Predictions)));
        Assert.Equal(1, provider.EnsureCount);
        Assert.Equal(AutoTagModelCatalog.MobileNetV2ProfileId, tagger.LoadedProfileId);
    }

    [Fact]
    public async Task GenerateAsync_ReportsPerPhotoFailureAndContinues()
    {
        using var files = new TemporaryPhotos();
        var failed = files.CreateImage("failed.jpg");
        var succeeded = files.CreateImage("succeeded.jpg");
        var processor = new SelectedPhotoAutoTagReviewProcessor(
            new FakeModelProvider(),
            new FakeTagger(
                [new TagPrediction("bird", 0.9f)],
                failurePath: failed.FilePath));

        var result = await processor.GenerateAsync(
            [failed, succeeded],
            CreateApprovedSettings(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Items.Count);
        Assert.Contains("Unreadable photo", result.Items[0].Error);
        Assert.Empty(result.Items[0].Predictions);
        Assert.Null(result.Items[1].Error);
        Assert.Single(result.Items[1].Predictions);
    }

    [Fact]
    public async Task GenerateAsync_CancellationReturnsOnlyCompletedSuggestions()
    {
        using var files = new TemporaryPhotos();
        var first = files.CreateImage("first.jpg");
        var second = files.CreateImage("second.jpg");
        using var cancellation = new CancellationTokenSource();
        var processor = new SelectedPhotoAutoTagReviewProcessor(
            new FakeModelProvider(),
            new FakeTagger([new TagPrediction("cat", 0.8f)]));
        var progress = new DelegateProgress<SelectedPhotoAutoTagReviewProgress>(
            update =>
            {
                if (update.Processed == 1)
                    cancellation.Cancel();
            });

        var result = await processor.GenerateAsync(
            [first, second],
            CreateApprovedSettings(),
            progress,
            cancellation.Token);

        Assert.True(result.IsCanceled);
        Assert.Single(result.Items);
        Assert.Equal(first.FilePath, result.Items[0].Image.FilePath);
    }

    [Fact]
    public async Task GenerateAsync_DoesNotReturnSuggestionsWhenPhotoChangesDuringPrediction()
    {
        using var files = new TemporaryPhotos();
        var image = files.CreateImage("changed.jpg");
        var tagger = new FakeTagger(
            [new TagPrediction("landscape", 0.76f)],
            afterPrediction: path =>
            {
                File.WriteAllText(path, "replacement with a different size");
                File.SetLastWriteTimeUtc(
                    path,
                    image.DateModified.AddSeconds(2));
            });
        var processor = new SelectedPhotoAutoTagReviewProcessor(
            new FakeModelProvider(),
            tagger);

        var result = await processor.GenerateAsync(
            [image],
            CreateApprovedSettings(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsCanceled);
        Assert.Contains("changed after it was selected", result.Items[0].Error);
        Assert.Empty(result.Items[0].Predictions);
    }

    [Fact]
    public async Task GenerateAsync_RequiresApprovedProfile()
    {
        using var files = new TemporaryPhotos();
        var processor = new SelectedPhotoAutoTagReviewProcessor(
            new FakeModelProvider(),
            new FakeTagger([]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.GenerateAsync(
                [files.CreateImage("photo.jpg")],
                new AutoTaggingSettings(
                    true,
                    AutoTagModelCatalog.MobileNetV2ProfileId),
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GenerateAsync_PropagatesUnavailableModelError()
    {
        using var files = new TemporaryPhotos();
        var processor = new SelectedPhotoAutoTagReviewProcessor(
            new FakeModelProvider(
                new InvalidOperationException("Model files are unavailable.")),
            new FakeTagger([]));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.GenerateAsync(
                [files.CreateImage("photo.jpg")],
                CreateApprovedSettings(),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("Model files are unavailable.", exception.Message);
    }

    private static AutoTaggingSettings CreateApprovedSettings()
    {
        var profile = AutoTagModelCatalog.MobileNetV2;
        return new AutoTaggingSettings(
            false,
            profile.Id,
            profile.DefaultMaximumTags,
            profile.DefaultConfidenceThreshold,
            ProfileQualityStatuses: new Dictionary<string, AutoTagQualityStatus>
            {
                [profile.ApprovalKey] = AutoTagQualityStatus.Approved
            });
    }

    private sealed class TemporaryPhotos : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-{Guid.NewGuid():N}");

        public TemporaryPhotos() => Directory.CreateDirectory(_directory);

        public ImageEntry CreateImage(
            string fileName,
            MediaType mediaType = MediaType.Image)
        {
            var path = Path.Combine(_directory, fileName);
            File.WriteAllText(path, "test photo");
            var modified = new DateTime(
                2026,
                1,
                2,
                3,
                4,
                5,
                DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, modified);
            return new ImageEntry
            {
                Id = 1,
                FilePath = path,
                FileName = fileName,
                FileSize = new FileInfo(path).Length,
                DateModified = File.GetLastWriteTimeUtc(path),
                DateIndexed = modified,
                MediaType = mediaType
            };
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeModelProvider(Exception? error = null) : IAutoTagModelProvider
    {
        public int EnsureCount { get; private set; }

        public Task EnsureModelAsync(
            string profileId,
            string? modelDirectory,
            CancellationToken cancellationToken = default)
        {
            EnsureCount++;
            return error is null ? Task.CompletedTask : Task.FromException(error);
        }

        public string GetAssetPath(
            string profileId,
            AutoTagAssetDefinition asset,
            string? modelDirectory) =>
            Path.Combine(modelDirectory ?? Path.GetTempPath(), profileId, asset.FileName);
    }

    private sealed class FakeTagger(
        IReadOnlyList<TagPrediction> predictions,
        string? failurePath = null,
        Action<string>? afterPrediction = null) : IAutoTagger
    {
        public List<string> RequestedPaths { get; } = [];
        public string? LoadedProfileId { get; private set; }

        public void LoadModel(string profileId, string? modelDirectory) =>
            LoadedProfileId = profileId;

        public Task<IReadOnlyList<TagPrediction>> PredictTagsAsync(
            string imagePath,
            string profileId,
            string? modelDirectory,
            int maximumTags,
            float confidenceThreshold,
            CancellationToken cancellationToken = default)
        {
            RequestedPaths.Add(imagePath);
            if (imagePath == failurePath)
                throw new InvalidDataException("Unreadable photo");

            afterPrediction?.Invoke(imagePath);
            return Task.FromResult(predictions);
        }
    }

    private sealed class DelegateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
