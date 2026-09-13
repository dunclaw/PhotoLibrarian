using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class FaceRepositoryTests
{
    [Fact]
    public async Task ReplaceFaceRegions_MarksImageScannedAndFileChangeInvalidatesScan()
    {
        var databasePath = CreateDatabasePath();

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
            var pendingImage = Assert.Single(
                await faces.GetImagesNeedingFaceScanAsync("pipeline-v1", cancellationToken));

            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                pendingImage.Id,
                pendingImage.FileSize,
                pendingImage.DateModified,
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
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ReplaceFaceRegions_PreservesReviewStateForMatchingFace()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var image = CreateImage(MediaType.Image);
            image.Id = await images.UpsertImageAsync(image);
            var personId = await faces.CreatePersonAsync("Alex");
            var rejectedPersonId = await faces.CreatePersonAsync("Sam");
            var cancellationToken = TestContext.Current.CancellationToken;
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
                        PersonId = personId,
                        PersonName = "Alex",
                        Confidence = 0.9f,
                        Embedding = [0.25f, 0.75f]
                    }
                ],
                "pipeline-v1",
                cancellationToken));
            var originalFaceId = Assert.Single(
                await faces.GetFacesForImageAsync(image.Id)).Id;
            await faces.SetPersonRepresentativeFaceAsync(
                personId,
                originalFaceId,
                cancellationToken);
            await faces.RejectPersonForFacesAsync(
                [originalFaceId],
                rejectedPersonId,
                cancellationToken);
            await faces.HideFacesFromSuggestionsAsync(
                [originalFaceId],
                cancellationToken);

            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                image.Id,
                image.FileSize,
                image.DateModified,
                [
                    new FaceRegion
                    {
                        ImageId = image.Id,
                        X = 0.11,
                        Y = 0.21,
                        Width = 0.3,
                        Height = 0.4,
                        Confidence = 0.95f,
                        Embedding = [0.3f, 0.7f]
                    }
                ],
                "pipeline-v2",
                cancellationToken));

            var rescannedFace = Assert.Single(
                await faces.GetFacesForImageAsync(image.Id));
            Assert.Equal(originalFaceId, rescannedFace.Id);
            Assert.Equal(personId, rescannedFace.PersonId);
            Assert.Equal("Alex", rescannedFace.PersonName);
            Assert.NotNull(rescannedFace.Embedding);
            Assert.Equal([0.3f, 0.7f], rescannedFace.Embedding);
            Assert.Contains(
                rejectedPersonId,
                (await faces.GetRejectedPersonIdsByFaceIdAsync(cancellationToken))[
                    originalFaceId]);
            Assert.Contains(
                originalFaceId,
                await faces.GetHiddenFaceSuggestionIdsAsync(cancellationToken));
            Assert.Equal(
                originalFaceId,
                Assert.Single(
                    await faces.GetAllPersonsAsync(),
                    person => person.Id == personId)
                    .RepresentativeFaceRegionId);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task DeletingImage_ClearsRepresentativeFaceReference()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var image = CreateImage(MediaType.Image);
            image.Id = await images.UpsertImageAsync(image);
            var personId = await faces.CreatePersonAsync("Alex");
            var cancellationToken = TestContext.Current.CancellationToken;
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
                        PersonId = personId,
                        PersonName = "Alex"
                    }
                ],
                "pipeline-v1",
                cancellationToken));
            await faces.SetPersonRepresentativeFaceAsync(
                personId,
                Assert.Single(await faces.GetFacesForImageAsync(image.Id)).Id,
                cancellationToken);

            await images.DeleteByPathAsync(image.FilePath);

            Assert.Null(
                Assert.Single(await faces.GetAllPersonsAsync())
                    .RepresentativeFaceRegionId);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task RemapFaceRegionsAfterCrop_UpdatesClippedFacesAndDeletesOutsideFaces()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var image = CreateImage(MediaType.Image);
            image.Id = await images.UpsertImageAsync(image);
            var personId = await faces.CreatePersonAsync("Alex");

            var cancellationToken = TestContext.Current.CancellationToken;
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                image.Id,
                image.FileSize,
                image.DateModified,
                [
                    new FaceRegion
                    {
                        ImageId = image.Id,
                        X = 0.1,
                        Y = 0.25,
                        Width = 0.4,
                        Height = 0.5,
                        PersonId = personId,
                        PersonName = "Alex",
                        Embedding = [0.25f, 0.75f],
                        Confidence = 0.9f
                    },
                    new FaceRegion
                    {
                        ImageId = image.Id,
                        X = 0,
                        Y = 0,
                        Width = 0.1,
                        Height = 0.1
                    }
                ],
                "pipeline-v1",
                cancellationToken));
            var keptId = (await faces.GetFacesForImageAsync(image.Id))
                .Single(face => face.PersonId == personId)
                .Id;

            await faces.RemapFaceRegionsAfterCropAsync(
                image.Id,
                100,
                80,
                new CropRectangle(25, 20, 50, 40),
                cancellationToken);
            await images.UpdateDimensionsAsync(
                image.Id,
                50,
                40,
                image.FileSize + 1,
                image.DateModified.AddSeconds(1),
                invalidateFaceScan: false);

            var remapped = Assert.Single(await faces.GetFacesForImageAsync(image.Id));
            Assert.Equal(keptId, remapped.Id);
            Assert.Equal("Alex", remapped.PersonName);
            Assert.Equal(personId, remapped.PersonId);
            Assert.NotNull(remapped.Embedding);
            Assert.Equal([0.25f, 0.75f], remapped.Embedding);
            Assert.Equal(0, remapped.X, 6);
            Assert.Equal(0, remapped.Y, 6);
            Assert.Equal(0.5, remapped.Width, 6);
            Assert.Equal(1, remapped.Height, 6);
            Assert.Empty(await faces.GetImagesNeedingFaceScanAsync("pipeline-v1", cancellationToken));

            image.FileSize++;
            image.Width = 50;
            image.Height = 40;
            image.DateModified = image.DateModified.AddSeconds(1);
            await images.UpsertImageAsync(image);
            Assert.Empty(await faces.GetImagesNeedingFaceScanAsync("pipeline-v1", cancellationToken));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ImportFaceMetadata_RebuildsPeopleReviewStateAndPreservesItDuringEmbeddingScan()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var image = CreateImage(MediaType.Image);
            image.Id = await images.UpsertImageAsync(image);
            var cancellationToken = TestContext.Current.CancellationToken;

            await faces.ImportFaceMetadataAsync(
                image.Id,
                new PhotoFaceMetadata(
                    1200,
                    800,
                    [
                        new PortableFaceMetadata(
                            0,
                            0.1,
                            0.2,
                            0.3,
                            0.4,
                            "Alex",
                            false,
                            true,
                            ["Sam"]),
                        new PortableFaceMetadata(
                            0,
                            0.6,
                            0.2,
                            0.2,
                            0.3,
                            null,
                            true,
                            false,
                            [])
                    ]),
                cancellationToken);

            var people = await faces.GetAllPersonsAsync();
            var alex = Assert.Single(people, person => person.Name == "Alex");
            var sam = Assert.Single(people, person => person.Name == "Sam");
            Assert.True(alex.SuggestionsHidden);
            var imported = await faces.GetFacesForImageAsync(image.Id);
            Assert.All(imported, face => Assert.Null(face.Embedding));
            var assigned = Assert.Single(imported, face => face.PersonName == "Alex");
            var excluded = Assert.Single(imported, face => face.PersonName is null);
            Assert.Contains(
                sam.Id,
                (await faces.GetRejectedPersonIdsByFaceIdAsync(cancellationToken))[
                    assigned.Id]);
            Assert.Contains(
                excluded.Id,
                await faces.GetHiddenFaceSuggestionIdsAsync(cancellationToken));
            Assert.Contains(
                image.Id,
                (await faces.GetImagesNeedingFaceScanAsync(
                    "pipeline-v1",
                    cancellationToken)).Select(entry => entry.Id));

            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                image.Id,
                image.FileSize,
                image.DateModified,
                [
                    new FaceRegion
                    {
                        ImageId = image.Id,
                        X = 0.11,
                        Y = 0.21,
                        Width = 0.3,
                        Height = 0.4,
                        Confidence = 0.9f,
                        Embedding = [0.25f, 0.75f]
                    },
                    new FaceRegion
                    {
                        ImageId = image.Id,
                        X = 0.61,
                        Y = 0.21,
                        Width = 0.2,
                        Height = 0.3,
                        Confidence = 0.8f,
                        Embedding = [0.75f, 0.25f]
                    }
                ],
                "pipeline-v1",
                cancellationToken));

            var rescanned = await faces.GetFacesForImageAsync(image.Id);
            Assert.Equal(
                alex.Id,
                Assert.Single(rescanned, face => face.PersonName == "Alex")
                    .PersonId);
            Assert.All(rescanned, face => Assert.NotNull(face.Embedding));
            Assert.Contains(
                excluded.Id,
                await faces.GetHiddenFaceSuggestionIdsAsync(cancellationToken));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ImportEmptyFaceMetadata_ClearsPortableStateWithoutDeletingDetectedGeometry()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var image = CreateImage(MediaType.Image);
            image.Id = await images.UpsertImageAsync(image);
            var personId = await faces.CreatePersonAsync("Alex");
            var cancellationToken = TestContext.Current.CancellationToken;
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
                        PersonId = personId,
                        PersonName = "Alex",
                        Embedding = [0.25f, 0.75f],
                        IsMetadataManaged = true
                    }
                ],
                "pipeline-v1",
                cancellationToken));
            var faceId = Assert.Single(
                await faces.GetFacesForImageAsync(image.Id)).Id;
            await faces.HideFacesFromSuggestionsAsync(
                [faceId],
                cancellationToken);

            await faces.ImportFaceMetadataAsync(
                image.Id,
                new PhotoFaceMetadata(0, 0, []),
                cancellationToken);

            var retained = Assert.Single(
                await faces.GetFacesForImageAsync(image.Id));
            Assert.Null(retained.PersonId);
            Assert.Null(retained.PersonName);
            Assert.False(retained.IsMetadataManaged);
            Assert.NotNull(retained.Embedding);
            Assert.Empty(
                await faces.GetHiddenFaceSuggestionIdsAsync(cancellationToken));
            Assert.Empty(await faces.GetAllPersonsAsync());
            Assert.Contains(
                image.Id,
                (await faces.GetImagesNeedingFaceScanAsync(
                    "pipeline-v1",
                    cancellationToken)).Select(entry => entry.Id));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    private static string CreateDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-{Guid.NewGuid():N}.db");

    private static ImageEntry CreateImage(MediaType mediaType) => new()
    {
        FilePath = @"C:\Photos\portrait.jpg",
        FileName = "portrait.jpg",
        FileSize = 100,
        DateModified = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        DateIndexed = DateTime.UtcNow,
        MediaType = mediaType
    };

    private static void DeleteDatabase(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        DeleteIfPresent(databasePath);
        DeleteIfPresent(databasePath + "-wal");
        DeleteIfPresent(databasePath + "-shm");
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
