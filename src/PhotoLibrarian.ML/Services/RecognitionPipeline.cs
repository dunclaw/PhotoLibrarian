using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.ML.Services;

public enum RecognitionStage
{
    None,
    FaceDetection,
    AutoTagging
}

public sealed record RecognitionRequest(
    bool IsFaceDetectionEnabled,
    AutoTaggingSettings? AutoTaggingSettings)
{
    public bool IsAutoTaggingEnabled =>
        AutoTaggingSettings?.CanRun == true;

    public bool HasWork => IsFaceDetectionEnabled || IsAutoTaggingEnabled;
}

public sealed record RecognitionResult(
    int Processed,
    int Failed,
    int FacesFound,
    int TagsAdded);

/// <summary>
/// Runs people recognition and descriptive tagging as one background job over
/// a single per-photo work item: the source is decoded and oriented once, then
/// every stage that is stale for that photo runs against the shared pixels
/// before moving to the next photo.
/// </summary>
public sealed class RecognitionPipeline
{
    private readonly IFaceScanStore _faceStore;
    private readonly IAutoTagStore _tagStore;
    private readonly IFaceModelProvider _faceModelProvider;
    private readonly IFaceDetector _detector;
    private readonly IFaceEmbedder _embedder;
    private readonly IAutoTagModelProvider _tagModelProvider;
    private readonly IAutoTagger _tagger;
    private readonly IBackgroundActivityGate _activityGate;
    private readonly IImageDecoder _decoder;

    public RecognitionPipeline(
        IFaceScanStore faceStore,
        IAutoTagStore tagStore,
        IFaceModelProvider faceModelProvider,
        IFaceDetector detector,
        IFaceEmbedder embedder,
        IAutoTagModelProvider tagModelProvider,
        IAutoTagger tagger,
        IBackgroundActivityGate activityGate,
        IImageDecoder decoder)
    {
        _faceStore = faceStore;
        _tagStore = tagStore;
        _faceModelProvider = faceModelProvider;
        _detector = detector;
        _embedder = embedder;
        _tagModelProvider = tagModelProvider;
        _tagger = tagger;
        _activityGate = activityGate;
        _decoder = decoder;
    }

    public event EventHandler<RecognitionProgressEventArgs>? Progress;

    public async Task<RecognitionResult> ProcessLibraryAsync(
        RecognitionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.HasWork)
        {
            Progress?.Invoke(
                this,
                RecognitionProgressEventArgs.Complete(0, 0, 0, 0, 0));
            return new RecognitionResult(0, 0, 0, 0);
        }

        Progress?.Invoke(this, RecognitionProgressEventArgs.Preparing());

        var settings = request.AutoTaggingSettings;
        var taggingEnabled = request.IsAutoTaggingEnabled;
        var definition = taggingEnabled
            ? AutoTagModelCatalog.ForId(settings!.ProfileId)
            : null;

        if (request.IsFaceDetectionEnabled)
        {
            await _faceModelProvider.EnsureModelsAsync(cancellationToken);
            _detector.LoadModel();
            _embedder.LoadModel();
        }

        if (taggingEnabled)
        {
            await _tagModelProvider.EnsureModelAsync(
                settings!.ProfileId,
                settings.ModelDirectory,
                cancellationToken);
            _tagger.LoadModel(settings.ProfileId, settings.ModelDirectory);
        }

        var workItems = await BuildWorkItemsAsync(
            request,
            definition,
            cancellationToken);

        var processed = 0;
        var failed = 0;
        var facesFound = 0;
        var tagsAdded = 0;
        var stage = RecognitionStage.None;

        Progress?.Invoke(
            this,
            new RecognitionProgressEventArgs(
                RecognitionStage.None,
                0,
                workItems.Count,
                0,
                0,
                0));

