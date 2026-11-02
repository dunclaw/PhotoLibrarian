using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Data;

/// <summary>
/// Persists versioned automatic-tagging results without overwriting user-created tags.
/// </summary>
public interface IAutoTagStore
{
    Task<List<ImageTag>> GetTagsAsync(
        long imageId,
        CancellationToken cancellationToken = default);

    Task<List<ImageEntry>> GetImagesNeedingAutoTagsAsync(
        string scanVersion,
        CancellationToken cancellationToken = default);

    Task<bool> TryReplaceAutoTagsIfUnchangedAsync(
        ImageEntry expectedImage,
        IReadOnlyCollection<GeneratedImageTag> tags,
        IReadOnlyCollection<ImageTag> expectedAutoTags,
        string? expectedScanVersion,
        CancellationToken cancellationToken = default);

    Task<bool> TryRestoreAutoTagsAsync(
        ImageEntry expectedImage,
        IReadOnlyCollection<ImageTag> expectedAutoTags,
        IReadOnlyCollection<ImageTag> previousAutoTags,
        string? previousScanVersion,
        CancellationToken cancellationToken = default);

    Task SetAutoTagScanVersionAsync(
        long imageId,
        string scanVersion,
        CancellationToken cancellationToken = default);
}

public sealed record GeneratedImageTag(string Tag, float Confidence);
