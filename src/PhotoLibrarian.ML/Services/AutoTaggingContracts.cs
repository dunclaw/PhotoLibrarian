namespace PhotoLibrarian.ML.Services;

public enum AutoTagQualityStatus
{
    Untested,
    Approved,
    Rejected
}

public sealed record AutoTaggingSettings(
    bool IsEnabled = false,
    string ProfileId = AutoTagModelCatalog.RamPlusProfileId,
    int MaximumTags = 8,
    float ConfidenceThreshold = 0.40f,
    string? ModelDirectory = null,
    Dictionary<string, AutoTagQualityStatus>? ProfileQualityStatuses = null)
{
    public AutoTagQualityStatus GetQualityStatus(string profileId)
    {
        if (!AutoTagModelCatalog.TryGet(profileId, out var definition))
        {
            return AutoTagQualityStatus.Untested;
        }

        return ProfileQualityStatuses?.GetValueOrDefault(
            definition.ApprovalKey) ?? AutoTagQualityStatus.Untested;
    }

    public bool IsProfileApproved =>
        GetQualityStatus(ProfileId) == AutoTagQualityStatus.Approved;

    public bool CanRun =>
        IsEnabled &&
        IsProfileApproved &&
        AutoTagModelCatalog.TryGet(ProfileId, out var definition) &&
        definition.CanBeApproved;
}

public interface IAutoTagger
{
    void LoadModel(string profileId, string? modelDirectory);

    Task<IReadOnlyList<TagPrediction>> PredictTagsAsync(
        string imagePath,
        string profileId,
        string? modelDirectory,
        int maximumTags,
        float confidenceThreshold,
        CancellationToken cancellationToken = default);
}

public interface IAutoTagModelProvider
{
    Task EnsureModelAsync(
        string profileId,
        string? modelDirectory,
        CancellationToken cancellationToken = default);

    string GetAssetPath(
        string profileId,
        AutoTagAssetDefinition asset,
        string? modelDirectory);
}

public interface IBackgroundActivityGate
{
    Task WaitForIdleAsync(CancellationToken cancellationToken = default);
}
