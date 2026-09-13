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
                string? error = null;
                try
                {
                    var metadataFaces = (await _store.GetFacesForImageAsync(
                            image.Id))
                        .Where(face => face.IsMetadataManaged)
                        .ToList();
                    var detections = await _detector.DetectFacesAsync(
                        image.FilePath,
                        cancellationToken);
                    var newDetections = detections
                        .Where(detection => metadataFaces.All(
                            face => IntersectionOverUnion(face, detection) < 0.5))
                        .ToList();
                    var embeddingInputs = metadataFaces
                        .Select(ToDetectedFace)
                        .Concat(newDetections)
                        .ToList();
                    var embeddings = await _embedder.GenerateEmbeddingsAsync(
                        image.FilePath,
                        embeddingInputs,
                        cancellationToken);
                    if (embeddingInputs.Count != embeddings.Count)
                    {
                        throw new InvalidDataException(
                            $"Expected {embeddingInputs.Count} embeddings but received {embeddings.Count}.");
                    }

                    var regions = metadataFaces
                        .Select((face, index) => new FaceRegion
                        {
                            ImageId = image.Id,
                            X = face.X,
                            Y = face.Y,
                            Width = face.Width,
                            Height = face.Height,
                            Confidence = face.Confidence,
                            Embedding = embeddings[index],
                            PersonId = face.PersonId,
                            PersonName = face.PersonName,
                            IsMetadataManaged = true
                        })
                        .Concat(newDetections.Select((face, index) =>
                            new FaceRegion
                            {
                                ImageId = image.Id,
                                X = face.X,
                                Y = face.Y,
                                Width = face.Width,
                                Height = face.Height,
                                Confidence = face.Confidence,
                                Embedding =
                                    embeddings[metadataFaces.Count + index]
                            }))
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
                    else
                    {
                        throw new InvalidOperationException(
                            "The photo changed during face detection and will be retried.");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failed++;
                    error = exception.Message;
                }

                processed++;
                Progress?.Invoke(
                    this,
                    new FaceProcessingProgressEventArgs(
                        processed,
                        pending.Count,
                        faceCount,
                        failed,
                        image.FileName,
                        error: error));
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

    private static DetectedFace ToDetectedFace(FaceRegion face)
    {
        var left = (float)face.X;
        var top = (float)face.Y;
        var width = (float)face.Width;
        var height = (float)face.Height;
        return new DetectedFace
        {
            X = left,
            Y = top,
            Width = width,
            Height = height,
            Confidence = face.Confidence,
            Landmarks =
            [
                new FaceLandmark(left + width * 0.30f, top + height * 0.38f),
                new FaceLandmark(left + width * 0.70f, top + height * 0.38f),
                new FaceLandmark(left + width * 0.50f, top + height * 0.58f),
                new FaceLandmark(left + width * 0.35f, top + height * 0.78f),
                new FaceLandmark(left + width * 0.65f, top + height * 0.78f)
            ]
        };
    }

    private static double IntersectionOverUnion(
        FaceRegion face,
        DetectedFace detection)
    {
        var left = Math.Max(face.X, detection.X);
        var top = Math.Max(face.Y, detection.Y);
        var right = Math.Min(face.X + face.Width, detection.X + detection.Width);
        var bottom = Math.Min(face.Y + face.Height, detection.Y + detection.Height);
        var intersection =
            Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union =
            face.Width * face.Height +
            detection.Width * detection.Height -
            intersection;
        return union <= 0 ? 0 : intersection / union;
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
