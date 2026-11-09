// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Services;
using XmpCore;
using XmpCore.Options;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PhotoImportServiceTests
{
    [Fact]
    public async Task ImportAsync_CopiesAndIndexesVerifiedFilesWithoutOverwriting()
    {
        var directory = CreateTemporaryDirectory();
        var sourceRoot = Path.Combine(directory, "camera");
        var dcim = Path.Combine(sourceRoot, "DCIM", "100MEDIA");
        var destinationRoot = Path.Combine(directory, "library");
        Directory.CreateDirectory(dcim);
        Directory.CreateDirectory(destinationRoot);
        var sourcePath = Path.Combine(dcim, "capture.cr2");
        var sourceSidecarPath = Path.ChangeExtension(sourcePath, ".xmp");
        var sourceBytes = Enumerable.Range(0, 256 * 1024)
            .Select(index => (byte)(index % 251))
            .ToArray();

        try
        {
            await File.WriteAllBytesAsync(
                sourcePath, sourceBytes, TestContext.Current.CancellationToken);
            await TagWriterService.WriteTagsToSidecarAsync(sourcePath, ["camera-tag"]);
            var sourceModified = DateTime.UtcNow.AddDays(-40);
            File.SetLastWriteTimeUtc(sourcePath, sourceModified);
            sourceModified = File.GetLastWriteTimeUtc(sourcePath);
            var originalSidecar = await File.ReadAllBytesAsync(
                sourceSidecarPath, TestContext.Current.CancellationToken);

            using var fixture = await CreateFixtureAsync(directory);
            var scan = await fixture.Service.ScanSourceAsync(
                sourceRoot, TestContext.Current.CancellationToken);
            var candidate = Assert.Single(scan.Candidates);
            var destinationDateDirectory = Path.Combine(
                destinationRoot,
                PhotoImportService.GetDateFolder(candidate));
            Directory.CreateDirectory(destinationDateDirectory);
            var existingPath = Path.Combine(destinationDateDirectory, "capture.cr2");
            await File.WriteAllTextAsync(
                existingPath, "keep existing file", TestContext.Current.CancellationToken);

            var result = await fixture.Service.ImportAsync(
                scan.Candidates,
                destinationRoot,
                "{name}",
                ["import-tag"],
                cancellationToken: TestContext.Current.CancellationToken);

            var imported = Assert.Single(result.Items);
            Assert.True(imported.Succeeded, imported.Error);
            Assert.EndsWith("capture_2.cr2", imported.DestinationPath, StringComparison.Ordinal);
            Assert.Equal(
                sourceBytes,
                await File.ReadAllBytesAsync(
                    imported.DestinationPath!, TestContext.Current.CancellationToken));
            Assert.Equal(
                "keep existing file",
                await File.ReadAllTextAsync(
                    existingPath, TestContext.Current.CancellationToken));
            Assert.Equal(sourceModified, File.GetLastWriteTimeUtc(imported.DestinationPath!));
            Assert.Equal(
                sourceBytes,
                await File.ReadAllBytesAsync(sourcePath, TestContext.Current.CancellationToken));
            Assert.Equal(
                originalSidecar,
                await File.ReadAllBytesAsync(
                    sourceSidecarPath, TestContext.Current.CancellationToken));

            var destinationSidecarPath = Path.ChangeExtension(imported.DestinationPath, ".xmp");
            var destinationTags = fixture.MetadataReader.ReadTags(imported.DestinationPath!);
            Assert.Contains("camera-tag", destinationTags);
            Assert.Contains("import-tag", destinationTags);
            Assert.True(File.Exists(destinationSidecarPath));
            Assert.NotNull(await fixture.Images.GetByPathAsync(imported.DestinationPath!));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_PreservesBothTagAndFaceSidecarsForJpeg()
    {
        var directory = CreateTemporaryDirectory();
        var dcim = Path.Combine(directory, "camera", "DCIM");
        var destinationRoot = Path.Combine(directory, "library");
        Directory.CreateDirectory(dcim);
        Directory.CreateDirectory(destinationRoot);
        var sourcePath = Path.Combine(dcim, "portrait.jpg");
        var sourceFaceSidecar = $"{sourcePath}.xmp";

        try
        {
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);
            await TagWriterService.WriteTagsToSidecarAsync(sourcePath, ["camera-tag"]);
            // Seed the legacy layout that older builds produced: tags in portrait.xmp and
            // face data in portrait.jpg.xmp.
            File.Move(sourceFaceSidecar, Path.ChangeExtension(sourcePath, ".xmp"));
            var faceMetadata = XmpMetaFactory.Create();
            faceMetadata.SetProperty(
                "http://purl.org/dc/elements/1.1/",
                "description",
                "paired face metadata");
            var originalFaceSidecar = XmpMetaFactory.SerializeToString(
                faceMetadata,
                new SerializeOptions());
            await File.WriteAllTextAsync(
                sourceFaceSidecar,
                originalFaceSidecar,
                TestContext.Current.CancellationToken);
            var originalFaceSidecarBytes = await File.ReadAllBytesAsync(
                sourceFaceSidecar,
                TestContext.Current.CancellationToken);

            using var fixture = await CreateFixtureAsync(directory);
            var scan = await fixture.Service.ScanSourceAsync(
                Path.GetDirectoryName(dcim)!, TestContext.Current.CancellationToken);
            var candidate = Assert.Single(scan.Candidates);
            Assert.Equal(2, candidate.SourceSidecars.Count);

            var result = await fixture.Service.ImportAsync(
                scan.Candidates,
                destinationRoot,
                "{name}",
                ["import-tag"],
                cancellationToken: TestContext.Current.CancellationToken);

            var imported = Assert.Single(result.Items);
            Assert.True(imported.Succeeded, imported.Error);
            var destinationPath = imported.DestinationPath!;
            var destinationFaceSidecar = $"{destinationPath}.xmp";
            Assert.Equal(
                originalFaceSidecarBytes,
                await File.ReadAllBytesAsync(
                    destinationFaceSidecar,
                    TestContext.Current.CancellationToken));
            Assert.Equal(
                originalFaceSidecarBytes,
                await File.ReadAllBytesAsync(
                    sourceFaceSidecar,
                    TestContext.Current.CancellationToken));
            Assert.Contains("camera-tag", fixture.MetadataReader.ReadTags(destinationPath));
            Assert.Contains("import-tag", fixture.MetadataReader.ReadTags(destinationPath));
            Assert.Equal(2, imported.Identity!.SourceSidecars.Count);
            Assert.Equal(2, imported.Identity.DestinationSidecars.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_CancellationRemovesStagingFilesAndDoesNotIndexPartialOutput()
    {
        var directory = CreateTemporaryDirectory();
        var dcim = Path.Combine(directory, "camera", "DCIM");
        var destinationRoot = Path.Combine(directory, "library");
        Directory.CreateDirectory(dcim);
        Directory.CreateDirectory(destinationRoot);
        var sourcePath = Path.Combine(dcim, "large.cr2");

        try
        {
            await using (var source = new FileStream(
                sourcePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                var block = new byte[128 * 1024];
                for (var index = 0; index < 32; index++)
                    await source.WriteAsync(block, TestContext.Current.CancellationToken);
            }

            using var fixture = await CreateFixtureAsync(directory);
            var scan = await fixture.Service.ScanSourceAsync(
                Path.GetDirectoryName(dcim)!, TestContext.Current.CancellationToken);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            var progress = new CancelOnCopyProgress(cancellation);

            var result = await fixture.Service.ImportAsync(
                scan.Candidates,
                destinationRoot,
                "{name}",
                [],
                progress,
                cancellation.Token);

            Assert.True(result.Canceled);
            Assert.Empty(result.Items);
            Assert.Empty(Directory.EnumerateFileSystemEntries(destinationRoot));
            Assert.Null(await fixture.Images.GetByPathAsync(Path.Combine(
                destinationRoot,
                PhotoImportService.GetDateFolder(Assert.Single(scan.Candidates)),
                "large.cr2")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_RejectsDestinationInsideDcimTree()
    {
        var directory = CreateTemporaryDirectory();
        var dcim = Path.Combine(directory, "camera", "DCIM");
        Directory.CreateDirectory(dcim);
        var sourcePath = Path.Combine(dcim, "photo.cr2");

        try
        {
            await File.WriteAllBytesAsync(
                sourcePath, [1, 2, 3], TestContext.Current.CancellationToken);
            var destinationPath = Path.Combine(dcim, "Imported");
            Directory.CreateDirectory(destinationPath);
            using var fixture = await CreateFixtureAsync(directory);
            var scan = await fixture.Service.ScanSourceAsync(
                Path.GetDirectoryName(dcim)!, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<IOException>(() => fixture.Service.ImportAsync(
                scan.Candidates,
                destinationPath,
                "{name}",
                [],
                cancellationToken: TestContext.Current.CancellationToken));
            Assert.Empty(Directory.EnumerateFileSystemEntries(destinationPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_SanitizesPathSeparatorsInNamingTemplate()
    {
        var directory = CreateTemporaryDirectory();
        var dcim = Path.Combine(directory, "camera", "DCIM");
        var destinationRoot = Path.Combine(directory, "library");
        Directory.CreateDirectory(dcim);
        Directory.CreateDirectory(destinationRoot);
        var sourcePath = Path.Combine(dcim, "capture.cr2");

        try
        {
            await File.WriteAllBytesAsync(
                sourcePath, [1, 2, 3], TestContext.Current.CancellationToken);
            using var fixture = await CreateFixtureAsync(directory);
            var scan = await fixture.Service.ScanSourceAsync(
                Path.GetDirectoryName(dcim)!, TestContext.Current.CancellationToken);

            var result = await fixture.Service.ImportAsync(
                scan.Candidates,
                destinationRoot,
                @"..\..\{name}",
                [],
                cancellationToken: TestContext.Current.CancellationToken);

            var imported = Assert.Single(result.Items);
            Assert.True(imported.Succeeded, imported.Error);
            Assert.Equal(
                ".._.._capture.cr2",
                Path.GetFileName(imported.DestinationPath));
            Assert.StartsWith(
                Path.GetFullPath(Path.Combine(
                    destinationRoot,
                    PhotoImportService.GetDateFolder(Assert.Single(scan.Candidates)))) +
                    Path.DirectorySeparatorChar,
                Path.GetFullPath(imported.DestinationPath!),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_ReportsDeviceRemovalAfterScanWithoutPublishingPartialFiles()
    {
        var directory = CreateTemporaryDirectory();
        var dcim = Path.Combine(directory, "camera", "DCIM");
        var destinationRoot = Path.Combine(directory, "library");
        Directory.CreateDirectory(dcim);
        Directory.CreateDirectory(destinationRoot);
        var sourcePath = Path.Combine(dcim, "removed.cr2");

        try
        {
            await File.WriteAllBytesAsync(
                sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);
            using var fixture = await CreateFixtureAsync(directory);
            var scan = await fixture.Service.ScanSourceAsync(
                Path.GetDirectoryName(dcim)!, TestContext.Current.CancellationToken);
            File.Delete(sourcePath);

            var result = await fixture.Service.ImportAsync(
                scan.Candidates,
                destinationRoot,
                "{name}",
                [],
                cancellationToken: TestContext.Current.CancellationToken);

            var item = Assert.Single(result.Items);
            Assert.False(item.Succeeded);
            Assert.NotNull(item.Error);
            Assert.Empty(Directory.EnumerateFileSystemEntries(destinationRoot));
            Assert.Null(await fixture.Images.GetByPathAsync(Path.Combine(
                destinationRoot,
                PhotoImportService.GetDateFolder(Assert.Single(scan.Candidates)),
                "removed.cr2")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_IndexFailureRollsBackPublishedMediaAndSidecars()
    {
        var directory = CreateTemporaryDirectory();
        var dcim = Path.Combine(directory, "camera", "DCIM");
        var destinationRoot = Path.Combine(directory, "library");
        Directory.CreateDirectory(dcim);
        Directory.CreateDirectory(destinationRoot);
        var sourcePath = Path.Combine(dcim, "invalid-sidecar.jpg");
        var sourceSidecarPath = $"{sourcePath}.xmp";

        try
        {
            await File.WriteAllBytesAsync(
                sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                sourceSidecarPath, "not valid XMP", TestContext.Current.CancellationToken);
            using var fixture = await CreateFixtureAsync(directory);
            var scan = await fixture.Service.ScanSourceAsync(
                Path.GetDirectoryName(dcim)!, TestContext.Current.CancellationToken);

            var result = await fixture.Service.ImportAsync(
                scan.Candidates,
                destinationRoot,
                "{name}",
                [],
                cancellationToken: TestContext.Current.CancellationToken);

            var item = Assert.Single(result.Items);
            Assert.False(item.Succeeded);
            Assert.NotNull(item.Error);
            Assert.Empty(Directory.EnumerateFileSystemEntries(destinationRoot));
            Assert.Null(await fixture.Images.GetByPathAsync(Path.Combine(
                destinationRoot,
                PhotoImportService.GetDateFolder(Assert.Single(scan.Candidates)),
                "invalid-sidecar.jpg")));
            Assert.True(File.Exists(sourcePath));
            Assert.True(File.Exists(sourceSidecarPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteVerifiedSourcesAsync_PreservesSourceChangedAfterImport()
    {
        var directory = CreateTemporaryDirectory();
        var dcim = Path.Combine(directory, "camera", "DCIM");
        var destinationRoot = Path.Combine(directory, "library");
        Directory.CreateDirectory(dcim);
        Directory.CreateDirectory(destinationRoot);
        var sourcePath = Path.Combine(dcim, "photo.cr2");

        try
        {
            await File.WriteAllBytesAsync(
                sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);
            using var fixture = await CreateFixtureAsync(directory);
            var scan = await fixture.Service.ScanSourceAsync(
                Path.GetDirectoryName(dcim)!, TestContext.Current.CancellationToken);
            var imported = await fixture.Service.ImportAsync(
                scan.Candidates,
                destinationRoot,
                "{name}",
                [],
                cancellationToken: TestContext.Current.CancellationToken);
            var successful = Assert.Single(imported.SuccessfulItems);
            await File.WriteAllBytesAsync(
                sourcePath, [9, 8, 7, 6], TestContext.Current.CancellationToken);

            var deletion = Assert.Single(await fixture.Service.DeleteVerifiedSourcesAsync(
                imported.Items,
                cancellationToken: TestContext.Current.CancellationToken));

            Assert.False(deletion.Deleted);
            Assert.True(File.Exists(sourcePath));
            Assert.Equal(
                new byte[] { 9, 8, 7, 6 },
                await File.ReadAllBytesAsync(sourcePath, TestContext.Current.CancellationToken));
            Assert.NotNull(await fixture.Images.GetByPathAsync(successful.DestinationPath!));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteVerifiedSourcesAsync_DeletesVerifiedTemporarySourceAndPairedSidecar()
    {
        var directory = CreateTemporaryDirectory();
        var dcim = Path.Combine(directory, "camera", "DCIM");
        var destinationRoot = Path.Combine(directory, "library");
        Directory.CreateDirectory(dcim);
        Directory.CreateDirectory(destinationRoot);
        var sourcePath = Path.Combine(dcim, "photo.cr2");
        var sourceSidecarPath = Path.ChangeExtension(sourcePath, ".xmp");

        try
        {
            await File.WriteAllBytesAsync(
                sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);
            await TagWriterService.WriteTagsToSidecarAsync(sourcePath, ["camera-tag"]);
            using var fixture = await CreateFixtureAsync(directory);
            var scan = await fixture.Service.ScanSourceAsync(
                Path.GetDirectoryName(dcim)!, TestContext.Current.CancellationToken);
            var imported = await fixture.Service.ImportAsync(
                scan.Candidates,
                destinationRoot,
                "{name}",
                [],
                cancellationToken: TestContext.Current.CancellationToken);

            var deletion = Assert.Single(await fixture.Service.DeleteVerifiedSourcesAsync(
                imported.Items,
                cancellationToken: TestContext.Current.CancellationToken));

            Assert.True(deletion.Deleted);
            Assert.False(File.Exists(sourcePath));
            Assert.False(File.Exists(sourceSidecarPath));
            Assert.True(File.Exists(imported.SuccessfulItems[0].DestinationPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanSourceAsync_SkipsAmbiguousSidecarsAndRequiresDcim()
    {
        var directory = CreateTemporaryDirectory();
        var dcim = Path.Combine(directory, "camera", "DCIM");
        var mediaDirectory = Path.Combine(dcim, "100MEDIA");
        Directory.CreateDirectory(mediaDirectory);

        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(mediaDirectory, "shared.cr2"),
                [1],
                TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(
                Path.Combine(mediaDirectory, "shared.jpg"),
                [2],
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(mediaDirectory, "shared.xmp"),
                "ambiguous sidecar",
                TestContext.Current.CancellationToken);
            using var fixture = await CreateFixtureAsync(directory);

            var scan = await fixture.Service.ScanSourceAsync(
                Path.GetDirectoryName(dcim)!, TestContext.Current.CancellationToken);

            Assert.Empty(scan.Candidates);
            Assert.Equal(2, scan.Warnings.Count);
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
                fixture.Service.ScanSourceAsync(
                    directory, TestContext.Current.CancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RemovableDeviceTracker_ReportsInsertionAfterRemovalAndIgnoresInitialDevices()
    {
        var tracker = new RemovableDeviceTracker();
        var initial = Path.Combine(Path.GetTempPath(), $"camera-{Guid.NewGuid():N}");
        var inserted = Path.Combine(Path.GetTempPath(), $"camera-{Guid.NewGuid():N}");

        Assert.Equal([Path.GetFullPath(initial)], tracker.Refresh([initial]));
        Assert.Equal([Path.GetFullPath(inserted)], tracker.Refresh([initial, inserted]));
        Assert.Empty(tracker.Refresh([initial]));
        Assert.Equal([Path.GetFullPath(inserted)], tracker.Refresh([initial, inserted]));
    }

    private static async Task<ImportFixture> CreateFixtureAsync(string directory)
    {
        var database = new CacheDatabase(Path.Combine(directory, $"cache-{Guid.NewGuid():N}.db"));
        await database.InitializeAsync();
        var images = new ImageRepository(database);
        var metadataReader = new MetadataReaderService();
        var scanner = new FolderScannerService();
        var indexer = new LibraryIndexingService(
            database,
            images,
            new TagRepository(database),
            new FaceRepository(database),
            scanner,
            metadataReader,
            new FaceMetadataStore());
        return new ImportFixture(
            database,
            scanner,
            images,
            metadataReader,
            new PhotoImportService(metadataReader, indexer, images));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ImportFixture(
        CacheDatabase database,
        FolderScannerService scanner,
        ImageRepository images,
        MetadataReaderService metadataReader,
        PhotoImportService service) : IDisposable
    {
        public ImageRepository Images { get; } = images;
        public MetadataReaderService MetadataReader { get; } = metadataReader;
        public PhotoImportService Service { get; } = service;

        public void Dispose()
        {
            scanner.Dispose();
            database.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    private sealed class CancelOnCopyProgress(CancellationTokenSource cancellation) :
        IProgress<PhotoImportProgress>
    {
        public void Report(PhotoImportProgress value)
        {
            if (value.Stage == PhotoImportStage.Copying && value.BytesCopied > 0)
                cancellation.Cancel();
        }
    }
}
