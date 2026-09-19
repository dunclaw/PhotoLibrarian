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
