using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.ML.Services;

public sealed class FaceLibraryProcessor
{
    private readonly IFaceScanStore _store;
    private readonly IFaceModelProvider _modelProvider;
    private readonly IFaceDetector _detector;
    private readonly IFaceEmbedder _embedder;

    public FaceLibraryProcessor(
        IFaceScanStore store,
        IFaceModelProvider modelProvider,
        IFaceDetector detector,
        IFaceEmbedder embedder)
    {
        _store = store;
        _modelProvider = modelProvider;
        _detector = detector;
        _embedder = embedder;
    }

    public event EventHandler<FaceProcessingProgressEventArgs>? Progress;

    public async Task<FaceProcessingResult> ProcessLibraryAsync(
        CancellationToken cancellationToken = default)
    {
        Progress?.Invoke(this, FaceProcessingProgressEventArgs.Preparing());
        await _modelProvider.EnsureModelsAsync(cancellationToken);
        _detector.LoadModel();
        _embedder.LoadModel();

        var pending = await _store.GetImagesNeedingFaceScanAsync(
            FaceModelCatalog.PipelineVersion,
            cancellationToken);
        var processed = 0;
        var failed = 0;
        var faceCount = 0;
        Progress?.Invoke(this, new FaceProcessingProgressEventArgs(0, pending.Count, 0, 0));

        try
        {
            foreach (var image in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var detections = await _detector.DetectFacesAsync(
                        image.FilePath,
                        cancellationToken);
                    var embeddings = await _embedder.GenerateEmbeddingsAsync(
                        image.FilePath,
                        detections,
                        cancellationToken);
                    if (detections.Count != embeddings.Count)
                    {
                        throw new InvalidDataException(
                            $"Expected {detections.Count} embeddings but received {embeddings.Count}.");
                    }

                    var regions = detections
                        .Select((face, index) => new FaceRegion
                        {
                            ImageId = image.Id,
                            X = face.X,
                            Y = face.Y,
                            Width = face.Width,
                            Height = face.Height,
                            Confidence = face.Confidence,
                            Embedding = embeddings[index]
                        })
                        .ToArray();
                    var wasSaved = await _store.TryReplaceFaceRegionsAsync(
                        image.Id,
                        image.FileSize,
                        image.DateModified,
                        regions,
                        FaceModelCatalog.PipelineVersion,
                        cancellationToken);
                    if (wasSaved)
                    {
                        faceCount += regions.Length;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failed++;
                    Progress?.Invoke(
                        this,
                        new FaceProcessingProgressEventArgs(
                            processed,
                            pending.Count,
                            faceCount,
                            failed,
                            image.FileName,
                            error: exception.Message));
                }

                processed++;
                Progress?.Invoke(
                    this,
                    new FaceProcessingProgressEventArgs(
                        processed,
                        pending.Count,
                        faceCount,
                        failed,
                        image.FileName));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Progress?.Invoke(
                this,
                new FaceProcessingProgressEventArgs(
                    processed,
                    pending.Count,
                    faceCount,
                    failed,
                    isCanceled: true));
            throw;
        }

        Progress?.Invoke(
            this,
            new FaceProcessingProgressEventArgs(
                processed,
                pending.Count,
                faceCount,
                failed,
                isComplete: true));
        return new FaceProcessingResult(processed, failed, faceCount);
    }
}

public sealed record FaceProcessingResult(int Processed, int Failed, int FacesFound);

public sealed class FaceProcessingProgressEventArgs(
    int processed,
    int total,
    int facesFound,
    int failed,
    string? currentFile = null,
    bool isPreparing = false,
    bool isComplete = false,
    bool isCanceled = false,
    string? error = null) : EventArgs
{
    public int Processed { get; } = processed;
    public int Total { get; } = total;
    public int FacesFound { get; } = facesFound;
    public int Failed { get; } = failed;
    public string? CurrentFile { get; } = currentFile;
    public bool IsPreparing { get; } = isPreparing;
    public bool IsComplete { get; } = isComplete;
    public bool IsCanceled { get; } = isCanceled;
    public string? Error { get; } = error;

    public static FaceProcessingProgressEventArgs Preparing() =>
        new(0, 0, 0, 0, isPreparing: true);
}
