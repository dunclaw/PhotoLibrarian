// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ML.Services;
using PhotoLibrarian.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class AutoTagReviewViewModelTests
{
    [Fact]
    public async Task SuggestionsAreNotWrittenUntilApprovedAndRejectedTagsStayOut()
    {
        using var fixture = new ReviewFixture();
        var image = await fixture.CreatePhotoAsync();
        await fixture.Tags.AddTagAsync(new ImageTag
        {
            ImageId = image.Id,
            Tag = "family",
            Source = TagSource.Manual
        });
        var review = fixture.CreateReview(
            new TagPrediction("dog", 0.9f),
            new TagPrediction("park", 0.7f));

        await review.OpenForImagesAsync([image]);

        Assert.DoesNotContain(
            await fixture.Tags.GetTagsAsync(image.Id),
            tag => tag.Source == TagSource.AutoML);
        Assert.False(File.Exists(Path.ChangeExtension(image.FilePath, ".xmp")));

        var photo = Assert.Single(review.Photos);
        Assert.Equal(2, photo.Suggestions.Count);
        photo.Suggestions[1].IsAccepted = false;
        await review.ApplyAsync();

        var stored = await fixture.Tags.GetTagsAsync(image.Id);
        Assert.Contains(
            stored,
            tag => tag.Tag == "family" &&
                tag.Source == TagSource.Manual);
        Assert.Contains(
            stored,
            tag => tag.Tag == "Auto/dog" &&
                tag.Source == TagSource.AutoML);
        Assert.DoesNotContain(stored, tag => tag.Tag == "Auto/park");
        Assert.Contains("Auto/dog", TagWriterService.ReadTagsFromSidecar(
            image.FilePath));
        Assert.DoesNotContain("Auto/park", TagWriterService.ReadTagsFromSidecar(
            image.FilePath));
        Assert.False(review.IsOpen);
    }

    [Fact]
    public async Task MetadataFailureIsReportedAndCanBeRetriedWithoutLosingTags()
    {
        using var fixture = new ReviewFixture();
        var image = await fixture.CreatePhotoAsync();
        var sidecarPath = Path.ChangeExtension(image.FilePath, ".xmp");
        Directory.CreateDirectory(sidecarPath);
        var review = fixture.CreateReview(new TagPrediction("bird", 0.88f));
        await review.OpenForImagesAsync([image]);

        await review.ApplyAsync();

        var photo = Assert.Single(review.Photos);
        Assert.True(photo.HasError);
        Assert.True(photo.CanRetrySave);
        Assert.True(review.CanApply);
        Assert.Contains(
            await fixture.Tags.GetTagsAsync(image.Id),
            tag => tag.Tag == "Auto/bird" &&
                tag.Source == TagSource.AutoML);

        Directory.Delete(sidecarPath);
        await review.ApplyAsync();

        Assert.False(photo.HasError);
        Assert.True(photo.IsCommitted);
        Assert.False(review.IsOpen);
        Assert.Contains(
            "Auto/bird",
            TagWriterService.ReadTagsFromSidecar(image.FilePath));
        Assert.Equal(
            2,
            (await fixture.Tags.GetTagsAsync(image.Id))
                .Count(tag => tag.Source == TagSource.AutoML));
    }

    private sealed class ReviewFixture : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-review-{Guid.NewGuid():N}");
        private readonly CacheDatabase _database;

        public TagRepository Tags { get; }
        private readonly ImageRepository _images;

        public ReviewFixture()
        {
            Directory.CreateDirectory(_directory);
            _database = new CacheDatabase(Path.Combine(_directory, "library.db"));
            _database.InitializeAsync().GetAwaiter().GetResult();
            Tags = new TagRepository(_database);
            _images = new ImageRepository(_database);
        }

        public async Task<ImageEntry> CreatePhotoAsync()
        {
            var path = Path.Combine(_directory, "photo.raw");
            await File.WriteAllTextAsync(path, "synthetic photo");
            var image = new ImageEntry
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                FileSize = new FileInfo(path).Length,
                DateModified = File.GetLastWriteTimeUtc(path),
                DateIndexed = DateTime.UtcNow,
                MediaType = MediaType.Image
            };
            image.Id = await _images.UpsertImageAsync(image);
            return image;
        }

        public AutoTagReviewViewModel CreateReview(
            params TagPrediction[] predictions)
        {
            var profile = AutoTagModelCatalog.MobileNetV2;
            var settings = new AutoTaggingSettings(
                false,
                profile.Id,
                profile.DefaultMaximumTags,
                profile.DefaultConfidenceThreshold,
                ProfileQualityStatuses: new Dictionary<string, AutoTagQualityStatus>
                {
                    [profile.ApprovalKey] = AutoTagQualityStatus.Approved
                });
            return new AutoTagReviewViewModel(
                new SelectedPhotoAutoTagReviewProcessor(
                    new FakeModelProvider(),
                    new FakeTagger(predictions)),
                Tags,
                () => settings);
        }

        public void Dispose()
        {
            _database.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
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
            Path.Combine(modelDirectory ?? Path.GetTempPath(), profileId, asset.FileName);
    }

    private sealed class FakeTagger(
        IReadOnlyList<TagPrediction> predictions) : IAutoTagger
    {
        public void LoadModel(string profileId, string? modelDirectory)
        {
        }

        public Task<IReadOnlyList<TagPrediction>> PredictTagsAsync(
            string imagePath,
            string profileId,
            string? modelDirectory,
            int maximumTags,
            float confidenceThreshold,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(predictions);
    }
}
