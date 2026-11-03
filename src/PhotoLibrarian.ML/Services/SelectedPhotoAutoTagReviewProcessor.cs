// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;

namespace PhotoLibrarian.ML.Services;

/// <summary>
/// Generates local automatic-tag suggestions for an explicitly selected set of
/// photos. This processor never writes tags.
/// </summary>
public sealed class SelectedPhotoAutoTagReviewProcessor
{
    private readonly IAutoTagModelProvider _modelProvider;
    private readonly IAutoTagger _autoTagger;

    public SelectedPhotoAutoTagReviewProcessor(
        IAutoTagModelProvider modelProvider,
        IAutoTagger autoTagger)
    {
        _modelProvider = modelProvider;
        _autoTagger = autoTagger;
    }

    public async Task<SelectedPhotoAutoTagReviewResult> GenerateAsync(
        IReadOnlyList<ImageEntry> selectedImages,
        AutoTaggingSettings settings,
        IProgress<SelectedPhotoAutoTagReviewProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!settings.IsProfileApproved)
        {
            throw new InvalidOperationException(
                "Choose and approve an automatic-tag profile in Settings before reviewing tags.");
        }

        var definition = AutoTagModelCatalog.ForId(settings.ProfileId);
        if (!definition.CanBeApproved)
        {
            throw new InvalidOperationException(
                "The selected automatic-tag profile is not available for review.");
        }

        var images = selectedImages
            .Where(image => image.MediaType == MediaType.Image)
            .ToArray();
        if (images.Length == 0)
        {
            throw new InvalidOperationException(
                "Select one or more photos before reviewing automatic tags.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _modelProvider.EnsureModelAsync(
            settings.ProfileId,
            settings.ModelDirectory,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _autoTagger.LoadModel(settings.ProfileId, settings.ModelDirectory);

        var results = new List<SelectedPhotoAutoTagReviewItem>(images.Length);
        foreach (var image in images)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new SelectedPhotoAutoTagReviewResult(results, true);
            }

            IReadOnlyList<TagPrediction> predictions = [];
            string? error = null;
            string? identity = null;
            try
            {
                var staleMessage = ValidatePhotoSnapshot(image);
                if (staleMessage is not null)
                {
                    throw new InvalidOperationException(staleMessage);
                }

                identity = await RecognitionSourceIdentity.ComputeAsync(
                    image.FilePath, cancellationToken);
                if (image.SourceIdentity is not null && image.SourceIdentity != identity)
                    throw new InvalidOperationException("The selected photo content changed.");
                predictions = await _autoTagger.PredictTagsAsync(
                    image.FilePath,
                    settings.ProfileId,
                    settings.ModelDirectory,
                    settings.MaximumTags,
                    settings.ConfidenceThreshold,
                    cancellationToken);

                staleMessage = ValidatePhotoSnapshot(image);
                if (staleMessage is not null)
                {
                    throw new InvalidOperationException(staleMessage);
                }
                await RecognitionSourceIdentity.ValidateAsync(image.FilePath, identity, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return new SelectedPhotoAutoTagReviewResult(results, true);
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }

            results.Add(new SelectedPhotoAutoTagReviewItem(
                image,
                error is null ? predictions : [],
                error,
                identity));
            progress?.Report(new SelectedPhotoAutoTagReviewProgress(
                results.Count,
                images.Length,
                image.FileName,
                error));
        }

        return new SelectedPhotoAutoTagReviewResult(results, false);
    }

    public static string? ValidatePhotoSnapshot(ImageEntry image)
    {
        try
        {
            var file = new FileInfo(image.FilePath);
            if (!file.Exists)
            {
                return "The photo is no longer available. Refresh the library and try again.";
            }

            if (file.Length != image.FileSize ||
                file.LastWriteTimeUtc != image.DateModified.ToUniversalTime())
            {
                return "The photo changed after it was selected. Refresh the library and try again.";
            }

            return null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return $"The photo could not be checked: {exception.Message}";
        }
    }
}

public sealed record SelectedPhotoAutoTagReviewItem(
    ImageEntry Image,
    IReadOnlyList<TagPrediction> Predictions,
    string? Error,
    string? SourceIdentity = null);

public sealed record SelectedPhotoAutoTagReviewProgress(
    int Processed,
    int Total,
    string CurrentFile,
    string? Error);

public sealed record SelectedPhotoAutoTagReviewResult(
    IReadOnlyList<SelectedPhotoAutoTagReviewItem> Items,
    bool IsCanceled);
