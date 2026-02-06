namespace PhotoLibrarian.Core.Models;

/// <summary>
/// Represents a watched folder in the library.
/// </summary>
public sealed class WatchedFolder
{
    public long Id { get; set; }
    public required string Path { get; set; }
    public bool IncludeSubfolders { get; set; } = true;
    public DateTime DateAdded { get; set; }
    public DateTime? LastScanned { get; set; }
}
