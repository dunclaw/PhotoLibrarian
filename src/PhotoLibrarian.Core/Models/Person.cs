namespace PhotoLibrarian.Core.Models;

/// <summary>
/// Represents a named person for face recognition grouping.
/// </summary>
public sealed class Person
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public byte[]? ThumbnailData { get; set; }
    public int FaceCount { get; set; }
}