        try
        {
            foreach (var item in workItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // One idle gate for the whole photo, so face and tag work
                // pause and resume together.
                await _activityGate.WaitForIdleAsync(cancellationToken);

                string? error = null;
                var itemFailed = false;
                DecodedImage? decoded = null;

                try
                {
                    // A photo that needs face work is decoded once at source
                    // resolution and shared; a tags-only photo skips the
                    // full-resolution decode entirely.
                    decoded = await _decoder.DecodeAsync(
                        item.Image.FilePath,
                        item.NeedsFaces,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    itemFailed = true;
                    error = exception.Message;
                }

                if (decoded is not null && item.NeedsFaces)
                {
                    stage = RecognitionStage.FaceDetection;
                    try
                    {
                        facesFound += await FaceScanWorkItem.ScanAsync(
                            item.Image,
                            decoded,
                            _faceStore,
                            _detector,
                            _embedder,
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        itemFailed = true;
                        error = exception.Message;
                    }
                }

                if (decoded is not null && item.NeedsTags)
                {
                    stage = RecognitionStage.AutoTagging;
                    try
                    {
                        tagsAdded += await AutoTagWorkItem.TagAsync(
                            item.Image,
                            decoded,
                            _tagStore,
                            _tagger,
                            settings!,
                            definition!,
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        itemFailed = true;
                        error = exception.Message;
                    }
                }

                if (itemFailed)
                {
                    failed++;
                }

                processed++;
                Progress?.Invoke(
                    this,
                    new RecognitionProgressEventArgs(
                        stage,
                        processed,
                        workItems.Count,
                        facesFound,
                        tagsAdded,
                        failed,
                        item.Image.FileName,
                        error: error,
                        profileId: settings?.ProfileId));
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            Progress?.Invoke(
                this,
                new RecognitionProgressEventArgs(
                    stage,
                    processed,
                    workItems.Count,
                    facesFound,
                    tagsAdded,
                    failed,
                    isCanceled: true,
                    profileId: settings?.ProfileId));
            throw;
        }

        Progress?.Invoke(
            this,
            new RecognitionProgressEventArgs(
                RecognitionStage.None,
                processed,
                workItems.Count,
                facesFound,
                tagsAdded,
                failed,
                isComplete: true,
                profileId: settings?.ProfileId));
        return new RecognitionResult(processed, failed, facesFound, tagsAdded);
    }

    /// <summary>
    /// Merges the stale-work queues for both stages into one ordered list of
    /// per-photo work items. Each stage keeps its own pipeline version, so a
    /// photo that is current for one stage is not reprocessed because the
    /// other stage is stale.
    /// </summary>
    private async Task<List<RecognitionWorkItem>> BuildWorkItemsAsync(
        RecognitionRequest request,
        AutoTagModelDefinition? definition,
        CancellationToken cancellationToken)
    {
        var pendingFaces = request.IsFaceDetectionEnabled
            ? await _faceStore.GetImagesNeedingFaceScanAsync(
                FaceModelCatalog.PipelineVersion,
                cancellationToken)
            : [];
        var pendingTags = definition is not null
            ? await _tagStore.GetImagesNeedingAutoTagsAsync(
                definition.PipelineVersion,
                cancellationToken)
            : [];

        var items = new List<RecognitionWorkItem>(
            pendingFaces.Count + pendingTags.Count);
        var byImageId = new Dictionary<long, int>();

        foreach (var image in pendingFaces)
        {
            byImageId[image.Id] = items.Count;
            items.Add(new RecognitionWorkItem(image, true, false));
        }

        foreach (var image in pendingTags)
        {
            if (byImageId.TryGetValue(image.Id, out var index))
            {
                items[index] = items[index] with { NeedsTags = true };
                continue;
            }

            byImageId[image.Id] = items.Count;
            items.Add(new RecognitionWorkItem(image, false, true));
        }

        return items;
    }

    private sealed record RecognitionWorkItem(
        ImageEntry Image,
        bool NeedsFaces,
        bool NeedsTags);
}

public sealed class RecognitionProgressEventArgs(
    RecognitionStage stage,
    int processed,
    int total,
    int facesFound,
    int tagsAdded,
    int failed,
    string? currentFile = null,
    bool isPreparing = false,
    bool isComplete = false,
    bool isCanceled = false,
    string? error = null,
    string? profileId = null) : EventArgs
{
    public RecognitionStage Stage { get; } = stage;
    public int Processed { get; } = processed;
    public int Total { get; } = total;
    public int FacesFound { get; } = facesFound;
    public int TagsAdded { get; } = tagsAdded;
    public int Failed { get; } = failed;
    public string? CurrentFile { get; } = currentFile;
    public bool IsPreparing { get; } = isPreparing;
    public bool IsComplete { get; } = isComplete;
    public bool IsCanceled { get; } = isCanceled;
    public string? Error { get; } = error;
    public string? ProfileId { get; } = profileId;

    public bool IsRunning => !IsComplete && !IsCanceled;

    public static RecognitionProgressEventArgs Preparing() =>
        new(RecognitionStage.None, 0, 0, 0, 0, 0, isPreparing: true);

    public static RecognitionProgressEventArgs Complete(
        int processed,
        int total,
        int facesFound,
        int tagsAdded,
        int failed) =>
        new(
            RecognitionStage.None,
            processed,
            total,
            facesFound,
            tagsAdded,
            failed,
            isComplete: true);
}
