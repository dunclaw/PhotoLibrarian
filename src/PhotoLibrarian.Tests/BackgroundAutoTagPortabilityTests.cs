// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class BackgroundAutoTagPortabilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"PhotoLibrarian-background-tag-portability-{Guid.NewGuid():N}");

    public BackgroundAutoTagPortabilityTests() =>
        Directory.CreateDirectory(_directory);

    [Fact]
    public async Task BackgroundTags_MoveWithXmpAndRestoreIntoFreshCache()
    {
        var imagePath = Path.Combine(_directory, "source", "photo.raw");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
        var image = await CreateIndexedImageAsync(imagePath);
        var tags = new TagRepository(_database!);
        await tags.AddTagAsync(new ImageTag
        {
            ImageId = image.Id,
            Tag = "family",
            Source = TagSource.Manual,
            Confidence = 1.0f
        });

        var processor = CreateProcessor(tags);
        var errors = new List<string>();
        processor.Progress += (_, progress) =>
        {
            if (!string.IsNullOrEmpty(progress.Error))
                errors.Add(progress.Error);
        };
        var result = await processor
            .ProcessLibraryAsync(CreateApprovedSettings());

        Assert.Empty(errors);
        Assert.Equal(new BatchTagResult(1, 0, 1), result);
        Assert.Equal(
            AutoTagModelCatalog.MobileNetV2.PipelineVersion,
            (await _images!.GetByPathAsync(imagePath))!.AutoTagScanVersion);
        Assert.Contains(
            await tags.GetTagsAsync(image.Id),
            tag => tag.Tag == "family" &&
                tag.Source == TagSource.Manual);

        var movedPath = Path.Combine(_directory, "moved", "photo.raw");
        Directory.CreateDirectory(Path.GetDirectoryName(movedPath)!);
        File.Move(imagePath, movedPath);
        File.Move(
            Path.ChangeExtension(imagePath, ".xmp"),
            Path.ChangeExtension(movedPath, ".xmp"));

        using var freshDatabase = new CacheDatabase(
            Path.Combine(_directory, "fresh-cache.db"));
        await freshDatabase.InitializeAsync();
        var freshImages = new ImageRepository(freshDatabase);
        var freshTags = new TagRepository(freshDatabase);
        using var scanner = new FolderScannerService();
        var indexer = new LibraryIndexingService(
            freshDatabase,
            freshImages,
            freshTags,
            new FaceRepository(freshDatabase),
            scanner,
            new MetadataReaderService(),
            new FaceMetadataStore());

        Assert.True(await indexer.IndexFileAsync(movedPath));
        var indexed = (await freshImages.GetByPathAsync(movedPath))!;
        var restored = await freshTags.GetTagsAsync(indexed.Id);
        Assert.Equal(
            TagSource.Manual,
            Assert.Single(restored, tag => tag.Tag == "family").Source);
        Assert.Equal(
            TagSource.AutoML,
            Assert.Single(restored, tag => tag.Tag == "Auto/dog").Source);
        Assert.Equal(
            0.87f,
            Assert.Single(restored, tag => tag.Tag == "Auto/dog").Confidence);
        Assert.Equal(
            AutoTagModelCatalog.MobileNetV2.PipelineVersion,
            indexed.AutoTagScanVersion);
    }

    [Fact]
    public async Task BackgroundMetadataFailureLeavesPhotoPendingForRetry()
    {
        var imagePath = Path.Combine(_directory, "retry.raw");
        await File.WriteAllBytesAsync(imagePath, [4, 5, 6]);
        var image = await CreateIndexedImageAsync(imagePath);
        var tags = new TagRepository(_database!);
        await tags.AddTagAsync(new ImageTag
        {
            ImageId = image.Id,
            Tag = "manual",
            Source = TagSource.Manual
        });
        await tags.AddTagAsync(new ImageTag
        {
            ImageId = image.Id,
            Tag = "Auto/cat",
            Source = TagSource.AutoML,
            Confidence = 0.72f
        });
        Directory.CreateDirectory(Path.ChangeExtension(imagePath, ".xmp"));
        var processor = CreateProcessor(tags);
        var errors = new List<string>();
        processor.Progress += (_, progress) =>
        {
            if (!string.IsNullOrEmpty(progress.Error))
                errors.Add(progress.Error);
        };

        var failed = await processor.ProcessLibraryAsync(
            CreateApprovedSettings());

        Assert.Equal(new BatchTagResult(1, 1, 0), failed);
        Assert.Single(errors);
        Assert.Single(await tags.GetImagesNeedingAutoTagsAsync(
            AutoTagModelCatalog.MobileNetV2.PipelineVersion));
        Assert.Contains(
            await tags.GetTagsAsync(image.Id),
            tag => tag.Tag == "manual" &&
                tag.Source == TagSource.Manual);
        Assert.Contains(
            await tags.GetTagsAsync(image.Id),
            tag => tag.Tag == "Auto/cat" &&
                tag.Source == TagSource.AutoML &&
                tag.Confidence == 0.72f);
        Assert.DoesNotContain(
            await tags.GetTagsAsync(image.Id),
            tag => tag.Tag == "Auto/dog");

        Directory.Delete(Path.ChangeExtension(imagePath, ".xmp"));
        var retried = await processor.ProcessLibraryAsync(
            CreateApprovedSettings());

        Assert.Equal(new BatchTagResult(1, 0, 1), retried);
        Assert.Empty(await tags.GetImagesNeedingAutoTagsAsync(
            AutoTagModelCatalog.MobileNetV2.PipelineVersion));
        Assert.Contains(
            await tags.GetTagsAsync(image.Id),
            tag => tag.Tag == "manual" &&
                tag.Source == TagSource.Manual);
        Assert.DoesNotContain(
            await tags.GetTagsAsync(image.Id),
            tag => tag.Tag == "Auto/cat");
        var portable = new MetadataReaderService()
            .ReadPortableTagMetadata(imagePath);
        Assert.NotNull(portable);
        Assert.Equal(
            AutoTagModelCatalog.MobileNetV2.PipelineVersion,
            portable.AutoTagScanVersion);
    }

    [Fact]
    public async Task BackgroundTaggingDoesNotOverwriteNewerSidecarEdits()
    {
        var imagePath = Path.Combine(_directory, "race.raw");
        var sidecarPath = Path.ChangeExtension(imagePath, ".xmp");
        await File.WriteAllBytesAsync(imagePath, [7, 8, 9]);
        await File.WriteAllTextAsync(sidecarPath, "user metadata before inference");
        var image = await CreateIndexedImageAsync(imagePath);
        var tags = new TagRepository(_database!);
        await tags.AddTagAsync(new ImageTag
        {
            ImageId = image.Id,
            Tag = "Auto/cat",
            Source = TagSource.AutoML,
            Confidence = 0.72f
        });
        var newerSidecar = "newer user metadata";
        var processor = CreateProcessor(
            tags,
            () => File.WriteAllText(sidecarPath, newerSidecar));

        var result = await processor.ProcessLibraryAsync(
            CreateApprovedSettings());

        Assert.Equal(new BatchTagResult(1, 1, 0), result);
        Assert.Equal(newerSidecar, await File.ReadAllTextAsync(sidecarPath));
        var storedTags = await tags.GetTagsAsync(image.Id);
        Assert.Contains(
            storedTags,
            tag => tag.Tag == "Auto/cat" &&
                tag.Source == TagSource.AutoML &&
                tag.Confidence == 0.72f);
        Assert.DoesNotContain(storedTags, tag => tag.Tag == "Auto/dog");
        Assert.Single(await tags.GetImagesNeedingAutoTagsAsync(
            AutoTagModelCatalog.MobileNetV2.PipelineVersion));
    }

    private CacheDatabase? _database;
    private ImageRepository? _images;

    private async Task<ImageEntry> CreateIndexedImageAsync(string imagePath)
    {
        if (_database is null)
        {
            _database = new CacheDatabase(
                Path.Combine(_directory, "library.db"));
            await _database.InitializeAsync();
            _images = new ImageRepository(_database);
        }

        var file = new FileInfo(imagePath);
        var image = new ImageEntry
        {
            FilePath = imagePath,
            FileName = file.Name,
            FileSize = file.Length,
            DateModified = file.LastWriteTimeUtc,
            DateIndexed = DateTime.UtcNow,
            MediaType = MediaType.Image
        };
        image.Id = await _images!.UpsertImageAsync(image);
        return image;
    }

    private static BatchTagProcessor CreateProcessor(
        IAutoTagStore store,
        Action? beforePrediction = null) =>
        new(
            store,
            new FakeModelProvider(),
            new FakeTagger(
                [new TagPrediction("dog", 0.87f)],
                beforePrediction),
            new IdleActivityGate());

    private static AutoTaggingSettings CreateApprovedSettings()
    {
        var profile = AutoTagModelCatalog.MobileNetV2;
        return new AutoTaggingSettings(
            true,
            profile.Id,
            profile.DefaultMaximumTags,
            profile.DefaultConfidenceThreshold,
            ProfileQualityStatuses: new Dictionary<string, AutoTagQualityStatus>
            {
                [profile.ApprovalKey] = AutoTagQualityStatus.Approved
            });
    }

    public void Dispose()
    {
        _database?.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
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
            Path.Combine(
                modelDirectory ?? Path.GetTempPath(),
                profileId,
                asset.FileName);
    }

    private sealed class FakeTagger(
        IReadOnlyList<TagPrediction> predictions,
        Action? beforePrediction = null) : IAutoTagger
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
            PredictAsync();

        private Task<IReadOnlyList<TagPrediction>> PredictAsync()
        {
            beforePrediction?.Invoke();
            return Task.FromResult(predictions);
        }
    }

    private sealed class IdleActivityGate : IBackgroundActivityGate
    {
        public Task WaitForIdleAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
