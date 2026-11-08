// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class LibraryIndexingServiceTests
{
    [Fact]
    public async Task IndexFileAsync_ReimportsChangedPortableTagSidecarForCachedImage()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-tag-sidecar-refresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "photo.raw");

        try
        {
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
            using var database = new CacheDatabase(
                Path.Combine(directory, "cache.db"));
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var tags = new TagRepository(database);
            using var scanner = new FolderScannerService();
            var indexer = new LibraryIndexingService(
                database,
                images,
                tags,
                new FaceRepository(database),
                scanner,
                new MetadataReaderService(),
                new FaceMetadataStore());
            var originalImageWriteTime = File.GetLastWriteTimeUtc(imagePath);

            await TagWriterService.WriteTagsToSidecarAsync(
                imagePath,
                ["Auto/dog", "family"],
                [
                    new ImageTag
                    {
                        Tag = "Auto",
                        Source = TagSource.AutoML,
                        Confidence = 0.55f
                    },
                    new ImageTag
                    {
                        Tag = "Auto/dog",
                        Source = TagSource.AutoML,
                        Confidence = 0.55f
                    },
                    new ImageTag
                    {
                        Tag = "family",
                        Source = TagSource.Manual,
                        Confidence = 1.0f
                    }
                ],
                "model-pipeline-v1");
            Assert.True(await indexer.IndexFileAsync(imagePath));
            Assert.False(await indexer.IndexFileAsync(imagePath));

            await TagWriterService.WriteTagsToSidecarAsync(
                imagePath,
                ["Auto/dog", "family"],
                [
                    new ImageTag
                    {
                        Tag = "Auto",
                        Source = TagSource.AutoML,
                        Confidence = 0.91f
                    },
                    new ImageTag
                    {
                        Tag = "Auto/dog",
                        Source = TagSource.AutoML,
                        Confidence = 0.91f
                    },
                    new ImageTag
                    {
                        Tag = "family",
                        Source = TagSource.Manual,
                        Confidence = 1.0f
                    }
                ],
                "model-pipeline-v2");

            Assert.Equal(originalImageWriteTime, File.GetLastWriteTimeUtc(imagePath));
            Assert.True(await indexer.IndexFileAsync(imagePath));

            var indexedImage = (await images.GetByPathAsync(imagePath))!;
            var restored = await tags.GetTagsAsync(indexedImage.Id);
            Assert.Equal(
                0.91f,
                Assert.Single(restored, tag => tag.Tag == "Auto/dog").Confidence);
            Assert.Equal(
                TagSource.Manual,
                Assert.Single(restored, tag => tag.Tag == "family").Source);
            Assert.Equal("model-pipeline-v2", indexedImage.AutoTagScanVersion);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".raw")]
    public async Task IndexFileAsync_RestoresPortableTagSourcesConfidenceAndScanVersion(
        string extension)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-tag-roundtrip-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(directory, "source");
        var movedDirectory = Path.Combine(directory, "moved");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(movedDirectory);
        var sourcePath = Path.Combine(sourceDirectory, $"tagged{extension}");
        var imagePath = Path.Combine(movedDirectory, $"tagged{extension}");

        try
        {
            if (extension == ".png")
                await WicTestImage.CreateAsync(sourcePath, 16, 12);
            else
                await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
            var portableTags = new[]
            {
                new ImageTag
                {
                    Tag = "Auto",
                    Source = TagSource.AutoML,
                    Confidence = 0.91f
                },
                new ImageTag
                {
                    Tag = "Auto/animal",
                    Source = TagSource.AutoML,
                    Confidence = 0.91f
                },
                new ImageTag
                {
                    Tag = "Auto/animal/bird",
                    Source = TagSource.AutoML,
                    Confidence = 0.91f
                },
                new ImageTag
                {
                    Tag = "Auto/animal/bird/duck",
                    Source = TagSource.AutoML,
                    Confidence = 0.82f
                },
                new ImageTag
                {
                    Tag = "family",
                    Source = TagSource.Manual,
                    Confidence = 1.0f
                }
            };
            const string scanVersion = "profile:approved-pipeline-v7";
            await TagWriterService.WriteTagsToSidecarAsync(
                sourcePath,
                portableTags.Select(tag => tag.Tag),
                portableTags,
                scanVersion);

            File.Move(sourcePath, imagePath);
            var sourceSidecar = Path.ChangeExtension(sourcePath, ".xmp");
            if (File.Exists(sourceSidecar))
            {
                File.Move(
                    sourceSidecar,
                    Path.ChangeExtension(imagePath, ".xmp"));
            }

            using var database = new CacheDatabase(
                Path.Combine(directory, "fresh-cache.db"));
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var tags = new TagRepository(database);
            using var scanner = new FolderScannerService();
            var indexer = new LibraryIndexingService(
                database,
                images,
                tags,
                new FaceRepository(database),
                scanner,
                new MetadataReaderService(),
                new FaceMetadataStore());

            Assert.True(await indexer.IndexFileAsync(imagePath));

            var restored = await tags.GetTagsAsync(
                (await images.GetByPathAsync(imagePath))!.Id);
            Assert.Equal(
                TagSource.AutoML,
                Assert.Single(restored, tag => tag.Tag == "Auto/animal/bird").Source);
            Assert.Equal(
                0.82f,
                Assert.Single(restored, tag => tag.Tag == "Auto/animal/bird/duck").Confidence);
            Assert.Equal(
                TagSource.Manual,
                Assert.Single(restored, tag => tag.Tag == "family").Source);
            Assert.Equal(
                scanVersion,
                (await images.GetByPathAsync(imagePath))!.AutoTagScanVersion);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WatchedFolderChanges_PreservesIndexDataWhenImageIsDeleted()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "copied.png");
        var databasePath = Path.Combine(directory, "cache.db");

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            using var scanner = new FolderScannerService();
            var indexer = new LibraryIndexingService(
                database, images, new TagRepository(database), new FaceRepository(database),
                scanner, new MetadataReaderService(), new FaceMetadataStore());
            using var changes = new WatchedFolderChangeService(
                scanner, indexer, images, TimeSpan.FromMilliseconds(200));
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            changes.LibraryChanged += (_, _) => changed.TrySetResult();
            scanner.SyncWatchedFolders([(directory, false)]);

            await WicTestImage.CreateAsync(imagePath, 16, 12);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var entry = await images.GetByPathAsync(imagePath);
            Assert.NotNull(entry);
            Assert.Equal(new FileInfo(imagePath).Length, entry.FileSize);

            changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            File.Delete(imagePath);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(await images.GetByPathAsync(imagePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task WatchedFolderChanges_CoalescesRenamesAndMultipleCopies()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var oldPath = Path.Combine(directory, "one.jpg");
        var renamedPath = Path.Combine(directory, "renamed.jpg");
        var secondPath = Path.Combine(directory, "two.jpg");

        try
        {
            using var database = new CacheDatabase(Path.Combine(directory, "cache.db"));
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            using var scanner = new FolderScannerService();
            var indexer = new LibraryIndexingService(
                database, images, new TagRepository(database), new FaceRepository(database),
                scanner, new MetadataReaderService(), new FaceMetadataStore());
            using var changes = new WatchedFolderChangeService(
                scanner, indexer, images, TimeSpan.FromMilliseconds(250));
            var notifications = 0;
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            changes.LibraryChanged += (_, _) =>
            {
                Interlocked.Increment(ref notifications);
                changed.TrySetResult();
            };
            scanner.SyncWatchedFolders([(directory, false)]);

            await File.WriteAllBytesAsync(oldPath, [1, 2]);
            await File.WriteAllBytesAsync(secondPath, [3, 4]);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(await images.GetByPathAsync(oldPath));
            Assert.NotNull(await images.GetByPathAsync(secondPath));
            Assert.Equal(1, Volatile.Read(ref notifications));

            changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            File.Move(oldPath, renamedPath);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(await images.GetByPathAsync(oldPath));
            Assert.NotNull(await images.GetByPathAsync(renamedPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task WatchedFolderChanges_PreservesRecordsWhenDirectoryMovesOut()
    {
        var root = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-{Guid.NewGuid():N}");
        var watched = Path.Combine(root, "watched");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(watched);
        Directory.CreateDirectory(outside);
        var imagePath = Path.Combine(outside, "photo.jpg");

        try
        {
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
            using var database = new CacheDatabase(Path.Combine(root, "cache.db"));
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            using var scanner = new FolderScannerService();
            var indexer = new LibraryIndexingService(
                database, images, new TagRepository(database), new FaceRepository(database),
                scanner, new MetadataReaderService(), new FaceMetadataStore());
            using var changes = new WatchedFolderChangeService(
                scanner, indexer, images, TimeSpan.FromMilliseconds(250));
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            changes.LibraryChanged += (_, _) => changed.TrySetResult();
            scanner.SyncWatchedFolders([(watched, true)]);

            var imported = Path.Combine(watched, "imported");
            Directory.Move(outside, imported);
            imagePath = Path.Combine(imported, "photo.jpg");
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(await images.GetByPathAsync(imagePath));

            changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Directory.Move(imported, outside);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(await images.GetByPathAsync(imagePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task WatchedFolderChanges_ReindexesRawImageWhenSidecarAppears()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "portrait.cr3");

        try
        {
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
            using var database = new CacheDatabase(Path.Combine(directory, "cache.db"));
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            using var scanner = new FolderScannerService();
            var indexer = new LibraryIndexingService(
                database, images, new TagRepository(database), new FaceRepository(database),
                scanner, new MetadataReaderService(), new FaceMetadataStore());
            await indexer.IndexFileAsync(imagePath);
            using var changes = new WatchedFolderChangeService(
                scanner, indexer, images, TimeSpan.FromMilliseconds(250));
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            WatchedFolderChangeService.WatchedLibraryChangedEventArgs? changeArgs = null;
            changes.LibraryChanged += (_, args) =>
            {
                changeArgs = args;
                changed.TrySetResult();
            };
            scanner.SyncWatchedFolders([(directory, false)]);

            await new FaceMetadataStore().WriteAsync(
                imagePath,
                new PhotoFaceMetadata(16, 12, []));

            await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(changeArgs);
            Assert.False(changeArgs.RequiresGridReload);
            Assert.Contains(imagePath, changeArgs.ChangedImagePaths);
            Assert.Equal(Path.ChangeExtension(imagePath, ".xmp"),
                (await images.GetByPathAsync(imagePath))!.FaceSidecarPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FailedFaceMetadataImport_IsRetriedForUnchangedImage()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "cache.db");
        var imagePath = Path.Combine(directory, "portrait.png");
        var sidecarPath = $"{imagePath}.xmp";

        try
        {
            await WicTestImage.CreateAsync(imagePath, 16, 12);
            await File.WriteAllTextAsync(
                sidecarPath,
                "not valid XMP",
                TestContext.Current.CancellationToken);

            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var service = new LibraryIndexingService(
                database,
                images,
                new TagRepository(database),
                faces,
                new FolderScannerService(),
                new MetadataReaderService(),
                new FaceMetadataStore());

            await service.IndexFolderAsync(
                directory,
                false,
                TestContext.Current.CancellationToken);
            Assert.False(
                (await images.GetByPathAsync(imagePath))!.FaceMetadataImported);

            await File.WriteAllTextAsync(
                sidecarPath,
                XmpMetaFactory.SerializeToString(
                    XmpMetaFactory.Create(),
                    new XmpCore.Options.SerializeOptions()),
                TestContext.Current.CancellationToken);
            await service.IndexFolderAsync(
                directory,
                false,
                TestContext.Current.CancellationToken);

            Assert.True(
                (await images.GetByPathAsync(imagePath))!.FaceMetadataImported);

            var seedPath = Path.Combine(directory, "seed.cr3");
            await new FaceMetadataStore().WriteAsync(
                seedPath,
                new PhotoFaceMetadata(
                    16,
                    12,
                    [
                        new PortableFaceMetadata(
                            1,
                            0.1,
                            0.2,
                            0.3,
                            0.4,
                            "Alex",
                            false,
                            false,
                            [])
                    ]),
                TestContext.Current.CancellationToken);
            File.Move(
                Path.ChangeExtension(seedPath, ".xmp"),
                sidecarPath,
                true);
            await service.IndexFolderAsync(
                directory,
                false,
                TestContext.Current.CancellationToken);
            Assert.Contains(
                await faces.GetAllPersonsAsync(),
                person => person.Name == "Alex");

            File.Delete(sidecarPath);
            await service.IndexFolderAsync(
                directory,
                false,
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain(
                await faces.GetAllPersonsAsync(),
                person => person.Name == "Alex");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
