using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class FaceReviewServiceTests
{
    [Fact]
    public async Task SuggestionGroups_IncludeFacesAcrossClusteringBatches()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var image = CreateImage();
            image.Id = await images.UpsertImageAsync(image);
            var cancellationToken = TestContext.Current.CancellationToken;
            var regions = Enumerable.Range(0, 513)
                .Select(index => CreateFace(
                    image.Id,
                    (index % 10) / 10.0,
                    [1f, 0f]))
                .ToList();

            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                image.Id,
                image.FileSize,
                image.DateModified,
                regions,
                "pipeline-v1",
                cancellationToken));
            var service = new FaceReviewService(
                faces,
                images,
                new FaceClusteringService(),
                new FaceRecognitionService());

            var groups = await service.GetSuggestionGroupsAsync(cancellationToken);

            Assert.Equal(513, groups.Sum(group => group.Suggestions.Count));
            Assert.True(groups.Count >= 2);

            var faceIds = (await faces.GetFacesForImageAsync(image.Id))
                .Select(face => face.Id)
                .ToList();
            var personId = await faces.CreatePersonAsync("Performance test");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await faces.RejectPersonForFacesAsync(
                faceIds,
                personId,
                cancellationToken);
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                $"Persisting 513 rejections took {stopwatch.ElapsedMilliseconds} ms.");
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task FaceThumbnailCropper_CreatesSquareFacePreview()
    {
        var imagePath = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}.png");
        try
        {
            await WicTestImage.CreateAsync(imagePath, 100, 80);

            var bytes = await FaceThumbnailCropper.CreateAsync(
                imagePath,
                new FaceRegion
                {
                    X = 0.25,
                    Y = 0.20,
                    Width = 0.20,
                    Height = 0.25
                },
                64,
                TestContext.Current.CancellationToken);

            Assert.Equal((64u, 64u), await WicTestImage.ReadPngSizeAsync(bytes));
        }
        finally
        {
            if (File.Exists(imagePath)) File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task FaceThumbnailCropper_UsesWindowsDecoderForRawExtension()
    {
        var imagePath = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}.cr3");
        try
        {
            await WicTestImage.CreateAsync(imagePath, 100, 80);

            var bytes = await FaceThumbnailCropper.CreateAsync(
                imagePath,
                new FaceRegion
                {
                    X = 0.25,
                    Y = 0.20,
                    Width = 0.20,
                    Height = 0.25
                },
                64,
                TestContext.Current.CancellationToken);

            Assert.Equal((64u, 64u), await WicTestImage.ReadPngSizeAsync(bytes));
        }
        finally
        {
            if (File.Exists(imagePath)) File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task SuggestionGroups_RerankRejectedFaceToNextClosestPerson()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var alexImage = CreateImage(@"C:\Photos\alex.jpg");
            alexImage.Id = await images.UpsertImageAsync(alexImage);
            var samImage = CreateImage(@"C:\Photos\sam.jpg");
            samImage.Id = await images.UpsertImageAsync(samImage);
            var candidateImage = CreateImage(@"C:\Photos\candidate.jpg");
            candidateImage.Id = await images.UpsertImageAsync(candidateImage);
            var alexId = await faces.CreatePersonAsync("Alex");
            var samId = await faces.CreatePersonAsync("Sam");
            var cancellationToken = TestContext.Current.CancellationToken;

            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                alexImage.Id,
                alexImage.FileSize,
                alexImage.DateModified,
                [CreateFace(alexImage.Id, 0.10, [1f, 0f], alexId, "Alex")],
                "pipeline-v1",
                cancellationToken));
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                samImage.Id,
                samImage.FileSize,
                samImage.DateModified,
                [CreateFace(samImage.Id, 0.30, [0f, 1f], samId, "Sam")],
                "pipeline-v1",
                cancellationToken));
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                candidateImage.Id,
                candidateImage.FileSize,
                candidateImage.DateModified,
                [CreateFace(candidateImage.Id, 0.50, [0.8f, 0.6f])],
                "pipeline-v1",
                cancellationToken));

            var candidateId = (await faces.GetFacesForImageAsync(candidateImage.Id))
                .Single(face => face.X == 0.50).Id;
            var service = new FaceReviewService(
                faces,
                images,
                new FaceClusteringService(),
                new FaceRecognitionService());

            var initialGroups = await service.GetSuggestionGroupsAsync(cancellationToken);
            var alexGroup = Assert.Single(
                initialGroups,
                group => group.SuggestedPersonId == alexId);
            var initialSuggestion = Assert.Single(alexGroup.Suggestions);
            Assert.Equal(candidateId, initialSuggestion.FaceRegionId);
            Assert.Equal(0.8f, initialSuggestion.Similarity!.Value, 3);
            Assert.Equal(
                [alexId, samId],
                initialSuggestion.Candidates.Select(candidate => candidate.PersonId));

            await service.RejectAsync(alexGroup, [candidateId], cancellationToken);

            var rerankedGroups = await service.GetSuggestionGroupsAsync(cancellationToken);
            Assert.DoesNotContain(
                rerankedGroups,
                group => group.SuggestedPersonId == alexId);
            var samGroup = Assert.Single(
                rerankedGroups,
                group => group.SuggestedPersonId == samId);
            var rerankedSuggestion = Assert.Single(samGroup.Suggestions);
            Assert.Equal(candidateId, rerankedSuggestion.FaceRegionId);
            Assert.Equal(0.6f, rerankedSuggestion.Similarity!.Value, 3);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task SuggestionGroups_DoNotSuggestSamePersonTwiceInOnePhoto()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var alexImage = CreateImage(@"C:\Photos\alex.jpg");
            alexImage.Id = await images.UpsertImageAsync(alexImage);
            var samImage = CreateImage(@"C:\Photos\sam.jpg");
            samImage.Id = await images.UpsertImageAsync(samImage);
            var groupImage = CreateImage(@"C:\Photos\group.jpg");
            groupImage.Id = await images.UpsertImageAsync(groupImage);
            var alexId = await faces.CreatePersonAsync("Alex");
            var samId = await faces.CreatePersonAsync("Sam");
            var cancellationToken = TestContext.Current.CancellationToken;

            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                alexImage.Id,
                alexImage.FileSize,
                alexImage.DateModified,
                [CreateFace(alexImage.Id, 0.10, [1f, 0f], alexId, "Alex")],
                "pipeline-v1",
                cancellationToken));
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                samImage.Id,
                samImage.FileSize,
                samImage.DateModified,
                [CreateFace(samImage.Id, 0.10, [0.7f, 0.714f], samId, "Sam")],
                "pipeline-v1",
                cancellationToken));
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                groupImage.Id,
                groupImage.FileSize,
                groupImage.DateModified,
                [
                    CreateFace(groupImage.Id, 0.20, [0.99f, 0.1f]),
                    CreateFace(groupImage.Id, 0.60, [0.95f, 0.312f])
                ],
                "pipeline-v1",
                cancellationToken));

            var service = new FaceReviewService(
                faces,
                images,
                new FaceClusteringService(),
                new FaceRecognitionService());

            var groups = await service.GetSuggestionGroupsAsync(cancellationToken);

            Assert.Single(
                Assert.Single(groups, group => group.SuggestedPersonId == alexId).Suggestions);
            Assert.Single(
                Assert.Single(groups, group => group.SuggestedPersonId == samId).Suggestions);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task HideFaces_RemovesUnnamedSuggestionsFromFutureGroups()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var image = CreateImage();
            image.Id = await images.UpsertImageAsync(image);
            var cancellationToken = TestContext.Current.CancellationToken;

            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                image.Id,
                image.FileSize,
                image.DateModified,
                [CreateFace(image.Id, 0.20, [1f, 0f])],
                "pipeline-v1",
                cancellationToken));
            var service = new FaceReviewService(
                faces,
                images,
                new FaceClusteringService(),
                new FaceRecognitionService());
            var suggestion = Assert.Single(
                Assert.Single(await service.GetSuggestionGroupsAsync(cancellationToken))
                    .Suggestions);

            await service.HideFacesAsync(
                [suggestion.FaceRegionId],
                cancellationToken);

            Assert.Contains(
                suggestion.FaceRegionId,
                await faces.GetHiddenFaceSuggestionIdsAsync(cancellationToken));
            Assert.Empty(await service.GetSuggestionGroupsAsync(cancellationToken));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task HideFaces_DoesNotRerouteSuggestionToAnotherPerson()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var alexImage = CreateImage(@"C:\Photos\alex.jpg");
            alexImage.Id = await images.UpsertImageAsync(alexImage);
            var samImage = CreateImage(@"C:\Photos\sam.jpg");
            samImage.Id = await images.UpsertImageAsync(samImage);
            var candidateImage = CreateImage(@"C:\Photos\candidate.jpg");
            candidateImage.Id = await images.UpsertImageAsync(candidateImage);
            var alexId = await faces.CreatePersonAsync("Alex");
            var samId = await faces.CreatePersonAsync("Sam");
            var cancellationToken = TestContext.Current.CancellationToken;

            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                alexImage.Id,
                alexImage.FileSize,
                alexImage.DateModified,
                [CreateFace(alexImage.Id, 0.10, [1f, 0f], alexId, "Alex")],
                "pipeline-v1",
                cancellationToken));
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                samImage.Id,
                samImage.FileSize,
                samImage.DateModified,
                [CreateFace(samImage.Id, 0.30, [0f, 1f], samId, "Sam")],
                "pipeline-v1",
                cancellationToken));
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                candidateImage.Id,
                candidateImage.FileSize,
                candidateImage.DateModified,
                [CreateFace(candidateImage.Id, 0.50, [0.8f, 0.6f])],
                "pipeline-v1",
                cancellationToken));

            var service = new FaceReviewService(
                faces,
                images,
                new FaceClusteringService(),
                new FaceRecognitionService());
            var initialGroup = Assert.Single(
                await service.GetSuggestionGroupsAsync(cancellationToken),
                group => group.SuggestedPersonId == alexId);
            var suggestion = Assert.Single(initialGroup.Suggestions);
            Assert.Equal(
                [alexId, samId],
                suggestion.Candidates.Select(candidate => candidate.PersonId));

            await service.HideFacesAsync(
                [suggestion.FaceRegionId],
                cancellationToken);

            var reopenedGroups = await service.GetSuggestionGroupsAsync(cancellationToken);
            Assert.DoesNotContain(
                reopenedGroups.SelectMany(group => group.Suggestions),
                item => item.FaceRegionId == suggestion.FaceRegionId);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task PeopleManagement_LoadsAssignedAndExcludedFacesAndRestoresExcludedFace()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var assignedImage = CreateImage(@"C:\Photos\assigned.jpg");
            assignedImage.Id = await images.UpsertImageAsync(assignedImage);
            var excludedImage = CreateImage(@"C:\Photos\excluded.jpg");
            excludedImage.Id = await images.UpsertImageAsync(excludedImage);
            var personId = await faces.CreatePersonAsync("Alex");
            var cancellationToken = TestContext.Current.CancellationToken;

            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                assignedImage.Id,
                assignedImage.FileSize,
                assignedImage.DateModified,
                [CreateFace(assignedImage.Id, 0.10, [1f, 0f], personId, "Alex")],
                "pipeline-v1",
                cancellationToken));
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                excludedImage.Id,
                excludedImage.FileSize,
                excludedImage.DateModified,
                [CreateFace(excludedImage.Id, 0.30, [0f, 1f])],
                "pipeline-v1",
                cancellationToken));
            var excludedFaceId = Assert.Single(
                await faces.GetFacesForImageAsync(excludedImage.Id)).Id;
            await faces.HideFacesFromSuggestionsAsync(
                [excludedFaceId],
                cancellationToken);
            var service = new FaceReviewService(
                faces,
                images,
                new FaceClusteringService(),
                new FaceRecognitionService());

            var snapshot = await service.GetPeopleManagementAsync(cancellationToken);

            var person = Assert.Single(snapshot.People);
            Assert.Equal("Alex", person.Person.Name);
            Assert.Single(person.Faces);
            Assert.Equal(
                excludedFaceId,
                Assert.Single(snapshot.HiddenFaces).FaceRegionId);

            await service.RestoreHiddenFacesAsync(
                [excludedFaceId],
                cancellationToken);

            Assert.Empty(
                (await service.GetPeopleManagementAsync(cancellationToken)).HiddenFaces);
            Assert.Contains(
                (await service.GetSuggestionGroupsAsync(cancellationToken))
                    .SelectMany(group => group.Suggestions),
                face => face.FaceRegionId == excludedFaceId);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task TagAsNewPerson_DoesNotLeaveEmptyPersonWhenAssignmentFails()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var faces = new FaceRepository(database);
            var service = new FaceReviewService(
                faces,
                new ImageRepository(database),
                new FaceClusteringService(),
                new FaceRecognitionService());

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.TagAsAsync(
                    [long.MaxValue],
                    null,
                    "Alex",
                    TestContext.Current.CancellationToken));

            Assert.Empty(await faces.GetAllPersonsAsync());
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task UndoTag_RestoresAssignmentDecisionsAndRepresentativePicture()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var image = CreateImage(@"C:\Photos\alex.jpg");
            image.Id = await images.UpsertImageAsync(image);
            var alexId = await faces.CreatePersonAsync("Alex");
            var samId = await faces.CreatePersonAsync("Sam");
            var rejectedPersonId = await faces.CreatePersonAsync("Taylor");
            var cancellationToken = TestContext.Current.CancellationToken;
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                image.Id,
                image.FileSize,
                image.DateModified,
                [CreateFace(image.Id, 0.10, [1f, 0f], alexId, "Alex")],
                "pipeline-v1",
                cancellationToken));
            var faceId = Assert.Single(
                await faces.GetFacesForImageAsync(image.Id)).Id;
            await faces.SetPersonRepresentativeFaceAsync(
                alexId,
                faceId,
                cancellationToken);
            await faces.RejectPersonForFacesAsync(
                [faceId],
                rejectedPersonId,
                cancellationToken);
            await faces.HideFacesFromSuggestionsAsync(
                [faceId],
                cancellationToken);
            var service = new FaceReviewService(
                faces,
                images,
                new FaceClusteringService(),
                new FaceRecognitionService());

            var operation = await service.ReassignFacesAsync(
                [faceId],
                samId,
                "Sam",
                cancellationToken);

            var reassigned = Assert.Single(
                await faces.GetFacesForImageAsync(image.Id));
            Assert.Equal(samId, reassigned.PersonId);
            Assert.Null(
                Assert.Single(
                    await faces.GetAllPersonsAsync(),
                    person => person.Id == alexId)
                    .RepresentativeFaceRegionId);
            Assert.DoesNotContain(
                faceId,
                await faces.GetHiddenFaceSuggestionIdsAsync(cancellationToken));
            Assert.DoesNotContain(
                faceId,
                (await faces.GetRejectedPersonIdsByFaceIdAsync(cancellationToken)).Keys);

            await service.UndoTagAsync(operation, cancellationToken);

            var restored = Assert.Single(
                await faces.GetFacesForImageAsync(image.Id));
            Assert.Equal(alexId, restored.PersonId);
            Assert.Equal("Alex", restored.PersonName);
            Assert.Equal(
                faceId,
                Assert.Single(
                    await faces.GetAllPersonsAsync(),
                    person => person.Id == alexId)
                    .RepresentativeFaceRegionId);
            Assert.Contains(
                faceId,
                await faces.GetHiddenFaceSuggestionIdsAsync(cancellationToken));
            Assert.Contains(
                rejectedPersonId,
                (await faces.GetRejectedPersonIdsByFaceIdAsync(cancellationToken))[
                    faceId]);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task UndoTag_RemovesNewPersonAndReturnsFaceToReview()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var image = CreateImage(@"C:\Photos\untagged.jpg");
            image.Id = await images.UpsertImageAsync(image);
            var cancellationToken = TestContext.Current.CancellationToken;
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                image.Id,
                image.FileSize,
                image.DateModified,
                [CreateFace(image.Id, 0.10, [1f, 0f])],
                "pipeline-v1",
                cancellationToken));
            var faceId = Assert.Single(
                await faces.GetFacesForImageAsync(image.Id)).Id;
            var service = new FaceReviewService(
                faces,
                images,
                new FaceClusteringService(),
                new FaceRecognitionService());

            var operation = await service.TagAsAsync(
                [faceId],
                null,
                "Alex",
                cancellationToken);
            await service.UndoTagAsync(operation, cancellationToken);

            Assert.Empty(await faces.GetAllPersonsAsync());
            Assert.Null(
                Assert.Single(
                    await faces.GetFacesForImageAsync(image.Id))
                    .PersonId);
            Assert.Contains(
                (await service.GetSuggestionGroupsAsync(cancellationToken))
                    .SelectMany(group => group.Suggestions),
                face => face.FaceRegionId == faceId);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task PersonDropTargets_UseChosenPictureAndSortByAssignedFaceCount()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var firstImage = CreateImage(@"C:\Photos\alex-1.jpg");
            firstImage.Id = await images.UpsertImageAsync(firstImage);
            var secondImage = CreateImage(@"C:\Photos\alex-2.jpg");
            secondImage.Id = await images.UpsertImageAsync(secondImage);
            var otherImage = CreateImage(@"C:\Photos\sam.jpg");
            otherImage.Id = await images.UpsertImageAsync(otherImage);
            var alexId = await faces.CreatePersonAsync("Alex");
            var samId = await faces.CreatePersonAsync("Sam");
            var cancellationToken = TestContext.Current.CancellationToken;

            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                firstImage.Id,
                firstImage.FileSize,
                firstImage.DateModified,
                [CreateFace(firstImage.Id, 0.10, [1f, 0f], alexId, "Alex")],
                "pipeline-v1",
                cancellationToken));
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                secondImage.Id,
                secondImage.FileSize,
                secondImage.DateModified,
                [CreateFace(secondImage.Id, 0.30, [0.9f, 0.1f], alexId, "Alex")],
                "pipeline-v1",
                cancellationToken));
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                otherImage.Id,
                otherImage.FileSize,
                otherImage.DateModified,
                [CreateFace(otherImage.Id, 0.50, [0f, 1f], samId, "Sam")],
                "pipeline-v1",
                cancellationToken));
            var chosenFaceId = Assert.Single(
                await faces.GetFacesForImageAsync(secondImage.Id)).Id;
            var service = new FaceReviewService(
                faces,
                images,
                new FaceClusteringService(),
                new FaceRecognitionService());

            var initialTargets = await service.GetPersonDropTargetsAsync(cancellationToken);

            Assert.Equal([alexId, samId], initialTargets.Select(target => target.Person.Id));
            Assert.NotEqual(
                chosenFaceId,
                Assert.Single(
                    initialTargets,
                    target => target.Person.Id == alexId)
                    .RepresentativeFace!
                    .FaceRegionId);

            await service.SetPersonRepresentativeFaceAsync(
                alexId,
                chosenFaceId,
                cancellationToken);

            var updatedTarget = Assert.Single(
                await service.GetPersonDropTargetsAsync(cancellationToken),
                target => target.Person.Id == alexId);
            Assert.Equal(chosenFaceId, updatedTarget.RepresentativeFace!.FaceRegionId);
            Assert.Equal(
                chosenFaceId,
                Assert.Single(
                    await faces.GetAllPersonsAsync(),
                    person => person.Id == alexId)
                    .RepresentativeFaceRegionId);

            await service.UnassignFacesAsync([chosenFaceId], cancellationToken);

            Assert.Null(
                Assert.Single(
                    await faces.GetAllPersonsAsync(),
                    person => person.Id == alexId)
                    .RepresentativeFaceRegionId);
            var fallbackFaceId = Assert.Single(
                await service.GetPersonDropTargetsAsync(cancellationToken),
                target => target.Person.Id == alexId)
                .RepresentativeFace!
                .FaceRegionId;
            await service.SetPersonRepresentativeFaceAsync(
                alexId,
                fallbackFaceId,
                cancellationToken);
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                firstImage.Id,
                firstImage.FileSize,
                firstImage.DateModified,
                [CreateFace(firstImage.Id, 0.20, [1f, 0f], alexId, "Alex")],
                "pipeline-v2",
                cancellationToken));
            Assert.Null(
                Assert.Single(
                    await faces.GetAllPersonsAsync(),
                    person => person.Id == alexId)
                    .RepresentativeFaceRegionId);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task PersonPicture_MergeInheritsSourceWhenTargetHasNoPicture()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var image = CreateImage(@"C:\Photos\alex.jpg");
            image.Id = await images.UpsertImageAsync(image);
            var sourcePersonId = await faces.CreatePersonAsync("Alex");
            var targetPersonId = await faces.CreatePersonAsync("Alexander");
            var cancellationToken = TestContext.Current.CancellationToken;
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                image.Id,
                image.FileSize,
                image.DateModified,
                [CreateFace(image.Id, 0.10, [1f, 0f], sourcePersonId, "Alex")],
                "pipeline-v1",
                cancellationToken));
            var faceId = Assert.Single(
                await faces.GetFacesForImageAsync(image.Id)).Id;
            await faces.SetPersonRepresentativeFaceAsync(
                sourcePersonId,
                faceId,
                cancellationToken);

            await faces.MergePersonsAsync(
                sourcePersonId,
                targetPersonId,
                cancellationToken);

            Assert.Equal(
                faceId,
                Assert.Single(await faces.GetAllPersonsAsync())
                    .RepresentativeFaceRegionId);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task PeopleManagement_RenamesMergesUnassignsAndDeletesPeople()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var firstImage = CreateImage(@"C:\Photos\first.jpg");
            firstImage.Id = await images.UpsertImageAsync(firstImage);
            var secondImage = CreateImage(@"C:\Photos\second.jpg");
            secondImage.Id = await images.UpsertImageAsync(secondImage);
            var rejectedImage = CreateImage(@"C:\Photos\rejected.jpg");
            rejectedImage.Id = await images.UpsertImageAsync(rejectedImage);
            var sourcePersonId = await faces.CreatePersonAsync("Alex");
            var targetPersonId = await faces.CreatePersonAsync("Alexander");
            var cancellationToken = TestContext.Current.CancellationToken;

            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                firstImage.Id,
                firstImage.FileSize,
                firstImage.DateModified,
                [CreateFace(firstImage.Id, 0.10, [1f, 0f], sourcePersonId, "Alex")],
                "pipeline-v1",
                cancellationToken));
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                secondImage.Id,
                secondImage.FileSize,
                secondImage.DateModified,
                [CreateFace(
                    secondImage.Id,
                    0.30,
                    [0.9f, 0.1f],
                    targetPersonId,
                    "Alexander")],
                "pipeline-v1",
                cancellationToken));
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                rejectedImage.Id,
                rejectedImage.FileSize,
                rejectedImage.DateModified,
                [CreateFace(rejectedImage.Id, 0.50, [0.7f, 0.3f])],
                "pipeline-v1",
                cancellationToken));
            var sourceFaceId = Assert.Single(
                await faces.GetFacesForImageAsync(firstImage.Id)).Id;
            var targetFaceId = Assert.Single(
                await faces.GetFacesForImageAsync(secondImage.Id)).Id;
            var rejectedFaceId = Assert.Single(
                await faces.GetFacesForImageAsync(rejectedImage.Id)).Id;
            await faces.RejectPersonForFacesAsync(
                [sourceFaceId],
                targetPersonId,
                cancellationToken);
            await faces.HideFacesFromSuggestionsAsync(
                [sourceFaceId],
                cancellationToken);
            await faces.RejectPersonForFacesAsync(
                [rejectedFaceId],
                sourcePersonId,
                cancellationToken);
            var service = new FaceReviewService(
                faces,
                images,
                new FaceClusteringService(),
                new FaceRecognitionService());

            await service.SetPersonRepresentativeFaceAsync(
                sourcePersonId,
                sourceFaceId,
                cancellationToken);
            await service.SetPersonRepresentativeFaceAsync(
                targetPersonId,
                targetFaceId,
                cancellationToken);
            await service.RenamePersonAsync(
                targetPersonId,
                "Alexander Smith",
                cancellationToken);
            await service.MergePersonsAsync(
                sourcePersonId,
                targetPersonId,
                cancellationToken);

            var mergedPerson = Assert.Single(await faces.GetAllPersonsAsync());
            Assert.Equal("Alexander Smith", mergedPerson.Name);
            Assert.Equal(2, mergedPerson.FaceCount);
            Assert.Equal(targetFaceId, mergedPerson.RepresentativeFaceRegionId);
            var mergedFace = Assert.Single(
                await faces.GetFacesForImageAsync(firstImage.Id));
            Assert.Equal(targetPersonId, mergedFace.PersonId);
            Assert.Equal("Alexander Smith", mergedFace.PersonName);
            Assert.DoesNotContain(
                sourceFaceId,
                (await faces.GetRejectedPersonIdsByFaceIdAsync(cancellationToken)).Keys);
            Assert.DoesNotContain(
                sourceFaceId,
                await faces.GetHiddenFaceSuggestionIdsAsync(cancellationToken));
            Assert.Contains(
                targetPersonId,
                (await faces.GetRejectedPersonIdsByFaceIdAsync(cancellationToken))[
                    rejectedFaceId]);

            await service.UnassignFacesAsync(
                [sourceFaceId],
                cancellationToken);
            var unassignedFace = Assert.Single(
                await faces.GetFacesForImageAsync(firstImage.Id));
            Assert.Null(unassignedFace.PersonId);
            Assert.Null(unassignedFace.PersonName);

            await service.DeletePersonAsync(targetPersonId, cancellationToken);

            Assert.Empty(await faces.GetAllPersonsAsync());
            var releasedTargetFace = Assert.Single(
                await faces.GetFacesForImageAsync(secondImage.Id));
            Assert.Equal(targetFaceId, releasedTargetFace.Id);
            Assert.Null(releasedTargetFace.PersonId);
            Assert.Null(releasedTargetFace.PersonName);
            var availableFaceIds = (await service.GetSuggestionGroupsAsync(cancellationToken))
                .SelectMany(group => group.Suggestions)
                .Select(face => face.FaceRegionId)
                .ToHashSet();
            Assert.Contains(sourceFaceId, availableFaceIds);
            Assert.Contains(targetFaceId, availableFaceIds);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ReviewWorkflow_PersistsRejectAssignAndHideDecisions()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var knownImage = CreateImage(@"C:\Photos\alex.jpg");
            knownImage.Id = await images.UpsertImageAsync(knownImage);
            var image = CreateImage(@"C:\Photos\portrait.jpg");
            image.Id = await images.UpsertImageAsync(image);
            var secondImage = CreateImage(@"C:\Photos\portrait-2.jpg");
            secondImage.Id = await images.UpsertImageAsync(secondImage);
            var personId = await faces.CreatePersonAsync("Alex");
            var cancellationToken = TestContext.Current.CancellationToken;

            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                knownImage.Id,
                knownImage.FileSize,
                knownImage.DateModified,
                [CreateFace(knownImage.Id, 0.10, [1f, 0f], personId, "Alex")],
                "pipeline-v1",
                cancellationToken));
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                image.Id,
                image.FileSize,
                image.DateModified,
                [
                    CreateFace(image.Id, 0.30, [0.99f, 0.01f]),
                    CreateFace(image.Id, 0.70, [-1f, 0f])
                ],
                "pipeline-v1",
                cancellationToken));
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                secondImage.Id,
                secondImage.FileSize,
                secondImage.DateModified,
                [CreateFace(secondImage.Id, 0.50, [0.98f, 0.02f])],
                "pipeline-v1",
                cancellationToken));

            var stored = await faces.GetFacesForImageAsync(image.Id);
            var rejectedFaceId = stored.Single(face => face.X == 0.30).Id;
            var remainingFaceId = (await faces.GetFacesForImageAsync(secondImage.Id))
                .Single().Id;
            var unrelatedFaceId = stored.Single(face => face.X == 0.70).Id;
            var service = new FaceReviewService(
                faces,
                images,
                new FaceClusteringService(),
                new FaceRecognitionService());

            var knownGroup = Assert.Single(
                await service.GetSuggestionGroupsAsync(cancellationToken),
                group => group.SuggestedPersonId == personId);
            Assert.Equal(2, knownGroup.Suggestions.Count);
            Assert.Equal(1, knownGroup.ConfirmedCount);

            await service.RejectAsync(knownGroup, [rejectedFaceId], cancellationToken);

            var afterReject = await service.GetSuggestionGroupsAsync(cancellationToken);
            var updatedKnownGroup = Assert.Single(
                afterReject,
                group => group.SuggestedPersonId == personId);
            Assert.Equal(remainingFaceId, Assert.Single(updatedKnownGroup.Suggestions).FaceRegionId);
            Assert.Contains(
                afterReject.Where(group => !group.SuggestedPersonId.HasValue),
                group => group.Suggestions.Any(face => face.FaceRegionId == rejectedFaceId));

            await service.TagAsAsync(
                [rejectedFaceId],
                personId,
                "Alex",
                cancellationToken);
            var assigned = (await faces.GetFacesForImageAsync(image.Id))
                .Single(face => face.Id == rejectedFaceId);
            Assert.Equal(personId, assigned.PersonId);
            Assert.DoesNotContain(
                rejectedFaceId,
                (await faces.GetRejectedPersonIdsByFaceIdAsync(cancellationToken)).Keys);

            await service.HidePersonAsync(updatedKnownGroup, cancellationToken);

            var afterHide = await service.GetSuggestionGroupsAsync(cancellationToken);
            Assert.Contains(
                afterHide.Where(group => !group.SuggestedPersonId.HasValue),
                group => group.Suggestions.Any(face => face.FaceRegionId == remainingFaceId));
            Assert.Contains(
                afterHide,
                group => group.Suggestions.Any(face => face.FaceRegionId == unrelatedFaceId));
            var person = Assert.Single(await faces.GetAllPersonsAsync());
            Assert.True(person.SuggestionsHidden);
            Assert.Equal(2, person.FaceCount);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task HideFaces_DoesNotUpdateCacheWhenMetadataWriteFails()
    {
        var databasePath = CreateDatabasePath();

        try
        {
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            var images = new ImageRepository(database);
            var faces = new FaceRepository(database);
            var image = CreateImage();
            image.Id = await images.UpsertImageAsync(image);
            var cancellationToken = TestContext.Current.CancellationToken;
            Assert.True(await faces.TryReplaceFaceRegionsAsync(
                image.Id,
                image.FileSize,
                image.DateModified,
                [CreateFace(image.Id, 0.2, [1f, 0f])],
                "pipeline-v1",
                cancellationToken));
            var faceId = Assert.Single(
                await faces.GetFacesForImageAsync(image.Id)).Id;
            var service = new FaceReviewService(
                faces,
                images,
                new FaceClusteringService(),
                new FaceRecognitionService(),
                new FailingFaceMetadataStore());

            await Assert.ThrowsAsync<IOException>(
                () => service.HideFacesAsync([faceId], cancellationToken));

            Assert.Empty(
                await faces.GetHiddenFaceSuggestionIdsAsync(cancellationToken));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    private static FaceRegion CreateFace(
        long imageId,
        double x,
        float[] embedding,
        long? personId = null,
        string? personName = null) => new()
    {
        ImageId = imageId,
        X = x,
        Y = 0.1,
        Width = 0.1,
        Height = 0.1,
        Embedding = embedding,
        Confidence = 0.9f,
        PersonId = personId,
        PersonName = personName
    };

    private static ImageEntry CreateImage(
        string filePath = @"C:\Photos\portrait.jpg") => new()
    {
        FilePath = filePath,
        FileName = Path.GetFileName(filePath),
        FileSize = 100,
        DateModified = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        DateIndexed = DateTime.UtcNow,
        MediaType = MediaType.Image
    };

    private static string CreateDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-{Guid.NewGuid():N}.db");

    private static void DeleteDatabase(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        DeleteIfPresent(databasePath);
        DeleteIfPresent(databasePath + "-wal");
        DeleteIfPresent(databasePath + "-shm");
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed class FailingFaceMetadataStore : IFaceMetadataStore
    {
        public Task WriteAsync(
            string imagePath,
            PhotoFaceMetadata metadata,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("Metadata write failed."));

        public PhotoFaceMetadata Read(string imagePath) => new(0, 0, []);
    }
}
