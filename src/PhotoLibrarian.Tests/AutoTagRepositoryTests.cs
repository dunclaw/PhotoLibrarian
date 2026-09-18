using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class AutoTagRepositoryTests
{
    [Fact]
    public async Task TryReplaceAutoTagsAsync_PreservesManualTagsAndTracksVersion()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var tags = new TagRepository(database);
            var image = CreateImage();
            image.Id = await images.UpsertImageAsync(image);
            await tags.AddTagAsync(new ImageTag
            {
                ImageId = image.Id,
                Tag = "family",
                Source = TagSource.Manual
            });

            var wasSaved = await tags.TryReplaceAutoTagsAsync(
                image,
                [
                    new GeneratedImageTag("golden retriever", 0.82f),
                    new GeneratedImageTag("park", 0.64f)
                ],
                "test-model-v1",
                TestContext.Current.CancellationToken);

            Assert.True(wasSaved);
            var storedTags = await tags.GetTagsAsync(image.Id);
            Assert.Contains(
                storedTags,
                tag => tag.Tag == "family" && tag.Source == TagSource.Manual);
            Assert.Contains(
                storedTags,
                tag => tag.Tag == ImageTag.AutomaticRootTag &&
                    tag.Source == TagSource.AutoML);
            Assert.Contains(
                storedTags,
                tag => tag.Tag == "Auto/golden retriever" &&
                    tag.Source == TagSource.AutoML);
            Assert.Empty(await tags.GetImagesNeedingAutoTagsAsync(
                "test-model-v1",
                TestContext.Current.CancellationToken));

            image.FileSize++;
            image.DateModified = image.DateModified.AddSeconds(1);
            await images.UpsertImageAsync(image);

            Assert.Single(await tags.GetImagesNeedingAutoTagsAsync(
                "test-model-v1",
                TestContext.Current.CancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task HierarchicalAutoTags_IndexEveryAncestorForNavigationAndRefinement()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var tags = new TagRepository(database);
            var image = CreateImage();
            image.Id = await images.UpsertImageAsync(image);
            Assert.True(await tags.TryReplaceAutoTagsAsync(image,
                [new("animal/bird/eagle", 0.8f), new("animal/bird", 0.95f),
                 new("animal/bird/duck", 0.7f)], "hierarchical-v2", TestContext.Current.CancellationToken));

            var stored = await tags.GetTagsAsync(image.Id);
            Assert.Equal(5, stored.Count);
            Assert.All(stored, tag => Assert.Equal(TagSource.AutoML, tag.Source));
            foreach (var path in new[] { "Auto", "Auto/animal", "Auto/animal/bird" })
            {
                Assert.Equal(0.95f, Assert.Single(stored, tag => tag.Tag == path).Confidence);
                Assert.Equal([image.Id], await tags.GetImageIdsWithTagAsync(path));
                Assert.Equal(image.Id,
                    Assert.Single(await images.GetFilteredAsync(false, [path])).Id);
            }
            Assert.Equal(0.8f, Assert.Single(stored,
                tag => tag.Tag == "Auto/animal/bird/eagle").Confidence);
            Assert.Empty(await tags.GetImageIdsWithTagAsync("animal"));
            var byImage = await tags.GetTagsByImageIdAsync();
            Assert.True(new ImageRefinementFilter { IncludedTags = ["Auto/animal"] }
                .Matches(image, byImage[image.Id]));
            Assert.False(new ImageRefinementFilter { ExcludedTags = ["Auto/animal/bird"] }
                .Matches(image, byImage[image.Id]));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    [Theory]
    [InlineData(TagSource.Manual)]
    [InlineData(TagSource.Imported)]
    [InlineData(TagSource.Metadata)]
    public async Task HierarchyRescan_ReplacesOldAutoTagsWithoutOverwritingUserPaths(TagSource source)
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var tags = new TagRepository(database);
            var image = CreateImage();
            image.Id = await images.UpsertImageAsync(image);
            await tags.TryReplaceAutoTagsAsync(image, [new("eagle", 0.8f)], "flat-v1",
                TestContext.Current.CancellationToken);
            await tags.AddTagAsync(new ImageTag
            {
                ImageId = image.Id, Tag = "Auto/animal/bird", Source = source, Confidence = 0.4f
            });
            await tags.AddTagAsync(new ImageTag { ImageId = image.Id, Tag = "family", Source = source });
            Assert.Single(await tags.GetImagesNeedingAutoTagsAsync("hierarchical-v2",
                TestContext.Current.CancellationToken));

            Assert.True(await tags.TryReplaceAutoTagsAsync(image,
                [new("animal/bird/eagle", 0.9f)], "hierarchical-v2", TestContext.Current.CancellationToken));
            var stored = await tags.GetTagsAsync(image.Id);
            Assert.DoesNotContain(stored, tag => tag.Tag == "Auto/eagle");
            Assert.Equal(TagSource.AutoML,
                Assert.Single(stored, tag => tag.Tag == "Auto/animal/bird/eagle").Source);
            foreach (var path in new[] { "Auto", "Auto/animal", "Auto/animal/bird" })
            {
                var tag = Assert.Single(stored, tag => tag.Tag == path);
                Assert.Equal(source, tag.Source);
                Assert.Equal(0.4f, tag.Confidence);
            }
            Assert.Empty(await tags.GetImagesNeedingAutoTagsAsync("hierarchical-v2",
                TestContext.Current.CancellationToken));
            await tags.TryReplaceAutoTagsAsync(image, [], "empty-v3", TestContext.Current.CancellationToken);
            stored = await tags.GetTagsAsync(image.Id);
            Assert.Equal(4, stored.Count);
            Assert.All(stored, tag => Assert.Equal(source, tag.Source));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task TryReplaceAutoTagsAsync_RejectsStaleImage()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var tags = new TagRepository(database);
            var image = CreateImage();
            image.Id = await images.UpsertImageAsync(image);
            image.FileSize++;

            var wasSaved = await tags.TryReplaceAutoTagsAsync(
                image,
                [new GeneratedImageTag("cat", 0.9f)],
                "test-model-v1",
                TestContext.Current.CancellationToken);

            Assert.False(wasSaved);
            Assert.Empty(await tags.GetTagsAsync(image.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task RemoveAllAutoTagsAsync_PreservesOtherSourcesAndMakesPhotosEligible()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var tags = new TagRepository(database);
            var first = CreateImage();
            first.Id = await images.UpsertImageAsync(first);
            var second = CreateImage();
            second.FilePath = @"C:\Photos\second.jpg";
            second.FileName = "second.jpg";
            second.Id = await images.UpsertImageAsync(second);

            await tags.TryReplaceAutoTagsAsync(
                first,
                [new("animal/bird/eagle", 0.8f)],
                "profile-v1",
                TestContext.Current.CancellationToken);
            await tags.TryReplaceAutoTagsAsync(
                second,
                [new("landscape", 0.7f)],
                "profile-v1",
                TestContext.Current.CancellationToken);
            await tags.AddTagAsync(new ImageTag
            {
                ImageId = first.Id,
                Tag = "family",
                Source = TagSource.Manual
            });
            await tags.AddTagAsync(new ImageTag
            {
                ImageId = first.Id,
                Tag = "camera",
                Source = TagSource.Metadata
            });
            await tags.AddTagAsync(new ImageTag
            {
                ImageId = second.Id,
                Tag = "vacation",
                Source = TagSource.Imported
            });

            var result = await tags.RemoveAllAutoTagsAsync(
                TestContext.Current.CancellationToken);

            Assert.Equal(6, result.TagCount);
            Assert.Equal(2, result.PhotoCount);
            var firstTags = await tags.GetTagsAsync(first.Id);
            Assert.Equal(
                ["camera", "family"],
                firstTags.Select(tag => tag.Tag)
                    .Order(StringComparer.Ordinal));
            Assert.Contains(
                firstTags,
                tag => tag.Tag == "camera" &&
                    tag.Source == TagSource.Metadata);
            Assert.Equal(
                ["vacation"],
                (await tags.GetTagsAsync(second.Id))
                    .Select(tag => tag.Tag));
            Assert.Equal(
                2,
                (await tags.GetImagesNeedingAutoTagsAsync(
                    "profile-v1",
                    TestContext.Current.CancellationToken)).Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    private static ImageEntry CreateImage() => new()
    {
        FilePath = @"C:\Photos\sample.jpg",
        FileName = "sample.jpg",
        FileSize = 100,
        DateModified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        DateIndexed = DateTime.UtcNow
    };

    private static string CreateDatabasePath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}.db");
}
