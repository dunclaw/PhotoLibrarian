using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class RecognitionPipelineTests
{
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

    private static ImageEntry CreateImage(long id) => new()
    {
        Id = id,
        FilePath = $@"C:\Photos\photo-{id}.jpg",
        FileName = $"photo-{id}.jpg",
        FileSize = 100,
        DateModified = DateTime.UtcNow,
        DateIndexed = DateTime.UtcNow
    };

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
            CancellationToken cancellationToken = default)
        {
            Saved.Add((imageId, faces, scanVersion));
            return Task.FromResult(true);
        }
    }

    private sealed class FakeTagStore(IReadOnlyList<ImageEntry> pending) : IAutoTagStore
    {
        public List<(ImageEntry Image, IReadOnlyCollection<GeneratedImageTag> Tags, string ScanVersion)> Saved { get; } = [];

        public Task<List<ImageEntry>> GetImagesNeedingAutoTagsAsync(
            string scanVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(pending.ToList());

        public Task<bool> TryReplaceAutoTagsAsync(
            ImageEntry expectedImage,
            IReadOnlyCollection<GeneratedImageTag> tags,
            string scanVersion,
            CancellationToken cancellationToken = default)
        {
            Saved.Add((expectedImage, tags, scanVersion));
            return Task.FromResult(true);
        }
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

        public void LoadModel()
        {
        }

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

        public void LoadModel(string profileId, string? modelDirectory)
        {
        }

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
}
