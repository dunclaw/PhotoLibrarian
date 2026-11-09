// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.ML.Services;

public sealed class FaceLibraryProcessor
{
    private readonly IFaceScanStore _store;
    private readonly IFaceModelProvider _modelProvider;
    private readonly IFaceDetector _detector;
    private readonly IFaceEmbedder _embedder;
    private bool _modelsReady;

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

    public async Task<IReadOnlyList<FaceRegion>> ScanPhotoAsync(ImageEntry image,
        CancellationToken token = default)
    {
        var file = new FileInfo(image.FilePath);
        if (!file.Exists || file.Length != image.FileSize || file.LastWriteTimeUtc != image.DateModified.ToUniversalTime())
            throw new IOException("The photo changed since indexing. Refresh the library before fusing.");
        if (image.FaceScanVersion != FaceModelCatalog.PipelineVersion)
        {
            if (!_modelsReady)
            {
                await _modelProvider.EnsureModelsAsync(token);
                await Task.Run(() =>
                {
                    _detector.LoadModel();
                    _embedder.LoadModel();
                }, token);
                _modelsReady = true;
            }
            await FaceScanWorkItem.ScanAsync(image, DecodedImage.WithoutPixels(image.FilePath),
                _store, _detector, _embedder, token);
            image.FaceScanVersion = FaceModelCatalog.PipelineVersion;
        }
        file.Refresh();
        if (!file.Exists || file.Length != image.FileSize || file.LastWriteTimeUtc != image.DateModified.ToUniversalTime())
            throw new IOException("The photo changed during face scanning. Refresh the library before fusing.");
        return await _store.GetFacesForImageAsync(image.Id);
    }

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
                    faceCount += await FaceScanWorkItem.ScanAsync(
                        image,
                        DecodedImage.WithoutPixels(image.FilePath),
                        _store,
                        _detector,
                        _embedder,
                        cancellationToken);
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
