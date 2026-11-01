// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Security.Cryptography;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class LibraryIndexMaintenanceServiceTests
{
    [Theory]
    [InlineData("renamed.png")]
    [InlineData("moved\\photo.png")]
    public async Task Reconcile_RepairsUniqueHashMatchedMoveAndPreservesDatabaseState(
        string destinationRelativePath)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var library = await TestLibrary.CreateAsync(root);
            await library.AddWatchedFolderAsync(root);
            var oldPath = Path.Combine(root, "photo.png");
            await WicTestImage.CreateAsync(oldPath, 16, 12);
            await library.IndexAsync(oldPath);
            var original = Assert.IsType<ImageEntry>(
                await library.Images.GetByPathAsync(oldPath));
            await SeedDatabaseOnlyStateAsync(library, original.Id);
            await SeedExactHashAsync(library, original, oldPath);

            var destinationPath = Path.Combine(
                root,
                destinationRelativePath.Replace(
                    '\\',
                    Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Move(oldPath, destinationPath);

            var result = await library.Maintenance.ReconcileAsync();

            Assert.True(result.Succeeded);
            Assert.Equal(1, result.RepairedMoves);
            Assert.Equal(1, result.ConfirmedMissingFiles);
            Assert.Null(await library.Images.GetByPathAsync(oldPath));
            var relocated = Assert.IsType<ImageEntry>(
                await library.Images.GetByPathAsync(destinationPath));
            Assert.Equal(original.Id, relocated.Id);
            Assert.True(relocated.IsFlagged);
            Assert.Equal(3, await CountDatabaseOnlyStateAsync(
                library.Database,
                original.Id));

            var hashes = await library.Images.GetDuplicateHashesAsync(
                TestContext.Current.CancellationToken);
            Assert.Contains(destinationPath, hashes.Keys);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_ReportsConfirmedDeletionButRetainsItsRecord()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var library = await TestLibrary.CreateAsync(root);
            await library.AddWatchedFolderAsync(root);
            var path = Path.Combine(root, "deleted.png");
            await WicTestImage.CreateAsync(path, 16, 12);
            await library.IndexAsync(path);
            var indexed = Assert.IsType<ImageEntry>(
                await library.Images.GetByPathAsync(path));
            File.Delete(path);

            var result = await library.Maintenance.ReconcileAsync();

            Assert.True(result.Succeeded);
            Assert.Equal(1, result.ConfirmedMissingFiles);
            Assert.Equal(indexed.Id, (await library.Images.GetByPathAsync(path))!.Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_DoesNotRepairAmbiguousExactCopies()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var library = await TestLibrary.CreateAsync(root);
            await library.AddWatchedFolderAsync(root);
            var oldPath = Path.Combine(root, "photo.png");
            await WicTestImage.CreateAsync(oldPath, 16, 12);
            await library.IndexAsync(oldPath);
            var original = Assert.IsType<ImageEntry>(
                await library.Images.GetByPathAsync(oldPath));
            await SeedExactHashAsync(library, original, oldPath);
            var firstCopy = Path.Combine(root, "first.png");
            var secondCopy = Path.Combine(root, "second.png");
            File.Move(oldPath, firstCopy);
            File.Copy(firstCopy, secondCopy);

            var result = await library.Maintenance.ReconcileAsync();

            Assert.True(result.Succeeded);
            Assert.Equal(0, result.RepairedMoves);
            Assert.Equal(original.Id, (await library.Images.GetByPathAsync(oldPath))!.Id);
            Assert.NotNull(await library.Images.GetByPathAsync(firstCopy));
            Assert.NotNull(await library.Images.GetByPathAsync(secondCopy));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_PreservesEntriesWhenWatchedLocationIsUnavailable()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var library = await TestLibrary.CreateAsync(root);
            var watchedFolder = Path.Combine(root, "removable-drive");
            Directory.CreateDirectory(watchedFolder);
            await library.AddWatchedFolderAsync(watchedFolder);
            var path = Path.Combine(watchedFolder, "photo.png");
            await WicTestImage.CreateAsync(path, 16, 12);
            await library.IndexAsync(path);
            var indexed = Assert.IsType<ImageEntry>(
                await library.Images.GetByPathAsync(path));
            Directory.Delete(watchedFolder, recursive: true);

            var result = await library.Maintenance.ReconcileAsync();

            Assert.False(result.Succeeded);
            Assert.Equal(0, result.ConfirmedMissingFiles);
            Assert.Contains(
                result.Issues,
                issue => issue.Kind ==
                    LibraryIndexMaintenanceIssueKind.FolderUnavailable);
            Assert.Equal(indexed.Id, (await library.Images.GetByPathAsync(path))!.Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_ReportsAccessDeniedAndPreservesEntries()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var watchedFolder = Path.Combine(root, "watched");
            Directory.CreateDirectory(watchedFolder);
            using var library = await TestLibrary.CreateAsync(
                root,
                new DenyingFileSystem(watchedFolder));
            await library.AddWatchedFolderAsync(watchedFolder);
            var path = Path.Combine(watchedFolder, "photo.png");
            await WicTestImage.CreateAsync(path, 16, 12);
            await library.IndexAsync(path);
            var indexed = Assert.IsType<ImageEntry>(
                await library.Images.GetByPathAsync(path));

            var result = await library.Maintenance.ReconcileAsync();

            Assert.False(result.Succeeded);
            Assert.Equal(0, result.ConfirmedMissingFiles);
            Assert.Contains(
                result.Issues,
                issue => issue.Kind ==
                    LibraryIndexMaintenanceIssueKind.AccessDenied);
            Assert.Equal(indexed.Id, (await library.Images.GetByPathAsync(path))!.Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_ReportsAndPreservesReparsePointFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var watchedFolder = Path.Combine(root, "watched");
            Directory.CreateDirectory(watchedFolder);
            var path = Path.Combine(watchedFolder, "photo.png");
            using var library = await TestLibrary.CreateAsync(
                root,
                new ReparsePointFileSystem(path));
            await library.AddWatchedFolderAsync(watchedFolder);
            await WicTestImage.CreateAsync(path, 16, 12);
            await library.IndexAsync(path);
            var indexed = Assert.IsType<ImageEntry>(
                await library.Images.GetByPathAsync(path));

            var result = await library.Maintenance.ReconcileAsync();

            Assert.False(result.Succeeded);
            Assert.Contains(
                result.Issues,
                issue => issue.Kind ==
                    LibraryIndexMaintenanceIssueKind.ReparsePointSkipped);
            Assert.Equal(indexed.Id, (await library.Images.GetByPathAsync(path))!.Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_CancellationRetainsCompletedWorkAndCanBeRetried()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var library = await TestLibrary.CreateAsync(root);
            await library.AddWatchedFolderAsync(root);
            var firstPath = Path.Combine(root, "one.png");
            var secondPath = Path.Combine(root, "two.png");
            await WicTestImage.CreateAsync(firstPath, 16, 12);
            await WicTestImage.CreateAsync(secondPath, 18, 12);
            using var cancellation = new CancellationTokenSource();
            var cancelAfterFirstIndex = new InlineProgress<LibraryIndexMaintenanceProgress>(
                update =>
                {
                    if (update.Stage ==
                            LibraryIndexMaintenanceStage.IndexingFiles &&
                        update.Current == 1)
                    {
                        cancellation.Cancel();
                    }
                });

            var interrupted = await library.Maintenance.ReconcileAsync(
                cancelAfterFirstIndex,
                cancellation.Token);

            Assert.True(interrupted.Cancelled);
            Assert.Equal(1, interrupted.UpdatedFiles);
            Assert.Equal(1, await library.Images.GetCountAsync());

            var resumed = await library.Maintenance.ReconcileAsync();

            Assert.True(resumed.Succeeded);
            Assert.Equal(1, resumed.UpdatedFiles);
            Assert.Equal(2, await library.Images.GetCountAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reconcile_ReportsFailedFileAndRetryCompletesWithoutLosingExistingEntries()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fakePath = Path.Combine(root, "not-yet-readable.png");
            using var library = await TestLibrary.CreateAsync(
                root,
                new PhantomFileSystem(root, fakePath));
            await library.AddWatchedFolderAsync(root);

            var failed = await library.Maintenance.ReconcileAsync();

            Assert.False(failed.Succeeded);
            Assert.Contains(
                failed.Issues,
                issue => issue.Kind ==
                    LibraryIndexMaintenanceIssueKind.FileIndexFailed &&
                    issue.Path == fakePath);
            Assert.Equal(0, await library.Images.GetCountAsync());

            await WicTestImage.CreateAsync(fakePath, 16, 12);
            var recovered = await library.Maintenance.ReconcileAsync();

            Assert.True(recovered.Succeeded);
            Assert.Equal(1, recovered.UpdatedFiles);
            Assert.NotNull(await library.Images.GetByPathAsync(fakePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SeedDatabaseOnlyStateAsync(
        TestLibrary library,
        long imageId)
    {
        using var connection = library.Database.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE images SET is_flagged = 1 WHERE id = $imageId;
            INSERT INTO tags (image_id, tag, source, confidence)
            VALUES ($imageId, 'manual-only', 1, 1.0);
            INSERT INTO persons (name, face_count) VALUES ('Local person', 1);
            INSERT INTO face_regions
                (image_id, x, y, width, height, person_id, confidence)
            VALUES (
                $imageId,
                0.1,
                0.2,
                0.3,
                0.4,
                last_insert_rowid(),
                1.0);
            """;
        command.Parameters.AddWithValue("$imageId", imageId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountDatabaseOnlyStateAsync(
        CacheDatabase database,
        long imageId)
    {
        using var connection = database.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM tags WHERE image_id = $imageId) +
                (SELECT COUNT(*) FROM face_regions WHERE image_id = $imageId) +
                (SELECT COUNT(*) FROM persons WHERE name = 'Local person')
            """;
        command.Parameters.AddWithValue("$imageId", imageId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task SeedExactHashAsync(
        TestLibrary library,
        ImageEntry entry,
        string path)
    {
        await using var stream = File.OpenRead(path);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        var info = new FileInfo(path);
        await library.Images.SaveDuplicateHashesAsync(
            [
                new DuplicateFileSnapshot(
                    entry,
                    info.Length,
                    info.LastWriteTimeUtc,
                    sha256,
                    null)
            ],
            TestContext.Current.CancellationToken);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestLibrary : IDisposable
    {
        private TestLibrary(
            CacheDatabase database,
            ImageRepository images,
            FolderScannerService scanner,
            LibraryIndexingService indexer,
            LibraryIndexMaintenanceService maintenance)
        {
            Database = database;
            Images = images;
            Scanner = scanner;
            Indexer = indexer;
            Maintenance = maintenance;
        }

        public CacheDatabase Database { get; }
        public ImageRepository Images { get; }
        public FolderScannerService Scanner { get; }
        public LibraryIndexingService Indexer { get; }
        public LibraryIndexMaintenanceService Maintenance { get; }

        public static async Task<TestLibrary> CreateAsync(
            string root,
            ILibraryMaintenanceFileSystem? fileSystem = null)
        {
            var database = new CacheDatabase(Path.Combine(root, "cache.db"));
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var tags = new TagRepository(database);
            var faces = new FaceRepository(database);
            var scanner = new FolderScannerService();
            var indexer = new LibraryIndexingService(
                database,
                images,
                tags,
                faces,
                scanner,
                new MetadataReaderService(),
                new FaceMetadataStore());
            return new TestLibrary(
                database,
                images,
                scanner,
                indexer,
                new LibraryIndexMaintenanceService(
                    database,
                    images,
                    indexer,
                    fileSystem));
        }

        public async Task AddWatchedFolderAsync(string path)
        {
            using var connection = Database.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO watched_folders (path, include_sub) VALUES ($path, 1)";
            command.Parameters.AddWithValue("$path", path);
            await command.ExecuteNonQueryAsync();
        }

        public async Task IndexAsync(string path)
        {
            await Indexer.IndexFileAsync(path);
        }

        public void Dispose()
        {
            Scanner.Dispose();
            Database.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    private sealed class DenyingFileSystem(string deniedDirectory)
        : ILibraryMaintenanceFileSystem
    {
        public IEnumerable<string> EnumerateFileSystemEntries(
            string directoryPath)
        {
            if (string.Equals(
                    directoryPath,
                    deniedDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Synthetic access denial.");
            }
            return Directory.EnumerateFileSystemEntries(directoryPath);
        }

        public FileAttributes GetAttributes(string path) =>
            File.GetAttributes(path);
    }

    private sealed class ReparsePointFileSystem(string reparsePath)
        : ILibraryMaintenanceFileSystem
    {
        public IEnumerable<string> EnumerateFileSystemEntries(
            string directoryPath) =>
            Directory.EnumerateFileSystemEntries(directoryPath);

        public FileAttributes GetAttributes(string path)
        {
            var attributes = File.GetAttributes(path);
            return string.Equals(
                    path,
                    reparsePath,
                    StringComparison.OrdinalIgnoreCase)
                ? attributes | FileAttributes.ReparsePoint
                : attributes;
        }
    }

    private sealed class PhantomFileSystem(
        string root,
        string phantomPath) : ILibraryMaintenanceFileSystem
    {
        public IEnumerable<string> EnumerateFileSystemEntries(
            string directoryPath) =>
            string.Equals(directoryPath, root, StringComparison.OrdinalIgnoreCase)
                ? [phantomPath]
                : [];

        public FileAttributes GetAttributes(string path) =>
            FileAttributes.Normal;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
