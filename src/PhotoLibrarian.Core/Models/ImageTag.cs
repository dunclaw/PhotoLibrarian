namespace PhotoLibrarian.Core.Models;

/// <summary>
/// Represents a tag associated with an image.
/// </summary>
public sealed class ImageTag
{
    public long ImageId { get; set; }
    public required string Tag { get; set; }
    public TagSource Source { get; set; } = TagSource.Manual;
    public float Confidence { get; set; } = 1.0f;
}

public enum TagSource
{
    Manual,
    AutoML,
    Imported,
    Metadata  // Tags read from XMP/IPTC metadata
}
