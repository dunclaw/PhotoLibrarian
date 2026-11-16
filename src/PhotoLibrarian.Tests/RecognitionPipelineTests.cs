// Copyright (c) Duncan Lawler. All Rights Reserved.

using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class RecognitionPipelineTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"PhotoLibrarian-recognition-pipeline-{Guid.NewGuid():N}");

    public RecognitionPipelineTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(".raw")]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".tif")]
    public async Task CachedLibrary_RestartAndMetadataEditsDoNotRepeatInference(string extension)
    {
        var path = await CreateLibraryPhotoAsync(extension);
        var detector = new FakeDetector([]);
        var tagger = new FakeTagger([new TagPrediction("beach", 0.9f)]);
        var decoder = new CountingDecoder();
        var request = CreateRequest();

        var first = await RunCachedLibraryAsync(request, detector, tagger, decoder);
        Assert.Equal(1, first.Processed);
        Assert.Equal(0, first.Failed);
        var portable = new MetadataReaderService().ReadPortableTagMetadata(path);
        Assert.NotNull(portable);
        Assert.Equal(portable.SourceIdentity, await RecognitionSourceIdentity.ComputeAsync(path));
        Assert.Equal(0, (await RunCachedLibraryAsync(request, detector, tagger, decoder)).Processed);

        // A fresh cache connection and pipeline instance are used on every pass.
        using (var database = await OpenLibraryAsync())
        {
            var images = new ImageRepository(database);
            var image = (await images.GetByPathAsync(path))!;
            var tags = new TagRepository(database);
            await tags.AddTagAsync(new ImageTag { ImageId = image.Id, Tag = "family", Source = TagSource.Manual });
            var allTags = await tags.GetTagsAsync(image.Id);
            await TagWriterService.WriteTagsToSidecarAsync(path,
                allTags.Select(tag => tag.Tag), allTags, image.AutoTagScanVersion,
                sourceIdentity: image.SourceIdentity);
            await new FaceMetadataStore().WriteAsync(path, new PhotoFaceMetadata(16, 12, []));
            using var scanner = new FolderScannerService();
            var indexer = CreateIndexer(database, scanner);
            Assert.True(await indexer.IndexFileAsync(path));
            Assert.Equal(FaceModelCatalog.PipelineVersion,
                (await images.GetByPathAsync(path))!.FaceScanVersion);
            Assert.Contains(await tags.GetTagsAsync(image.Id), tag => tag.Tag == "family" && tag.Source == TagSource.Manual);
        }

        Assert.Equal(0, (await RunCachedLibraryAsync(request, detector, tagger, decoder)).Processed);
        Assert.Single(detector.CalledPaths);
        Assert.Single(tagger.CalledPaths);
        Assert.Equal(1, decoder.DecodeCount);
        Assert.Equal(1, detector.LoadCount);
        Assert.Equal(1, tagger.LoadCount);
    }

    [Fact]
    public async Task CachedLibrary_ContentEditWithPreservedStatsDoesNotRestoreStalePortableCompletion()
    {
        var path = await CreateLibraryPhotoAsync(".raw");
        var detector = new FakeDetector([]);
        var tagger = new FakeTagger([]);
        var decoder = new CountingDecoder();
        var request = CreateRequest();
        await RunCachedLibraryAsync(request, detector, tagger, decoder);
        var timestamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllBytesAsync(path, [9, 8, 7]);
        File.SetLastWriteTimeUtc(path, timestamp);

        using (var database = await OpenLibraryAsync())
        {
            using var scanner = new FolderScannerService();
            Assert.True(await CreateIndexer(database, scanner).IndexFileAsync(path));
            var image = (await new ImageRepository(database).GetByPathAsync(path))!;
            Assert.Null(image.AutoTagScanVersion);
            Assert.Null(image.FaceScanVersion);
        }

        Assert.Equal(1, (await RunCachedLibraryAsync(request, detector, tagger, decoder)).Processed);
        Assert.Equal(2, detector.CalledPaths.Count);
        Assert.Equal(2, tagger.CalledPaths.Count);
    }

    [Fact]
    public async Task CachedLibrary_ChangedPixelsWithOldPortableMetadataDoNotRestoreCompletion()
    {
        var path = await CreateLibraryPhotoAsync(".png");
        var detector = new FakeDetector([]);
        var tagger = new FakeTagger([]);
        var decoder = new CountingDecoder();
        var request = CreateRequest();
        await RunCachedLibraryAsync(request, detector, tagger, decoder);
        var portable = new MetadataReaderService().ReadPortableTagMetadata(path)!;

        var changedPath = Path.Combine(_directory, "changed-photo.png");
        await WicTestImage.CreateAsync(changedPath, 16, 12, false, (_, _) => (10, 20, 30));
        await TagWriterService.WriteTagsToSidecarAsync(changedPath, [], [], portable.AutoTagScanVersion,
            sourceIdentity: portable.SourceIdentity);
        Assert.Equal(1, (await RunCachedLibraryAsync(request, detector, tagger, decoder)).Processed);
        Assert.Equal(2, detector.CalledPaths.Count);
        Assert.Equal(2, tagger.CalledPaths.Count);
    }

    [Fact]
    public async Task CachedLibrary_OrientationChangeInvalidatesRecognitionInput()
    {
        var path = await CreateLibraryPhotoAsync(".jpg");
        var detector = new FakeDetector([]);
        var tagger = new FakeTagger([]);
        var decoder = new CountingDecoder();
        await RunCachedLibraryAsync(CreateRequest(), detector, tagger, decoder);
        await EmbeddedMetadataWriter.WriteAsync(path, orientation: 6);
        Assert.Equal(1, (await RunCachedLibraryAsync(CreateRequest(), detector, tagger, decoder)).Processed);
        Assert.Equal(2, detector.CalledPaths.Count);
        Assert.Equal(2, tagger.CalledPaths.Count);
    }

    [Fact]
    public async Task CachedLibrary_SettingsAndPipelineChangesOnlyRequeueTheirOwnStage()
    {
        await CreateLibraryPhotoAsync(".raw");
        var detector = new FakeDetector([]);
        var tagger = new FakeTagger([]);
        var decoder = new CountingDecoder();
        var request = CreateRequest();
        await RunCachedLibraryAsync(request, detector, tagger, decoder);
        var changedSettings = request with
        {
            AutoTaggingSettings = request.AutoTaggingSettings! with { MaximumTags = 2 }
        };
        Assert.Equal(1, (await RunCachedLibraryAsync(changedSettings, detector, tagger, decoder)).Processed);
        Assert.Equal(0, (await RunCachedLibraryAsync(changedSettings, detector, tagger, decoder)).Processed);
        Assert.Single(detector.CalledPaths);
        Assert.Equal(2, tagger.CalledPaths.Count);

        using (var database = await OpenLibraryAsync())
        {
            using var connection = database.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE images SET face_scan_version = 'old-pipeline'";
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(1, (await RunCachedLibraryAsync(changedSettings, detector, tagger, decoder)).Processed);
        Assert.Equal(2, detector.CalledPaths.Count);
        Assert.Equal(2, tagger.CalledPaths.Count);

        changedSettings = changedSettings with
        {
            AutoTaggingSettings = changedSettings.AutoTaggingSettings! with { ConfidenceThreshold = 0.3f }
        };
        Assert.Equal(1, (await RunCachedLibraryAsync(changedSettings, detector, tagger, decoder)).Processed);
        Assert.Equal(0, (await RunCachedLibraryAsync(changedSettings, detector, tagger, decoder)).Processed);
        Assert.Equal(2, detector.CalledPaths.Count);
        Assert.Equal(3, tagger.CalledPaths.Count);

        var changedModel = changedSettings with
        {
            AutoTaggingSettings = CreateApprovedSettings(AutoTagModelCatalog.EfficientNetLite4ProfileId)
        };
        Assert.Equal(1, (await RunCachedLibraryAsync(changedModel, detector, tagger, decoder)).Processed);
        Assert.Equal(2, detector.CalledPaths.Count);
        Assert.Equal(4, tagger.CalledPaths.Count);
    }

    [Fact]
    public async Task CachedLibrary_CanceledTaggingRetriesWithoutRepeatingCompletedFaces()
    {
        await CreateLibraryPhotoAsync(".raw");
        using var cancellation = new CancellationTokenSource();
        var detector = new FakeDetector([]);
        var decoder = new CountingDecoder();
        var request = CreateRequest();
        var canceledTagger = new FakeTagger([], onCall: _ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunCachedLibraryAsync(request, detector, canceledTagger, decoder, cancellation.Token));

        var tagger = new FakeTagger([]);
        Assert.Equal(1, (await RunCachedLibraryAsync(request, detector, tagger, decoder)).Processed);
        Assert.Single(detector.CalledPaths);
        Assert.Single(tagger.CalledPaths);
        Assert.Equal(0, (await RunCachedLibraryAsync(request, detector, tagger, decoder)).Processed);
    }

    [Fact]
    public async Task CachedLibrary_MetadataFailureAndOfflineSourceRemainRetryable()
    {
        var path = await CreateLibraryPhotoAsync(".raw");
        var sidecar = Path.ChangeExtension(path, ".xmp");
        Directory.CreateDirectory(sidecar);
        var detector = new FakeDetector([]);
        var tagger = new FakeTagger([]);
        var decoder = new CountingDecoder();
        var request = new RecognitionRequest(false, CreateApprovedSettings());
        Assert.Equal(1, (await RunCachedLibraryAsync(request, detector, tagger, decoder)).Failed);
        Directory.Delete(sidecar);

        File.Move(path, path + ".offline");
        Assert.Equal(1, (await RunCachedLibraryAsync(request, detector, tagger, decoder)).Failed);
        File.Move(path + ".offline", path);
        Assert.Equal(1, (await RunCachedLibraryAsync(request, detector, tagger, decoder)).Processed);
        Assert.Equal(0, (await RunCachedLibraryAsync(request, detector, tagger, decoder)).Processed);
        Assert.Equal(2, tagger.CalledPaths.Count);
        Assert.Empty(detector.CalledPaths);

        var loads = tagger.LoadCount;
        File.Move(path, path + ".offline");
        Assert.Equal(0, (await RunCachedLibraryAsync(request, detector, tagger, decoder)).Processed);
        Assert.Equal(loads, tagger.LoadCount);
        File.Move(path + ".offline", path);
    }

    [Fact]
    public async Task CachedLibrary_CommitsRejectNewSourceIdentityWithUnchangedStats()
    {
        var path = await CreateLibraryPhotoAsync(".raw");
        using var database = await OpenLibraryAsync();
        using var scanner = new FolderScannerService();
        await CreateIndexer(database, scanner).IndexFileAsync(path);
        var images = new ImageRepository(database);
        var snapshot = (await images.GetByPathAsync(path))!;
        var updated = (await images.GetByPathAsync(path))!;
        updated.SourceIdentity = "new-source-identity";
        await images.UpsertImageAsync(updated);
        var faces = new FaceRepository(database);
        var tags = new TagRepository(database);

        Assert.False(await faces.TryReplaceFaceRegionsAsync(snapshot.Id,
            snapshot.FileSize, snapshot.DateModified, [], FaceModelCatalog.PipelineVersion,
            expectedSourceIdentity: snapshot.SourceIdentity));
        Assert.False(await tags.TryReplaceAutoTagsIfUnchangedAsync(snapshot, [], [], null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tags.SetAutoTagScanVersionAsync(snapshot.Id, "current",
                expectedSourceIdentity: snapshot.SourceIdentity));
    }

    [Fact]
    public async Task CachedLibrary_DisabledAndUnapprovedRecognitionDoNotRunModels()
    {
        await CreateLibraryPhotoAsync(".raw");
        var detector = new FakeDetector([]);
        var tagger = new FakeTagger([]);
        var decoder = new CountingDecoder();
        Assert.Equal(0, (await RunCachedLibraryAsync(
            new RecognitionRequest(false, new AutoTaggingSettings(true)),
            detector, tagger, decoder)).Processed);
        Assert.Equal(0, decoder.DecodeCount);
        Assert.Empty(detector.CalledPaths);
        Assert.Empty(tagger.CalledPaths);
        Assert.Equal(0, detector.LoadCount);
        Assert.Equal(0, tagger.LoadCount);
        Assert.Equal(1, (await RunCachedLibraryAsync(
            CreateRequest(), detector, tagger, decoder)).Processed);
    }

    [Fact]
    public async Task CachedLibrary_UnchangedScopedStartupSkipsMetadataAndModelLoading()
    {
        var included = Path.Combine(_directory, "included");
        var excluded = Path.Combine(_directory, "excluded");
        Directory.CreateDirectory(included);
        Directory.CreateDirectory(excluded);
        for (var index = 0; index < 100; index++)
            await WicTestImage.CreateAsync(Path.Combine(included, $"{index}.png"), 16, 12);
        await File.WriteAllBytesAsync(Path.Combine(excluded, "outside.dng"), [1, 2, 3]);

        var detector = new FakeDetector([]);
        var tagger = new FakeTagger([]);
        var decoder = new CountingDecoder();
        var request = CreateRequest() with
        {
            WatchedFolders = [new WatchedFolder { Path = included, IncludeSubfolders = false }]
        };
        var first = await RunCachedLibraryAsync(request, detector, tagger, decoder);
        Assert.Equal(100, first.Processed);
        using (var database = await OpenLibraryAsync())
        {
            using var scanner = new FolderScannerService();
            // Reconcile completion's metadata writes before measuring the stable restart.
            await CreateIndexer(database, scanner).IndexFolderAsync(included, false);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using (var database = await OpenLibraryAsync())
        {
            using var scanner = new FolderScannerService();
            var indexer = CreateIndexer(database, scanner);
            IndexingProgressEventArgs? completion = null;
            indexer.Progress += (_, progress) => completion = progress;
            await indexer.IndexFolderAsync(included, false);
            Assert.NotNull(completion);
            Assert.Equal(0, completion.Processed);
            Assert.Equal(100, completion.Skipped);
            var pipeline = CreatePipeline(new FaceRepository(database), new TagRepository(database),
                detector, new FakeEmbedder([]), tagger, decoder, new CountingActivityGate());
            pipeline.Progress += (_, progress) =>
            {
                if (progress.Error is not null)
                    TestContext.Current.TestOutputHelper?.WriteLine(progress.Error);
            };
            Assert.Equal(0, (await pipeline.ProcessLibraryAsync(request)).Processed);
        }
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Synthetic stable scoped startup (100 x 16x12 PNG): {watch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(100, decoder.DecodeCount);
        Assert.Equal(1, detector.LoadCount);
        Assert.Equal(1, tagger.LoadCount);
    }

    private async Task<string> CreateLibraryPhotoAsync(string extension)
    {
        var path = Path.Combine(_directory, "cached-photo" + (extension == ".raw" ? ".dng" : extension));
        if (extension == ".raw")
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
        else
            await WicTestImage.CreateAsync(path, 16, 12, jpeg: extension == ".jpg");
        return path;
    }

    private async Task<CacheDatabase> OpenLibraryAsync()
    {
        var database = new CacheDatabase(Path.Combine(_directory, "library.db"));
        await database.InitializeAsync();
        return database;
    }

    private static LibraryIndexingService CreateIndexer(CacheDatabase database, FolderScannerService scanner) =>
        new(database, new ImageRepository(database), new TagRepository(database),
            new FaceRepository(database), scanner, new MetadataReaderService(), new FaceMetadataStore());

    private async Task<RecognitionResult> RunCachedLibraryAsync(
        RecognitionRequest request,
        FakeDetector detector,
        FakeTagger tagger,
        CountingDecoder decoder,
        CancellationToken cancellationToken = default)
    {
        using var database = await OpenLibraryAsync();
        using var scanner = new FolderScannerService();
        await CreateIndexer(database, scanner).IndexFolderAsync(_directory);
        var pipeline = CreatePipeline(new FaceRepository(database), new TagRepository(database),
            detector, new FakeEmbedder([]), tagger, decoder, new CountingActivityGate());
        pipeline.Progress += (_, progress) =>
        {
            if (progress.Error is not null)
                TestContext.Current.TestOutputHelper?.WriteLine(progress.Error);
        };
        return await pipeline.ProcessLibraryAsync(request, cancellationToken);
    }

    [Fact]
    public async Task ProcessLibraryAsync_SkipsPhotosOutsideRemainingWatchRootsForBothStages()
    {
        var image = CreateImage(1);
        var faceStore = new FakeFaceStore([image]);
        var tagStore = new FakeTagStore([image]);
        var decoder = new CountingDecoder();
        var pipeline = CreatePipeline(faceStore, tagStore, new FakeDetector([]),
            new FakeEmbedder([]), new FakeTagger([]), decoder, new CountingActivityGate());

        var result = await pipeline.ProcessLibraryAsync(
            CreateRequest() with { WatchedFolders = [] },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, decoder.DecodeCount);
        Assert.Equal(0, result.Processed);
        Assert.Empty(faceStore.Saved);
        Assert.Empty(tagStore.Saved);
    }

    [Fact]
    public async Task ProcessLibraryAsync_DecodesEachPhotoOnceAndSharesItAcrossStages()
    {
        var image = CreateImage(1);
        var faceStore = new FakeFaceStore([image]);
        var tagStore = new FakeTagStore([image]);
        var detector = new FakeDetector([CreateFace()]);
        var tagger = new FakeTagger([new TagPrediction("beach", 0.9f)]);
        var decoder = new CountingDecoder();
        var pipeline = CreatePipeline(
            faceStore,
            tagStore,
            detector,
            new FakeEmbedder([[0.6f, 0.8f]]),
            tagger,
            decoder,
            new CountingActivityGate());

        var result = await pipeline.ProcessLibraryAsync(
            CreateRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, decoder.DecodeCount);
        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Failed);
        Assert.Single(faceStore.Saved);
        Assert.Single(tagStore.Saved);
        // Both stages ran against the very same decode.
        Assert.NotNull(detector.LastImage);
        Assert.Same(detector.LastImage, tagger.LastImage);
    }

    [Fact]
    public async Task ProcessLibraryAsync_TagsEachPhotoBeforeMovingToTheNextPhoto()
    {
        var images = new List<ImageEntry> { CreateImage(1), CreateImage(2) };
        var faceStore = new FakeFaceStore(images);
        var tagStore = new FakeTagStore(images);
        var order = new List<string>();
        var detector = new FakeDetector(
            [CreateFace()],
            onCall: path => order.Add($"face:{path}"));
        var tagger = new FakeTagger(
            [new TagPrediction("beach", 0.9f)],
            onCall: path => order.Add($"tag:{path}"));
        var pipeline = CreatePipeline(
            faceStore,
            tagStore,
            detector,
            new FakeEmbedder([[0.6f, 0.8f]]),
            tagger,
            new CountingDecoder(),
            new CountingActivityGate());

        await pipeline.ProcessLibraryAsync(
            CreateRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                $"face:{images[0].FilePath}",
                $"tag:{images[0].FilePath}",
                $"face:{images[1].FilePath}",
                $"tag:{images[1].FilePath}"
            ],
            order);
    }

    [Fact]
    public async Task ProcessLibraryAsync_TagsOnlyPhotoSkipsTheFullResolutionDecode()
    {
        var image = CreateImage(1);
        var faceStore = new FakeFaceStore([]);
        var tagStore = new FakeTagStore([image]);
        var decoder = new CountingDecoder();
        var pipeline = CreatePipeline(
            faceStore,
            tagStore,
            new FakeDetector([]),
            new FakeEmbedder([]),
            new FakeTagger([new TagPrediction("beach", 0.9f)]),
            decoder,
            new CountingActivityGate());

        await pipeline.ProcessLibraryAsync(
            CreateRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal([false], decoder.SharedPixelRequests);
        Assert.Single(tagStore.Saved);
        Assert.Empty(faceStore.Saved);
    }

    [Fact]
    public async Task ProcessLibraryAsync_DoesNotRerunTheStageThatIsAlreadyCurrent()
    {
        var faceImage = CreateImage(1);
        var tagImage = CreateImage(2);
        var faceStore = new FakeFaceStore([faceImage]);
        var tagStore = new FakeTagStore([tagImage]);
        var detector = new FakeDetector([CreateFace()]);
        var tagger = new FakeTagger([new TagPrediction("beach", 0.9f)]);
        var pipeline = CreatePipeline(
            faceStore,
            tagStore,
            detector,
            new FakeEmbedder([[0.6f, 0.8f]]),
            tagger,
            new CountingDecoder(),
            new CountingActivityGate());

        var result = await pipeline.ProcessLibraryAsync(
            CreateRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Processed);
        Assert.Equal([faceImage.FilePath], detector.CalledPaths);
        Assert.Equal([tagImage.FilePath], tagger.CalledPaths);
    }

    [Fact]
    public async Task ProcessLibraryAsync_WaitsForTheIdleGateOncePerPhoto()
    {
        var images = new List<ImageEntry> { CreateImage(1), CreateImage(2) };
        var gate = new CountingActivityGate();
        var pipeline = CreatePipeline(
            new FakeFaceStore(images),
            new FakeTagStore(images),
            new FakeDetector([CreateFace()]),
            new FakeEmbedder([[0.6f, 0.8f]]),
            new FakeTagger([new TagPrediction("beach", 0.9f)]),
            new CountingDecoder(),
            gate);

        await pipeline.ProcessLibraryAsync(
            CreateRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, gate.WaitCount);
    }

    [Fact]
    public async Task ProcessLibraryAsync_CancellationStopsBothStagesAndReportsPaused()
    {
        using var cancellation = new CancellationTokenSource();
        var images = new List<ImageEntry> { CreateImage(1), CreateImage(2) };
        var tagger = new FakeTagger([new TagPrediction("beach", 0.9f)]);
        var pipeline = CreatePipeline(
            new FakeFaceStore(images),
            new FakeTagStore(images),
            new CancelingDetector(cancellation),
            new FakeEmbedder([]),
            tagger,
            new CountingDecoder(),
            new CountingActivityGate());
        RecognitionProgressEventArgs? canceled = null;
        pipeline.Progress += (_, progress) =>
        {
            if (progress.IsCanceled)
            {
                canceled = progress;
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.ProcessLibraryAsync(
                CreateRequest(),
                cancellation.Token));

        Assert.NotNull(canceled);
        Assert.Equal(0, canceled.Processed);
        Assert.Empty(tagger.CalledPaths);
    }

    [Fact]
    public async Task ProcessLibraryAsync_PhotoFailureInOneStageStillRunsTheOtherStage()
    {
        var images = new List<ImageEntry> { CreateImage(1), CreateImage(2) };
        var faceStore = new FakeFaceStore(images);
        var tagStore = new FakeTagStore(images);
        var detector = new FakeDetector(
            [CreateFace()],
            failurePath: images[0].FilePath);
        var tagger = new FakeTagger([new TagPrediction("beach", 0.9f)]);
        var pipeline = CreatePipeline(
            faceStore,
            tagStore,
            detector,
            new FakeEmbedder([[0.6f, 0.8f]]),
            tagger,
            new CountingDecoder(),
            new CountingActivityGate());
        var errors = new List<string>();
        pipeline.Progress += (_, progress) =>
        {
            if (!string.IsNullOrEmpty(progress.Error))
            {
                errors.Add(progress.Error);
            }
        };

        var result = await pipeline.ProcessLibraryAsync(
            CreateRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Processed);
        Assert.Equal(1, result.Failed);
        Assert.Single(errors);
        // The failed face stage did not stop tagging for the same photo,
        // and the next photo still ran both stages.
        Assert.Equal(2, tagStore.Saved.Count);
        Assert.Single(faceStore.Saved);
    }

    [Fact]
    public async Task ProcessLibraryAsync_ReportsCombinedProgressAndActiveStage()
    {
        var image = CreateImage(1);
        var pipeline = CreatePipeline(
            new FakeFaceStore([image]),
            new FakeTagStore([image]),
            new FakeDetector([CreateFace()]),
            new FakeEmbedder([[0.6f, 0.8f]]),
            new FakeTagger([new TagPrediction("beach", 0.9f)]),
            new CountingDecoder(),
            new CountingActivityGate());
        var updates = new List<RecognitionProgressEventArgs>();
        pipeline.Progress += (_, progress) => updates.Add(progress);

        await pipeline.ProcessLibraryAsync(
            CreateRequest(),
            TestContext.Current.CancellationToken);

        var perPhoto = updates.Single(
            update => update.CurrentFile == image.FileName);
        Assert.Equal(RecognitionStage.AutoTagging, perPhoto.Stage);
        Assert.Equal(1, perPhoto.Processed);
        Assert.Equal(1, perPhoto.Total);
        Assert.Equal(1, perPhoto.FacesFound);
        Assert.Equal(1, perPhoto.TagsAdded);

        var completion = updates.Single(update => update.IsComplete);
        Assert.Equal(RecognitionStage.None, completion.Stage);
        Assert.Equal(1, completion.FacesFound);
        Assert.Equal(1, completion.TagsAdded);
    }

    private static RecognitionPipeline CreatePipeline(
        IFaceScanStore faceStore,
        IAutoTagStore tagStore,
        IFaceDetector detector,
        IFaceEmbedder embedder,
        IAutoTagger tagger,
        IImageDecoder decoder,
        IBackgroundActivityGate gate) =>
        new(
            faceStore,
            tagStore,
            new FakeFaceModelProvider(),
            detector,
            embedder,
            new FakeAutoTagModelProvider(),
            tagger,
            gate,
            decoder);

    private static RecognitionRequest CreateRequest() =>
        new(true, CreateApprovedSettings());

    private static AutoTaggingSettings CreateApprovedSettings(
        string profileId = AutoTagModelCatalog.MobileNetV2ProfileId)
    {
        var definition = AutoTagModelCatalog.ForId(profileId);
        return new AutoTaggingSettings(
            true,
            profileId,
            definition.DefaultMaximumTags,
            definition.DefaultConfidenceThreshold,
            null,
            new Dictionary<string, AutoTagQualityStatus>
            {
                [definition.ApprovalKey] = AutoTagQualityStatus.Approved
            });
    }

    private ImageEntry CreateImage(long id)
    {
        var path = Path.Combine(_directory, $"photo-{id}.raw");
        File.WriteAllBytes(path, [1, 2, 3]);
        var file = new FileInfo(path);
        return new ImageEntry
        {
            Id = id,
            FilePath = path,
            FileName = file.Name,
            FileSize = file.Length,
            DateModified = file.LastWriteTimeUtc,
            DateIndexed = DateTime.UtcNow,
            MediaType = MediaType.Image
        };
    }

    private static DetectedFace CreateFace() => new()
    {
        X = 0.1f,
        Y = 0.2f,
        Width = 0.3f,
        Height = 0.4f,
        Confidence = 0.9f,
        Landmarks =
        [
            new(0.2f, 0.3f),
            new(0.3f, 0.3f),
            new(0.25f, 0.4f),
            new(0.21f, 0.5f),
            new(0.29f, 0.5f)
        ]
    };

    private sealed class CountingDecoder : IImageDecoder
    {
        public int DecodeCount { get; private set; }
        public List<bool> SharedPixelRequests { get; } = [];

        public Task<DecodedImage> DecodeAsync(
            string filePath,
            bool requiresSharedPixels,
            CancellationToken cancellationToken = default)
        {
            DecodeCount++;
            SharedPixelRequests.Add(requiresSharedPixels);
            return Task.FromResult(DecodedImage.WithoutPixels(filePath));
        }
    }

    private sealed class FakeFaceStore(IReadOnlyList<ImageEntry> pending) : IFaceScanStore
    {
        public List<(long ImageId, IReadOnlyCollection<FaceRegion> Faces, string ScanVersion)> Saved { get; } = [];

        public Task<List<ImageEntry>> GetImagesNeedingFaceScanAsync(
            string scanVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(pending.ToList());

        public Task<List<FaceRegion>> GetFacesForImageAsync(long imageId) =>
            Task.FromResult<List<FaceRegion>>([]);

        public Task<bool> TryReplaceFaceRegionsAsync(
            long imageId,
            long expectedFileSize,
            DateTime expectedDateModified,
            IReadOnlyCollection<FaceRegion> faces,
            string scanVersion,
            CancellationToken cancellationToken = default,
            string? expectedSourceIdentity = null)
        {
            Saved.Add((imageId, faces, scanVersion));
            return Task.FromResult(true);
        }
    }

    private sealed class FakeTagStore(IReadOnlyList<ImageEntry> pending) : IAutoTagStore
    {
        public List<(
            ImageEntry Image,
            IReadOnlyCollection<GeneratedImageTag> Tags,
            string? ScanVersion)> Saved { get; } = [];

        public Task<List<ImageTag>> GetTagsAsync(
            long imageId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<ImageTag>());

        public Task<List<ImageEntry>> GetImagesNeedingAutoTagsAsync(
            string scanVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(pending.ToList());

        public Task<bool> TryReplaceAutoTagsIfUnchangedAsync(
            ImageEntry expectedImage,
            IReadOnlyCollection<GeneratedImageTag> tags,
            IReadOnlyCollection<ImageTag> expectedAutoTags,
            string? expectedScanVersion,
            CancellationToken cancellationToken = default)
        {
            Saved.Add((expectedImage, tags, null));
            return Task.FromResult(true);
        }

        public Task<bool> TryRestoreAutoTagsAsync(
            ImageEntry expectedImage,
            IReadOnlyCollection<ImageTag> expectedAutoTags,
            IReadOnlyCollection<ImageTag> previousAutoTags,
            string? previousScanVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task SetAutoTagScanVersionAsync(
            long imageId,
            string scanVersion,
            CancellationToken cancellationToken = default,
            string? expectedSourceIdentity = null) =>
            Task.CompletedTask;
    }

    private sealed class FakeFaceModelProvider : IFaceModelProvider
    {
        public Task EnsureModelsAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeAutoTagModelProvider : IAutoTagModelProvider
    {
        public Task EnsureModelAsync(
            string profileId,
            string? modelDirectory,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public string GetAssetPath(
            string profileId,
            AutoTagAssetDefinition asset,
            string? modelDirectory) =>
            Path.Combine(
                modelDirectory ?? Path.GetTempPath(),
                profileId,
                asset.FileName);
    }

    private sealed class FakeDetector(
        IReadOnlyList<DetectedFace> faces,
        string? failurePath = null,
        Action<string>? onCall = null) : IFaceDetector
    {
        public List<string> CalledPaths { get; } = [];
        public DecodedImage? LastImage { get; private set; }
        public int LoadCount { get; private set; }

        public void LoadModel() => LoadCount++;

        public Task<IReadOnlyList<DetectedFace>> DetectFacesAsync(
            string imagePath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The pipeline must pass the shared decode, not a file path.");

        public Task<IReadOnlyList<DetectedFace>> DetectFacesAsync(
            DecodedImage image,
            CancellationToken cancellationToken = default)
        {
            LastImage = image;
            CalledPaths.Add(image.FilePath);
            onCall?.Invoke(image.FilePath);
            if (image.FilePath == failurePath)
            {
                throw new InvalidDataException("Unreadable image");
            }

            return Task.FromResult(faces);
        }
    }

    private sealed class CancelingDetector(CancellationTokenSource cancellation) : IFaceDetector
    {
        public void LoadModel()
        {
        }

        public Task<IReadOnlyList<DetectedFace>> DetectFacesAsync(
            string imagePath,
            CancellationToken cancellationToken = default) =>
            DetectFacesAsync(DecodedImage.WithoutPixels(imagePath), cancellationToken);

        public Task<IReadOnlyList<DetectedFace>> DetectFacesAsync(
            DecodedImage image,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<DetectedFace>>([]);
        }
    }

    private sealed class FakeEmbedder(IReadOnlyList<float[]> embeddings) : IFaceEmbedder
    {
        public void LoadModel()
        {
        }

        public Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
            string imagePath,
            IReadOnlyList<DetectedFace> faces,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(embeddings);

        public Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
            DecodedImage image,
            IReadOnlyList<DetectedFace> faces,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(embeddings);
    }

    private sealed class FakeTagger(
        IReadOnlyList<TagPrediction> predictions,
        Action<string>? onCall = null) : IAutoTagger
    {
        public List<string> CalledPaths { get; } = [];
        public DecodedImage? LastImage { get; private set; }
        public int LoadCount { get; private set; }

        public void LoadModel(string profileId, string? modelDirectory) => LoadCount++;

        public Task<IReadOnlyList<TagPrediction>> PredictTagsAsync(
            string imagePath,
            string profileId,
            string? modelDirectory,
            int maximumTags,
            float confidenceThreshold,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The pipeline must pass the shared decode, not a file path.");

        public Task<IReadOnlyList<TagPrediction>> PredictTagsAsync(
            DecodedImage image,
            string profileId,
            string? modelDirectory,
            int maximumTags,
            float confidenceThreshold,
            CancellationToken cancellationToken = default)
        {
            LastImage = image;
            CalledPaths.Add(image.FilePath);
            onCall?.Invoke(image.FilePath);
            return Task.FromResult(predictions);
        }
    }

    private sealed class CountingActivityGate : IBackgroundActivityGate
    {
        public int WaitCount { get; private set; }

        public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
        {
            WaitCount++;
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
