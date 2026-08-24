namespace PhotoLibrarian.Core.Models;

public enum RatingFilterMode
{
    Exact,
    AndHigher,
    AndLower
}

public enum FlagFilterMode
{
    Any,
    Flagged,
    Unflagged
}

public enum MediaKindFilter
{
    Any,
    Photos,
    Videos
}

[Flags]
public enum MissingMetadataFilter
{
    None = 0,
    Tags = 1,
    Rating = 2,
    CaptureDate = 4,
    Geotag = 8
}

/// <summary>
/// AND-composed refinement applied after the left navigation pane builds its union.
/// </summary>
public sealed record ImageRefinementFilter
{
    public static ImageRefinementFilter Empty { get; } = new();

    public int? Rating { get; init; }
    public RatingFilterMode RatingMode { get; init; } = RatingFilterMode.AndHigher;
    public DateTime? DateFrom { get; init; }
    public DateTime? DateTo { get; init; }
    public IReadOnlyList<string> IncludedTags { get; init; } = [];
    public IReadOnlyList<string> ExcludedTags { get; init; } = [];
    public long? PersonId { get; init; }
    public FlagFilterMode Flag { get; init; }
    public MediaKindFilter MediaKind { get; init; }
    public IReadOnlyList<string> Extensions { get; init; } = [];
    public MissingMetadataFilter MissingMetadata { get; init; }

    public bool IsActive =>
        Rating.HasValue ||
        DateFrom.HasValue ||
        DateTo.HasValue ||
        IncludedTags.Count > 0 ||
        ExcludedTags.Count > 0 ||
        PersonId.HasValue ||
        Flag != FlagFilterMode.Any ||
        MediaKind != MediaKindFilter.Any ||
        Extensions.Count > 0 ||
        MissingMetadata != MissingMetadataFilter.None;

    public bool RequiresTags =>
        IncludedTags.Count > 0 ||
        ExcludedTags.Count > 0 ||
        MissingMetadata.HasFlag(MissingMetadataFilter.Tags);

    public bool RequiresPeople => PersonId.HasValue;

    public bool Matches(
        ImageEntry image,
        IReadOnlyCollection<string>? tags = null,
        IReadOnlyCollection<long>? personIds = null)
    {
        tags ??= Array.Empty<string>();
        personIds ??= Array.Empty<long>();

        if (Rating is int rating)
        {
            if (image.Rating is not int imageRating || imageRating <= 0)
                return false;

            bool ratingMatches = RatingMode switch
            {
                RatingFilterMode.Exact => imageRating == rating,
                RatingFilterMode.AndHigher => imageRating >= rating,
                RatingFilterMode.AndLower => imageRating <= rating,
                _ => false
            };

            if (!ratingMatches)
                return false;
        }

        var captureDate = image.DateTaken;
        if ((DateFrom.HasValue || DateTo.HasValue) &&
            !captureDate.HasValue)
            return false;

        if (DateFrom is DateTime from && captureDate!.Value.Date < from.Date)
            return false;

        if (DateTo is DateTime to && captureDate!.Value.Date > to.Date)
            return false;

        if (IncludedTags.Any(required =>
                !tags.Contains(required, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (ExcludedTags.Any(excluded =>
                tags.Contains(excluded, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (PersonId is long personId && !personIds.Contains(personId))
            return false;

        if (Flag == FlagFilterMode.Flagged && !image.IsFlagged)
            return false;

        if (Flag == FlagFilterMode.Unflagged && image.IsFlagged)
            return false;

        if (MediaKind == MediaKindFilter.Photos && image.MediaType != MediaType.Image)
            return false;

        if (MediaKind == MediaKindFilter.Videos && image.MediaType != MediaType.Video)
            return false;

        if (Extensions.Count > 0)
        {
            var extension = NormalizeExtension(Path.GetExtension(image.FilePath));
            if (!Extensions.Select(NormalizeExtension)
                .Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (MissingMetadata.HasFlag(MissingMetadataFilter.Tags) && tags.Count > 0)
            return false;

        if (MissingMetadata.HasFlag(MissingMetadataFilter.Rating) &&
            image.Rating is > 0)
        {
            return false;
        }

        if (MissingMetadata.HasFlag(MissingMetadataFilter.CaptureDate) &&
            image.DateTaken.HasValue)
        {
            return false;
        }

        if (MissingMetadata.HasFlag(MissingMetadataFilter.Geotag) &&
            image.GpsLatitude.HasValue &&
            image.GpsLongitude.HasValue)
        {
            return false;
        }

        return true;
    }

    public static string NormalizeExtension(string extension)
    {
        var trimmed = extension.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        return trimmed.StartsWith('.') ? trimmed : $".{trimmed}";
    }
}
