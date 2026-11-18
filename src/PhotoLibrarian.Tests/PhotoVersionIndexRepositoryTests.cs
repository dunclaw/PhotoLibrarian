// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PhotoVersionIndexRepositoryTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"PhotoLibrarian-version-index-{Guid.NewGuid():N}");
    private CacheDatabase _database = null!;
    private ImageRepository _images = null!;
    private PhotoVersionIndexRepository _index = null!;
    private EditHistoryService _history = null!;
    private PhotoVersionRecipeStore _store = null!;
    private CacheDatabase? _freshDatabase;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _database = new CacheDatabase(Path.Combine(_directory, "cache.db"));
        await _database.InitializeAsync();
        _images = new(_database);
        _index = new(_database);
        _history = new();
        _store = new(Path.Combine(_directory, "history"), _history, () => Task.CompletedTask);
    }

    public ValueTask DisposeAsync()
    {
        _history.Clear();
        ClearPool(_database);
        if (_freshDatabase is not null) ClearPool(_freshDatabase);
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static void ClearPool(CacheDatabase database)
    {
        using var connection = database.CreateConnection();
        database.Dispose();
        SqliteConnection.ClearPool(connection);
    }

    [Fact]
    public async Task ReconciliationPreservesKnownVersionsOnMissingOrInvalidMetadata()
    {
        var (path, imageId, family) = await CreateFamilyAsync();
        Assert.Equal(PhotoVersionIndexWriteStatus.Applied,
            await _index.ReconcileAsync(imageId, path, null, family));
        var indexed = (await _index.GetAsync(imageId))!;
        Assert.Equal(PhotoVersionRecipeStatus.Success, indexed.Status);
        Assert.Equal(family.DocumentRevision, indexed.DocumentRevision);
        Assert.Equal(family.InputIdentity, indexed.InputIdentity);
        Assert.Equal(family.Records!.Select(record => record.Target.VariantId),
            indexed.Versions.Select(record => record.VariantId));
        Assert.True(indexed.Versions[0].IsMain);
        Assert.False(indexed.Versions[1].IsMain);

        Assert.Equal(PhotoVersionIndexWriteStatus.Applied,
            await _index.ReconcileAsync(imageId, path, indexed.Generation,
                new(PhotoVersionRecipeStatus.VersionMissing, Message: "Metadata is missing.")));
        var missing = (await _index.GetAsync(imageId))!;
        Assert.Equal(PhotoVersionRecipeStatus.VersionMissing, missing.Status);
        Assert.Equal(indexed.SourceId, missing.SourceId);
        Assert.Equal(indexed.Versions, missing.Versions);
        Assert.Equal(PhotoVersionIndexWriteStatus.Stale,
            await _index.ReconcileAsync(imageId, path, indexed.Generation, family));
        var afterStale = (await _index.GetAsync(imageId))!;
        Assert.Equal(missing.Generation, afterStale.Generation);
        Assert.Equal(missing.Status, afterStale.Status);
        Assert.Equal(missing.Versions, afterStale.Versions);

        Assert.Equal(PhotoVersionIndexWriteStatus.Applied,
            await _index.ReconcileAsync(imageId, path, missing.Generation,
                new(PhotoVersionRecipeStatus.InvalidMetadata, Message: "Invalid metadata.")));
        var invalid = (await _index.GetAsync(imageId))!;
        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata, invalid.Status);
        Assert.Equal(indexed.Versions, invalid.Versions);
        var reread = await _store.ListAsync(path);
        Assert.Equal(PhotoVersionIndexWriteStatus.Applied,
            await _index.ReconcileAsync(imageId, path, invalid.Generation, reread));
        Assert.Equal(PhotoVersionRecipeStatus.Success, (await _index.GetAsync(imageId))!.Status);
    }

    [Fact]
    public async Task UnknownVersionRemainsInOrderAndMalformedReconciliationCannotReplaceIt()
    {
        var (path, imageId, family) = await CreateFamilyAsync();
        var records = family.Records!.ToArray();
        records[1] = records[1] with { Status = PhotoVersionRecipeStatus.Unsupported, Snapshot = null };
        var partiallyReadable = family with { Records = records };
        Assert.Equal(PhotoVersionIndexWriteStatus.Applied,
            await _index.ReconcileAsync(imageId, path, null, partiallyReadable));
        var indexed = (await _index.GetAsync(imageId))!;
        Assert.Equal(PhotoVersionRecipeStatus.Success, indexed.Versions[0].Status);
        Assert.Equal(PhotoVersionRecipeStatus.Unsupported, indexed.Versions[1].Status);
        Assert.Equal(records[1].Target.VariantId, indexed.Versions[1].VariantId);

        records[1] = records[0] with { IsMain = false };
        Assert.Equal(PhotoVersionIndexWriteStatus.Invalid,
            await _index.ReconcileAsync(imageId, path, indexed.Generation, family with { Records = records }));
        var unchanged = (await _index.GetAsync(imageId))!;
        Assert.Equal(indexed.Generation, unchanged.Generation);
        Assert.Equal(indexed.Versions, unchanged.Versions);
    }

    [Fact]
    public async Task ConcurrentReconciliationsCannotOverwriteTheWinningGeneration()
    {
        var (path, imageId, family) = await CreateFamilyAsync();
        Assert.Equal(PhotoVersionIndexWriteStatus.Applied,
            await _index.ReconcileAsync(imageId, path, null, family));
        var generation = (await _index.GetAsync(imageId))!.Generation;
        using var start = new ManualResetEventSlim();
        var first = Task.Run(async () =>
        {
            start.Wait();
            return await _index.ReconcileAsync(imageId, path, generation, family);
        });
        var second = Task.Run(async () =>
        {
            start.Wait();
            return await _index.ReconcileAsync(imageId, path, generation,
                new(PhotoVersionRecipeStatus.Conflict, Message: "Changed during indexing."));
        });
        start.Set();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, status => status == PhotoVersionIndexWriteStatus.Applied);
        Assert.Single(results, status => status == PhotoVersionIndexWriteStatus.Stale);
        var indexed = (await _index.GetAsync(imageId))!;
        Assert.NotEqual(generation, indexed.Generation);
        Assert.Equal(family.Records!.Select(record => record.Target.VariantId),
            indexed.Versions.Select(record => record.VariantId));
        Assert.Equal(results[0] == PhotoVersionIndexWriteStatus.Applied
            ? PhotoVersionRecipeStatus.Success : PhotoVersionRecipeStatus.Conflict, indexed.Status);
    }

    [Fact]
    public async Task ReadFromOldPathCannotMarkRenamedPhotoUnavailable()
    {
        var (path, imageId, family) = await CreateFamilyAsync();
        Assert.Equal(PhotoVersionIndexWriteStatus.Applied,
            await _index.ReconcileAsync(imageId, path, null, family));
        var indexed = (await _index.GetAsync(imageId))!;
        var renamed = Path.Combine(_directory, "renamed.cr3");
        File.Move(path, renamed);
        File.Move(Path.ChangeExtension(path, ".xmp"), Path.ChangeExtension(renamed, ".xmp"));
        await _images.UpdatePathAsync(imageId, renamed, null);

        Assert.Equal(PhotoVersionIndexWriteStatus.Stale,
            await _index.ReconcileAsync(imageId, path, indexed.Generation,
                new(PhotoVersionRecipeStatus.SourceMissing)));
        Assert.Equal(indexed.Generation, (await _index.GetAsync(imageId))!.Generation);
        Assert.Equal(PhotoVersionIndexWriteStatus.Applied,
            await _index.ReconcileAsync(imageId, renamed, indexed.Generation, await _store.ListAsync(renamed)));
        Assert.Equal(PhotoVersionRecipeStatus.Success, (await _index.GetAsync(imageId))!.Status);
    }

    [Fact]
    public async Task OldFamilyReadCannotReplaceNewerImageMetadata()
    {
        var (path, imageId, family) = await CreateFamilyAsync();
        var image = (await _images.GetByPathAsync(path))!;
        image.MetadataHash = "newer";
        await _images.UpsertImageAsync(image);

        Assert.Equal(PhotoVersionIndexWriteStatus.Stale,
            await _index.ReconcileAsync(imageId, path, null, family,
                expectedImageMetadataHash: "older"));
        Assert.Null(await _index.GetAsync(imageId));
        Assert.Equal(PhotoVersionIndexWriteStatus.Applied,
            await _index.ReconcileAsync(imageId, path, null, family,
                expectedImageMetadataHash: "newer"));
    }

    [Fact]
    public async Task IdenticalPortableIdsRemainSeparateForPhysicalPhotos()
    {
        var (path, imageId, family) = await CreateFamilyAsync();
        Assert.Equal(PhotoVersionIndexWriteStatus.Applied,
            await _index.ReconcileAsync(imageId, path, null, family));
        var copyDirectory = Path.Combine(_directory, "copy");
        Directory.CreateDirectory(copyDirectory);
        var copyPath = Path.Combine(copyDirectory, Path.GetFileName(path));
        File.Copy(path, copyPath);
        File.Copy(Path.ChangeExtension(path, ".xmp"), Path.ChangeExtension(copyPath, ".xmp"));
        var copyId = await _images.UpsertImageAsync(new ImageEntry
        {
            FilePath = copyPath, FileName = Path.GetFileName(copyPath), DateModified = DateTime.UtcNow
        });
        var copyFamily = await _store.ListAsync(copyPath);
        Assert.Equal(PhotoVersionIndexWriteStatus.Applied,
            await _index.ReconcileAsync(copyId, copyPath, null, copyFamily));
        var original = (await _index.GetAsync(imageId))!;
        var copy = (await _index.GetAsync(copyId))!;
        Assert.Equal(original.SourceId, copy.SourceId);
        Assert.Equal(original.Versions, copy.Versions);
        Assert.NotEqual(original.Generation, copy.Generation);

        await _images.DeleteByPathAsync(path);
        Assert.Null(await _index.GetAsync(imageId));
        Assert.Equal(copy.Generation, (await _index.GetAsync(copyId))!.Generation);
    }

    [Fact]
    public async Task FailedReplacementRollsBackTheWholeIndexProjection()
    {
        var (path, imageId, family) = await CreateFamilyAsync();
        Assert.Equal(PhotoVersionIndexWriteStatus.Applied,
            await _index.ReconcileAsync(imageId, path, null, family));
        var before = (await _index.GetAsync(imageId))!;
        using (var connection = _database.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_version_insert BEFORE INSERT ON photo_version_index
                WHEN NEW.position = 1 BEGIN SELECT RAISE(ABORT, 'injected failure'); END
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(
            () => _index.ReconcileAsync(imageId, path, before.Generation, family));
        var after = (await _index.GetAsync(imageId))!;
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Versions, after.Versions);
    }

    [Fact]
    public async Task SuccessfulRemovalReconcilesOnlyTheDeletedVersion()
    {
        var (path, imageId, family) = await CreateFamilyAsync();
        Assert.Equal(PhotoVersionIndexWriteStatus.Applied,
            await _index.ReconcileAsync(imageId, path, null, family));
        var before = (await _index.GetAsync(imageId))!;
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            (await _store.RemoveCopyAsync(family.Records![1].Snapshot!)).Status);
        Assert.Equal(PhotoVersionIndexWriteStatus.Applied,
            await _index.ReconcileAsync(imageId, path, before.Generation, await _store.ListAsync(path)));
        var after = (await _index.GetAsync(imageId))!;
        Assert.Single(after.Versions);
        Assert.Equal(before.Versions[0], after.Versions[0]);
        Assert.Equal(before.SourceId, after.SourceId);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task CancelledReconciliationLeavesThePriorGenerationIntact()
    {
        var (path, imageId, family) = await CreateFamilyAsync();
        Assert.Equal(PhotoVersionIndexWriteStatus.Applied,
            await _index.ReconcileAsync(imageId, path, null, family));
        var before = (await _index.GetAsync(imageId))!;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _index.ReconcileAsync(imageId, path, before.Generation,
                new(PhotoVersionRecipeStatus.VersionMissing), cancellation.Token));
        var after = (await _index.GetAsync(imageId))!;
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Versions, after.Versions);
    }

    [Fact]
    public async Task MissingFamilyOnFirstScanDoesNotInventVersions()
    {
        var path = Path.Combine(_directory, "unedited.cr3");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var imageId = await _images.UpsertImageAsync(new ImageEntry
        {
            FilePath = path, FileName = Path.GetFileName(path), DateModified = DateTime.UtcNow
        });
        var family = await _store.ListAsync(path);
        Assert.Equal(PhotoVersionRecipeStatus.VersionMissing, family.Status);
        Assert.Equal(PhotoVersionIndexWriteStatus.Applied,
            await _index.ReconcileAsync(imageId, path, null, family));
        var indexed = (await _index.GetAsync(imageId))!;
        Assert.Equal(PhotoVersionRecipeStatus.VersionMissing, indexed.Status);
        Assert.Null(indexed.SourceId);
        Assert.Empty(indexed.Versions);
    }

    [Fact]
    public async Task FreshCacheCanRebuildFromRelocatedPortableFamily()
    {
        var (path, imageId, family) = await CreateFamilyAsync();
        Assert.Equal(PhotoVersionIndexWriteStatus.Applied,
            await _index.ReconcileAsync(imageId, path, null, family));
        var relocatedDirectory = Path.Combine(_directory, "relocated");
        Directory.CreateDirectory(relocatedDirectory);
        var relocated = Path.Combine(relocatedDirectory, "renamed.cr3");
        File.Copy(path, relocated);
        File.Copy(Path.ChangeExtension(path, ".xmp"), Path.ChangeExtension(relocated, ".xmp"));

        _freshDatabase = new CacheDatabase(Path.Combine(_directory, "fresh.db"));
        await _freshDatabase.InitializeAsync();
        var freshImages = new ImageRepository(_freshDatabase);
        var freshId = await freshImages.UpsertImageAsync(new ImageEntry
        {
            FilePath = relocated, FileName = Path.GetFileName(relocated), DateModified = DateTime.UtcNow
        });
        var freshIndex = new PhotoVersionIndexRepository(_freshDatabase);
        var relocatedFamily = await _store.ListAsync(relocated);
        Assert.Equal(PhotoVersionIndexWriteStatus.Applied,
            await freshIndex.ReconcileAsync(freshId, relocated, null, relocatedFamily));
        var indexed = (await freshIndex.GetAsync(freshId))!;
        Assert.Equal(family.Records![0].Target.SourceId, indexed.SourceId);
        Assert.Equal(family.Records.Select(record => record.Target.VariantId),
            indexed.Versions.Select(record => record.VariantId));
        Assert.Equal(PhotoVersionRecipeStatus.Success, indexed.Status);
    }

    [Fact]
    public async Task LibraryIndexerReconcilesFamilyAndRetainsVersionsWhenMetadataDisappears()
    {
        var (path, imageId, family) = await CreateFamilyAsync();
        using var scanner = new FolderScannerService();
        var indexer = new LibraryIndexingService(_database, _images, new TagRepository(_database),
            new FaceRepository(_database), scanner, new MetadataReaderService(), new FaceMetadataStore());

        await indexer.IndexFileAsync(path);
        var indexed = (await _index.GetAsync(imageId))!;
        Assert.Equal(PhotoVersionRecipeStatus.Success, indexed.Status);
        Assert.Equal(family.Records!.Select(record => record.Target.VariantId),
            indexed.Versions.Select(record => record.VariantId));
        Assert.False(await indexer.IndexFileAsync(path));
        Assert.Equal(indexed.Generation, (await _index.GetAsync(imageId))!.Generation);

        var sidecar = Path.ChangeExtension(path, ".xmp");
        var originalSidecar = await File.ReadAllTextAsync(sidecar);
        var xmp = XmpMetaFactory.ParseFromString(originalSidecar);
        xmp.SetProperty(MetadataWriterService.PhotoLibrarianNamespace, "plib:VirtualCopies", "{broken");
        await File.WriteAllTextAsync(sidecar, XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()));
        await indexer.IndexFileAsync(path);
        var invalid = (await _index.GetAsync(imageId))!;
        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata, invalid.Status);
        Assert.Equal(indexed.Versions, invalid.Versions);
        await File.WriteAllTextAsync(sidecar, originalSidecar);
        await indexer.IndexFileAsync(path);

        File.Delete(sidecar);
        await indexer.IndexFileAsync(path);
        var missing = (await _index.GetAsync(imageId))!;
        Assert.Equal(PhotoVersionRecipeStatus.VersionMissing, missing.Status);
        Assert.Equal(indexed.Versions, missing.Versions);
    }

    [Fact]
    public async Task LibraryIndexerDetectsRemovalOfQualifiedRawFamilySidecar()
    {
        var path = Path.Combine(_directory, "paired.cr3");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await WicTestImage.CreateAsync(Path.Combine(_directory, "paired.jpg"), 8, 8, jpeg: true);
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            (await _store.CreateFamilyAsync(path, "Original", "Copy")).Status);
        using var scanner = new FolderScannerService();
        var indexer = new LibraryIndexingService(_database, _images, new TagRepository(_database),
            new FaceRepository(_database), scanner, new MetadataReaderService(), new FaceMetadataStore());

        await indexer.IndexFileAsync(path, verifyContent: false);
        var image = (await _images.GetByPathAsync(path))!;
        var before = (await _index.GetAsync(image.Id))!;
        Assert.Equal(PhotoVersionRecipeStatus.Success, before.Status);
        File.Delete(path + ".xmp");

        await indexer.IndexFileAsync(path, verifyContent: false);
        var after = (await _index.GetAsync(image.Id))!;
        Assert.Equal(PhotoVersionRecipeStatus.VersionMissing, after.Status);
        Assert.Equal(before.Versions, after.Versions);
    }

    [Fact]
    public async Task ExplicitRefreshDetectsQualifiedSidecarChangeWithUnchangedStamp()
    {
        var path = Path.Combine(_directory, "paired.cr3");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await WicTestImage.CreateAsync(Path.Combine(_directory, "paired.jpg"), 8, 8, jpeg: true);
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            (await _store.CreateFamilyAsync(path, "Original", "Copy")).Status);
        using var scanner = new FolderScannerService();
        var indexer = new LibraryIndexingService(_database, _images, new TagRepository(_database),
            new FaceRepository(_database), scanner, new MetadataReaderService(), new FaceMetadataStore());

        await indexer.IndexFileAsync(path);
        var image = (await _images.GetByPathAsync(path))!;
        var before = (await _index.GetAsync(image.Id))!;
        var sidecar = path + ".xmp";
        var timestamp = File.GetLastWriteTimeUtc(sidecar);
        var content = await File.ReadAllTextAsync(sidecar);
        var updated = content.Replace("Copy", "Cupy", StringComparison.Ordinal);
        Assert.NotEqual(content, updated);
        Assert.Equal(content.Length, updated.Length);
        await File.WriteAllTextAsync(sidecar, updated);
        File.SetLastWriteTimeUtc(sidecar, timestamp);

        await indexer.IndexFileAsync(path, verifyContent: true);
        var after = (await _index.GetAsync(image.Id))!;
        Assert.Equal(PhotoVersionRecipeStatus.Success, after.Status);
        Assert.Equal(before.Versions[1].VariantId, after.Versions[1].VariantId);
        Assert.Equal("Cupy", after.Versions[1].Name);
    }

    [Fact]
    public async Task LibraryIndexerDetectsAmbiguousNewRawCompanion()
    {
        var (path, imageId, _) = await CreateFamilyAsync();
        using var scanner = new FolderScannerService();
        var indexer = new LibraryIndexingService(_database, _images, new TagRepository(_database),
            new FaceRepository(_database), scanner, new MetadataReaderService(), new FaceMetadataStore());

        await indexer.IndexFileAsync(path, verifyContent: false);
        var before = (await _index.GetAsync(imageId))!;
        Assert.Equal(PhotoVersionRecipeStatus.Success, before.Status);
        File.Copy(path, Path.ChangeExtension(path, ".nef"));

        await indexer.IndexFileAsync(path, verifyContent: false);
        var after = (await _index.GetAsync(imageId))!;
        Assert.Equal(PhotoVersionRecipeStatus.Conflict, after.Status);
        Assert.Equal(before.Versions, after.Versions);
    }

    [Fact]
    public async Task ExplicitRefreshDetectsChangedRawCompanionWithUnchangedStamp()
    {
        var (path, imageId, _) = await CreateFamilyAsync();
        var companion = Path.ChangeExtension(path, ".nef");
        var original = await File.ReadAllBytesAsync(path);
        var distinct = original.ToArray();
        distinct[0] ^= 0xff;
        await File.WriteAllBytesAsync(companion, distinct);
        using var scanner = new FolderScannerService();
        var indexer = new LibraryIndexingService(_database, _images, new TagRepository(_database),
            new FaceRepository(_database), scanner, new MetadataReaderService(), new FaceMetadataStore());

        await indexer.IndexFileAsync(path);
        var before = (await _index.GetAsync(imageId))!;
        Assert.Equal(PhotoVersionRecipeStatus.Success, before.Status);
        var modified = File.GetLastWriteTimeUtc(companion);
        await File.WriteAllBytesAsync(companion, original);
        File.SetLastWriteTimeUtc(companion, modified);

        await indexer.IndexFileAsync(path, verifyContent: true);
        var after = (await _index.GetAsync(imageId))!;
        Assert.Equal(PhotoVersionRecipeStatus.Conflict, after.Status);
        Assert.Equal(before.Versions, after.Versions);
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".tif")]
    [InlineData(".bmp")]
    public async Task RasterFamilyIsIndexedAsUnavailableUntilIdentityIsSupported(string extension)
    {
        var path = Path.Combine(_directory, "source" + extension);
        await WicTestImage.CreateAsync(path, 16, 16, jpeg: extension == ".jpg");
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            Guid.NewGuid(), "pixels-v1:fixture", Guid.NewGuid(), "Original", "<main/>",
            Guid.NewGuid(), "Copy", out var document));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, document!, null));
        using var scanner = new FolderScannerService();
        var indexer = new LibraryIndexingService(_database, _images, new TagRepository(_database),
            new FaceRepository(_database), scanner, new MetadataReaderService(), new FaceMetadataStore());

        await indexer.IndexFileAsync(path);
        var image = (await _images.GetByPathAsync(path))!;
        var indexed = (await _index.GetAsync(image.Id))!;
        Assert.Equal(PhotoVersionRecipeStatus.Unsupported, indexed.Status);
        Assert.Empty(indexed.Versions);
        Assert.Equal(document, MetadataWriterService.ReadVersionFamily(path).Value);
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".tif")]
    [InlineData(".bmp")]
    public async Task OrdinaryRasterIsNotMarkedAsAnUnsupportedFamily(string extension)
    {
        var path = Path.Combine(_directory, "ordinary" + extension);
        await WicTestImage.CreateAsync(path, 16, 16, jpeg: extension == ".jpg");
        using var scanner = new FolderScannerService();
        var indexer = new LibraryIndexingService(_database, _images, new TagRepository(_database),
            new FaceRepository(_database), scanner, new MetadataReaderService(), new FaceMetadataStore());

        await indexer.IndexFileAsync(path);
        var image = (await _images.GetByPathAsync(path))!;
        var indexed = (await _index.GetAsync(image.Id))!;
        Assert.Equal(PhotoVersionRecipeStatus.VersionMissing, indexed.Status);
        Assert.Empty(indexed.Versions);
        Assert.NotNull(indexed.MetadataHash);
    }

    [Fact]
    public async Task LibraryIndexerRebuildsRelocatedFamilyInFreshCache()
    {
        var (path, _, family) = await CreateFamilyAsync();
        var destination = Path.Combine(_directory, "relocated");
        Directory.CreateDirectory(destination);
        var relocated = Path.Combine(destination, "renamed.cr3");
        File.Copy(path, relocated);
        File.Copy(Path.ChangeExtension(path, ".xmp"), Path.ChangeExtension(relocated, ".xmp"));
        _freshDatabase = new CacheDatabase(Path.Combine(_directory, "fresh.db"));
        await _freshDatabase.InitializeAsync();
        var images = new ImageRepository(_freshDatabase);
        using var scanner = new FolderScannerService();
        var indexer = new LibraryIndexingService(_freshDatabase, images, new TagRepository(_freshDatabase),
            new FaceRepository(_freshDatabase), scanner, new MetadataReaderService(), new FaceMetadataStore());

        await indexer.IndexFileAsync(relocated);
        var image = (await images.GetByPathAsync(relocated))!;
        var indexed = (await new PhotoVersionIndexRepository(_freshDatabase).GetAsync(image.Id))!;
        Assert.Equal(PhotoVersionRecipeStatus.Success, indexed.Status);
        Assert.Equal(family.Records![0].Target.SourceId, indexed.SourceId);
        Assert.Equal(family.Records.Select(record => record.Target.VariantId),
            indexed.Versions.Select(record => record.VariantId));
    }

    [Fact]
    public async Task UnchangedPreviouslyIndexedPhotoGetsVersionProjection()
    {
        var (path, imageId, family) = await CreateFamilyAsync();
        using var scanner = new FolderScannerService();
        var indexer = new LibraryIndexingService(_database, _images, new TagRepository(_database),
            new FaceRepository(_database), scanner, new MetadataReaderService(), new FaceMetadataStore());
        await indexer.IndexFileAsync(path);
        using (var connection = _database.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM photo_version_family_index WHERE image_id = $id";
            command.Parameters.AddWithValue("$id", imageId);
            await command.ExecuteNonQueryAsync();
        }

        Assert.False(await indexer.IndexFileAsync(path, verifyContent: false));
        var rebuilt = (await _index.GetAsync(imageId))!;
        Assert.Equal(PhotoVersionRecipeStatus.Success, rebuilt.Status);
        Assert.Equal(family.Records!.Select(record => record.Target.VariantId),
            rebuilt.Versions.Select(record => record.VariantId));
    }

    private async Task<(string Path, long ImageId, PhotoVersionFamilyResult Family)> CreateFamilyAsync()
    {
        var path = Path.Combine(_directory, "source.cr3");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var imageId = await _images.UpsertImageAsync(new ImageEntry
        {
            FilePath = path, FileName = Path.GetFileName(path), DateModified = DateTime.UtcNow
        });
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            (await _store.CreateFamilyAsync(path, "Original", "Copy")).Status);
        var family = await _store.ListAsync(path);
        Assert.Equal(PhotoVersionRecipeStatus.Success, family.Status);
        return (path, imageId, family);
    }
}
