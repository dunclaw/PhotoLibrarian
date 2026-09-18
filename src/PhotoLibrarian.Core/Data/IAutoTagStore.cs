using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Data;

/// <summary>
/// Persists versioned automatic-tagging results without overwriting user-created tags.
/// </summary>
public interface IAutoTagStore
{
    Task<List<ImageEntry>> GetImagesNeedingAutoTagsAsync(
        string scanVersion,
        CancellationToken cancellationToken = default);

    Task<bool> TryReplaceAutoTagsAsync(
        ImageEntry expectedImage,
        IReadOnlyCollection<GeneratedImageTag> tags,
        string scanVersion,
        CancellationToken cancellationToken = default);
}

public sealed record GeneratedImageTag(string Tag, float Confidence);
