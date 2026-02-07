using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ML.Services;

namespace PhotoLibrarian.ML.Services;

/// <summary>
/// Background service that processes untagged images using the ML auto-tagging service.
/// </summary>
public sealed class BatchTagProcessor
{
    private readonly ImageRepository _imageRepo;
    private readonly TagRepository _tagRepo;
    private readonly AutoTaggingService _autoTagger;

    public event EventHandler<BatchTagProgressEventArgs>? Progress;

    public BatchTagProcessor(
        ImageRepository imageRepo,
        TagRepository tagRepo,
        AutoTaggingService autoTagger)
    {
        _imageRepo = imageRepo;
        _tagRepo = tagRepo;
        _autoTagger = autoTagger;
    }

    /// <summary>
    /// Processes all untagged images in the library.
    /// </summary>
    public async Task ProcessUntaggedAsync(CancellationToken ct = default)
    {
        if (!_autoTagger.IsModelLoaded)
        {
            if (!_autoTagger.IsModelAvailable) return;
            _autoTagger.LoadModel();
        }

        var images = await _imageRepo.GetAllAsync();
        var untagged = new List<ImageEntry>();

        foreach (var img in images)
        {
            if (img.MediaType == MediaType.Video) continue;
            var existing = await _tagRepo.GetTagsAsync(img.Id);
            if (!existing.Any(t => t.Source == TagSource.AutoML))
                untagged.Add(img);
        }

        if (untagged.Count == 0) return;

        int processed = 0;
        Progress?.Invoke(this, new BatchTagProgressEventArgs(0, untagged.Count));

        var paths = untagged.Select(i => i.FilePath);
        var pathToImage = untagged.ToDictionary(i => i.FilePath, i => i);

        await foreach (var (filePath, tags) in _autoTagger.PredictBatchAsync(paths, null, ct))
        {
            if (!pathToImage.TryGetValue(filePath, out var entry)) continue;

            foreach (var tag in tags)
            {
                await _tagRepo.AddTagAsync(new ImageTag
                {
                    ImageId = entry.Id,
                    Tag = tag.Tag,
                    Source = TagSource.AutoML,
                    Confidence = tag.Confidence
                });
            }

            processed++;
            if (processed % 5 == 0)
                Progress?.Invoke(this, new BatchTagProgressEventArgs(processed, untagged.Count));
        }

        Progress?.Invoke(this, new BatchTagProgressEventArgs(processed, untagged.Count, isComplete: true));
    }
}

public sealed class BatchTagProgressEventArgs(int processed, int total, bool isComplete = false) : EventArgs
{
    public int Processed { get; } = processed;
    public int Total { get; } = total;
    public bool IsComplete { get; } = isComplete;
}
