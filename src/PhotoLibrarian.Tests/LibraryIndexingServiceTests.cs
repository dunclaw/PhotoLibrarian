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
    public async Task WatchedFolderChanges_IndexExplorerCopyAndRemoveDeletedImage()
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
            Assert.Null(await images.GetByPathAsync(imagePath));
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
            Assert.Null(await images.GetByPathAsync(oldPath));
            Assert.NotNull(await images.GetByPathAsync(renamedPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task WatchedFolderChanges_IndexesMovedInDirectoryAndRemovesMovedOutDirectory()
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
            Assert.Null(await images.GetByPathAsync(imagePath));
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
            changes.LibraryChanged += (_, _) => changed.TrySetResult();
            scanner.SyncWatchedFolders([(directory, false)]);

            await new FaceMetadataStore().WriteAsync(
                imagePath,
                new PhotoFaceMetadata(16, 12, []));

            await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
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
