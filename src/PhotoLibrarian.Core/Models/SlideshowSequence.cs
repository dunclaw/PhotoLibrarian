namespace PhotoLibrarian.Core.Models;

/// <summary>A fixed photo working set, independent of subsequent grid selection changes.</summary>
/// <remarks>
/// Two or more selected photos restrict the slideshow to that selection, starting at the first one
/// in display order. A single selected photo plays the whole filtered set starting at that photo.
/// An explicit <c>startingPath</c> (e.g. the photo shown in the viewer) always wins when present.
/// </remarks>
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
        var restrictToSelection = selectedPaths.Count > 1;
        _photos = filteredImages
            .Where(image => image.MediaType == MediaType.Image &&
                (!restrictToSelection || selectedPaths.Contains(image.FilePath)))
            .ToList();
        if (startingPath is null && selectedPaths.Count == 1)
            startingPath = selectedPaths.First();
        var start = startingPath is null ? -1 : _photos.FindIndex(image =>
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
