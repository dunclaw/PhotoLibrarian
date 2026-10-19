namespace PhotoLibrarian.Core.Models;

/// <summary>A fixed photo working set, independent of subsequent grid selection changes.</summary>
public sealed class SlideshowSequence
{
    private readonly List<ImageEntry> _photos;

    public int Count => _photos.Count;
    public int Index { get; private set; }
    public ImageEntry? Current => Count == 0 ? null : _photos[Index];

    public SlideshowSequence(
        IEnumerable<ImageEntry> filteredImages,
        IEnumerable<ImageEntry> selectedImages,
        string? startingPath = null)
    {
        var selectedPaths = selectedImages
            .Select(image => image.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _photos = filteredImages
            .Where(image => image.MediaType == MediaType.Image &&
                (selectedPaths.Count == 0 || selectedPaths.Contains(image.FilePath)))
            .ToList();
        var start = _photos.FindIndex(image =>
            string.Equals(image.FilePath, startingPath, StringComparison.OrdinalIgnoreCase));
        Index = Math.Max(0, start);
    }

    public void Next()
    {
        if (Count > 0) Index = (Index + 1) % Count;
    }

    public void Previous()
    {
        if (Count > 0) Index = (Index - 1 + Count) % Count;
    }
}
