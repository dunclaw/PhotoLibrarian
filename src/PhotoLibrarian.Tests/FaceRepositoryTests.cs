using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class FaceRepositoryTests
{
    [Fact]
    public async Task ReplaceFaceRegions_MarksImageScannedAndFileChangeInvalidatesScan()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}.db");

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var image = CreateImage(MediaType.Image);
            image.Id = await images.UpsertImageAsync(image);
            var video = CreateImage(MediaType.Video);
            video.FilePath = @"C:\Photos\clip.mp4";
            video.FileName = "clip.mp4";
            await images.UpsertImageAsync(video);

            var cancellationToken = TestContext.Current.CancellationToken;
            Assert.Single(await faces.GetImagesNeedingFaceScanAsync("pipeline-v1", cancellationToken));

            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                image.Id,
                image.FileSize,
                image.DateModified,
                [
                    new FaceRegion
                    {
                        ImageId = image.Id,
                        X = 0.1,
                        Y = 0.2,
                        Width = 0.3,
                        Height = 0.4,
                        Confidence = 0.9f,
                        Embedding = [0.25f, 0.75f]
                    }
                ],
                "pipeline-v1",
                cancellationToken));

            Assert.Empty(await faces.GetImagesNeedingFaceScanAsync("pipeline-v1", cancellationToken));
            var storedFace = Assert.Single(await faces.GetFacesForImageAsync(image.Id));
            Assert.NotNull(storedFace.Embedding);
            Assert.Equal([0.25f, 0.75f], storedFace.Embedding);

            image.FileSize++;
            image.DateModified = image.DateModified.AddSeconds(1);
            await images.UpsertImageAsync(image);

            Assert.False(await faces.TryReplaceFaceRegionsAsync(
                image.Id,
                expectedFileSize: image.FileSize - 1,
                expectedDateModified: image.DateModified.AddSeconds(-1),
                faces: [],
                scanVersion: "pipeline-v1",
                cancellationToken));
            Assert.Single(await faces.GetFacesForImageAsync(image.Id));

            var pending = Assert.Single(
                await faces.GetImagesNeedingFaceScanAsync("pipeline-v1", cancellationToken));
            Assert.Equal(image.Id, pending.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteIfPresent(databasePath);
            DeleteIfPresent(databasePath + "-wal");
            DeleteIfPresent(databasePath + "-shm");
        }
    }

    private static ImageEntry CreateImage(MediaType mediaType) => new()
    {
        FilePath = @"C:\Photos\portrait.jpg",
        FileName = "portrait.jpg",
        FileSize = 100,
        DateModified = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        DateIndexed = DateTime.UtcNow,
        MediaType = mediaType
    };

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
