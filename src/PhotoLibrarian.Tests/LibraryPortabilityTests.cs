// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class LibraryPortabilityTests
{
    [Fact]
    public async Task LegacyFaceMetadataDoesNotClearAnUnexportedPictureChoice()
    {
        var root = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var database = new CacheDatabase(Path.Combine(root, "legacy.db"));
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var imageId = await images.UpsertImageAsync(new ImageEntry
            {
                FilePath = Path.Combine(root, "photo.png"),
                FileName = "photo.png",
                DateModified = DateTime.UtcNow
            });
            var metadata = new PhotoFaceMetadata(
                120, 80,
                [new PortableFaceMetadata(0, 0.1, 0.2, 0.3, 0.4, "Alex", false, false, [])]);
            var ct = TestContext.Current.CancellationToken;
            await faces.ImportFaceMetadataAsync(imageId, metadata, ct);
            var person = Assert.Single(await faces.GetAllPersonsAsync());
            var face = Assert.Single(await faces.GetFacesForImageAsync(imageId));
            await faces.SetPersonRepresentativeFaceAsync(person.Id, face.Id, ct);

            await faces.ImportFaceMetadataAsync(imageId, metadata, ct);

            Assert.Equal(face.Id, Assert.Single(await faces.GetAllPersonsAsync()).RepresentativeFaceRegionId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".cr3")]
    public async Task PersonPictureSurvivesMovingOnlyPhotosAndMetadataToFreshDatabase(
        string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-{Guid.NewGuid():N}");
        var photos = Path.Combine(root, "photos");
        var movedPhotos = Path.Combine(root, "moved");
        Directory.CreateDirectory(photos);
        Directory.CreateDirectory(movedPhotos);
        var firstPath = Path.Combine(photos, $"first{extension}");
        var secondPath = Path.Combine(photos, $"second{extension}");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await WicTestImage.CreateAsync(firstPath, 120, 80);
            await WicTestImage.CreateAsync(secondPath, 120, 80);
            using (var original = new CacheDatabase(Path.Combine(root, "original.db")))
            {
                await original.InitializeAsync();
                var images = new ImageRepository(original);
                var faces = new FaceRepository(original);
                using var scanner = new FolderScannerService();
                var store = new FaceMetadataStore();
                var indexer = new LibraryIndexingService(
                    original, images, new TagRepository(original), faces,
                    scanner, new MetadataReaderService(), store);
                foreach (var path in new[] { firstPath, secondPath })
                {
                    await TagWriterService.WriteTagsToSidecarAsync(path, ["Travel/Family"]);
                    await MetadataWriterService.WriteFlagAsync(path, true);
                    await store.WriteAsync(path, new PhotoFaceMetadata(
                        120, 80,
                        [new PortableFaceMetadata(
                            0, 0.1, 0.2, 0.3, 0.4, "Alex", true, true, ["Sam"])]), ct);
                    await indexer.IndexFileAsync(path, ct);
                }

                var person = Assert.Single(await faces.GetAllPersonsAsync(), p => p.Name == "Alex");
                var firstImage = (await images.GetByPathAsync(firstPath))!;
                var secondImage = (await images.GetByPathAsync(secondPath))!;
                var firstFace = Assert.Single(await faces.GetFacesForImageAsync(firstImage.Id));
                var secondFace = Assert.Single(await faces.GetFacesForImageAsync(secondImage.Id));
                var service = new FaceReviewService(
                    faces, images, new FaceClusteringService(),
                    new FaceRecognitionService(), store);
                await service.SetPersonRepresentativeFaceAsync(person.Id, firstFace.Id, ct);
                await service.SetPersonRepresentativeFaceAsync(person.Id, secondFace.Id, ct);
                Assert.False(Assert.Single(store.Read(firstPath).Faces).IsPersonRepresentative);
                Assert.True(Assert.Single(store.Read(secondPath).Faces).IsPersonRepresentative);
            }

            foreach (var file in Directory.EnumerateFiles(photos))
            {
                File.Move(file, Path.Combine(movedPhotos, Path.GetFileName(file)));
            }

            // Neither the old SQLite database nor its IDs are carried to the new library.
            using var rebuilt = new CacheDatabase(Path.Combine(root, "rebuilt.db"));
            await rebuilt.InitializeAsync();
            var rebuiltImages = new ImageRepository(rebuilt);
            var rebuiltFaces = new FaceRepository(rebuilt);
            var rebuiltTags = new TagRepository(rebuilt);
            await rebuiltFaces.CreatePersonAsync("Unrelated person");
            using var rebuiltScanner = new FolderScannerService();
            var rebuiltIndexer = new LibraryIndexingService(
                rebuilt, rebuiltImages, rebuiltTags, rebuiltFaces,
                rebuiltScanner, new MetadataReaderService(), new FaceMetadataStore());
            // The old representative's explicit false must not clear the new selection.
            foreach (var name in new[] { Path.GetFileName(secondPath), Path.GetFileName(firstPath) })
            {
                await rebuiltIndexer.IndexFileAsync(Path.Combine(movedPhotos, name), ct);
            }

            var alex = Assert.Single(await rebuiltFaces.GetAllPersonsAsync(), p => p.Name == "Alex");
            Assert.True(alex.SuggestionsHidden);
            foreach (var name in new[] { Path.GetFileName(firstPath), Path.GetFileName(secondPath) })
            {
                var image = (await rebuiltImages.GetByPathAsync(Path.Combine(movedPhotos, name)))!;
                Assert.True(image.IsFlagged);
                Assert.Contains(await rebuiltTags.GetTagsAsync(image.Id), t => t.Tag == "Travel/Family");
                var face = Assert.Single(await rebuiltFaces.GetFacesForImageAsync(image.Id));
                Assert.Equal("Alex", face.PersonName);
                Assert.Contains(face.Id, await rebuiltFaces.GetHiddenFaceSuggestionIdsAsync(ct));
                var metadata = await rebuiltFaces.GetPhotoFaceMetadataAsync(image.Id, 120, 80, ct);
                Assert.Contains("Sam", Assert.Single(metadata.Faces).RejectedPersonNames);
                Assert.Equal(name == Path.GetFileName(secondPath), alex.RepresentativeFaceRegionId == face.Id);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
