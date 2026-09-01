using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class FaceLibraryProcessorTests
{
    [Fact]
    public async Task ProcessLibraryAsync_StoresDetectedFacesAndReportsCompletion()
    {
        var store = new FakeStore([CreateImage(1)]);
        var modelProvider = new FakeModelProvider();
        var detector = new FakeDetector([CreateFace()]);
        var embedder = new FakeEmbedder([[0.6f, 0.8f]]);
        var processor = new FaceLibraryProcessor(store, modelProvider, detector, embedder);
        FaceProcessingProgressEventArgs? completion = null;
        processor.Progress += (_, progress) =>
        {
            if (progress.IsComplete)
            {
                completion = progress;
            }
        };

        var result = await processor.ProcessLibraryAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new FaceProcessingResult(1, 0, 1), result);
        Assert.True(modelProvider.WasCalled);
        Assert.True(detector.WasLoaded);
        Assert.True(embedder.WasLoaded);
        var saved = Assert.Single(store.Saved);
        Assert.Equal(FaceModelCatalog.PipelineVersion, saved.ScanVersion);
        var storedEmbedding = Assert.Single(saved.Faces).Embedding;
        Assert.NotNull(storedEmbedding);
        Assert.Equal([0.6f, 0.8f], storedEmbedding);
        Assert.NotNull(completion);
        Assert.Equal(1, completion.FacesFound);
    }

    [Fact]
    public async Task ProcessLibraryAsync_CancellationLeavesCurrentImagePending()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeStore([CreateImage(1), CreateImage(2)]);
        var processor = new FaceLibraryProcessor(
            store,
            new FakeModelProvider(),
            new CancelingDetector(cancellation),
            new FakeEmbedder([]));
        FaceProcessingProgressEventArgs? canceled = null;
        processor.Progress += (_, progress) =>
        {
            if (progress.IsCanceled)
            {
                canceled = progress;
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processor.ProcessLibraryAsync(cancellation.Token));

        Assert.Empty(store.Saved);
        Assert.NotNull(canceled);
        Assert.Equal(0, canceled.Processed);
    }

    private static ImageEntry CreateImage(long id) => new()
    {
        Id = id,
        FilePath = $@"C:\Photos\portrait-{id}.jpg",
        FileName = $"portrait-{id}.jpg",
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

    private sealed class FakeStore(List<ImageEntry> pending) : IFaceScanStore
    {
        public List<(long ImageId, IReadOnlyCollection<FaceRegion> Faces, string ScanVersion)> Saved { get; } = [];

        public Task<List<ImageEntry>> GetImagesNeedingFaceScanAsync(
            string scanVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(pending);

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

    private sealed class FakeModelProvider : IFaceModelProvider
    {
        public bool WasCalled { get; private set; }

        public Task EnsureModelsAsync(CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDetector(IReadOnlyList<DetectedFace> faces) : IFaceDetector
    {
        public bool WasLoaded { get; private set; }

        public void LoadModel() => WasLoaded = true;

        public Task<IReadOnlyList<DetectedFace>> DetectFacesAsync(
            string imagePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(faces);
    }

    private sealed class CancelingDetector(CancellationTokenSource cancellation) : IFaceDetector
    {
        public void LoadModel()
        {
        }

        public Task<IReadOnlyList<DetectedFace>> DetectFacesAsync(
            string imagePath,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<DetectedFace>>([]);
        }
    }

    private sealed class FakeEmbedder(IReadOnlyList<float[]> embeddings) : IFaceEmbedder
    {
        public bool WasLoaded { get; private set; }

        public void LoadModel() => WasLoaded = true;

        public Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
            string imagePath,
            IReadOnlyList<DetectedFace> faces,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(embeddings);
    }
}
