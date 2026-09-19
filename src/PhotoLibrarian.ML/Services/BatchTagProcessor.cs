using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.ML.Services;

/// <summary>
/// Applies approved local content tags to photos in idle-time, restartable
/// batches.
/// </summary>
public sealed class BatchTagProcessor
{
    private readonly IAutoTagStore _store;
    private readonly IAutoTagModelProvider _modelProvider;
    private readonly IAutoTagger _autoTagger;
    private readonly IBackgroundActivityGate _activityGate;

    public BatchTagProcessor(
        IAutoTagStore store,
        IAutoTagModelProvider modelProvider,
        IAutoTagger autoTagger,
        IBackgroundActivityGate activityGate)
    {
        _store = store;
        _modelProvider = modelProvider;
        _autoTagger = autoTagger;
        _activityGate = activityGate;
    }

    public event EventHandler<BatchTagProgressEventArgs>? Progress;

    public async Task<BatchTagResult> ProcessLibraryAsync(
        AutoTaggingSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!settings.CanRun)
        {
            throw new InvalidOperationException(
                "Automatic tagging requires an explicitly approved model profile.");
        }

        var definition =
            AutoTagModelCatalog.ForId(settings.ProfileId);
        await _activityGate.WaitForIdleAsync(cancellationToken);
        Progress?.Invoke(
            this,
            BatchTagProgressEventArgs.Preparing(settings.ProfileId));
        await _modelProvider.EnsureModelAsync(
            settings.ProfileId,
            settings.ModelDirectory,
            cancellationToken);
        _autoTagger.LoadModel(
            settings.ProfileId,
            settings.ModelDirectory);

        var pending = await _store.GetImagesNeedingAutoTagsAsync(
            definition.PipelineVersion,
            cancellationToken);
        var processed = 0;
        var failed = 0;
        var tagsAdded = 0;
        Progress?.Invoke(
            this,
            new BatchTagProgressEventArgs(
                0,
                pending.Count,
                0,
                0,
                settings.ProfileId));

        try
        {
            foreach (var image in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _activityGate.WaitForIdleAsync(cancellationToken);
                string? error = null;

                try
                {
                    tagsAdded += await AutoTagWorkItem.TagAsync(
                        image,
                        DecodedImage.WithoutPixels(image.FilePath),
                        _store,
                        _autoTagger,
                        settings,
                        definition,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
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
                    new BatchTagProgressEventArgs(
                        processed,
                        pending.Count,
                        failed,
                        tagsAdded,
                        settings.ProfileId,
                        image.FileName,
                        error: error));
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            Progress?.Invoke(
                this,
                new BatchTagProgressEventArgs(
                    processed,
                    pending.Count,
                    failed,
                    tagsAdded,
                    settings.ProfileId,
                    isCanceled: true));
            throw;
        }

        Progress?.Invoke(
            this,
            new BatchTagProgressEventArgs(
                processed,
                pending.Count,
                failed,
                tagsAdded,
                settings.ProfileId,
                isComplete: true));
        return new BatchTagResult(processed, failed, tagsAdded);
    }
}

public sealed record BatchTagResult(
    int Processed,
    int Failed,
    int TagsAdded);

public sealed class BatchTagProgressEventArgs(
    int processed,
    int total,
    int failed,
    int tagsAdded,
    string profileId,
    string? currentFile = null,
    bool isPreparing = false,
    bool isComplete = false,
    bool isCanceled = false,
    string? error = null) : EventArgs
{
    public int Processed { get; } = processed;
    public int Total { get; } = total;
    public int Failed { get; } = failed;
    public int TagsAdded { get; } = tagsAdded;
    public string ProfileId { get; } = profileId;
    public string? CurrentFile { get; } = currentFile;
    public bool IsPreparing { get; } = isPreparing;
    public bool IsComplete { get; } = isComplete;
    public bool IsCanceled { get; } = isCanceled;
    public string? Error { get; } = error;

    public static BatchTagProgressEventArgs Preparing(
        string profileId) =>
        new(0, 0, 0, 0, profileId, isPreparing: true);
}
