namespace PhotoLibrarian.Core.Models;

/// <summary>
/// Thumbnail size tiers for cached thumbnails.
/// </summary>
public enum ThumbnailSize
{
    Small = 256,
    Medium = 512
}

/// <summary>
/// Represents a cached thumbnail blob.
/// </summary>
public sealed class ThumbnailEntry
{
    public long ImageId { get; set; }
    public ThumbnailSize Size { get; set; }
    public required byte[] Data { get; set; }
}
