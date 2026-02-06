namespace PhotoLibrarian.Core.Models;

/// <summary>
/// Represents a detected face region in an image, following MWG Region Schema concepts.
/// </summary>
public sealed class FaceRegion
{
    public long Id { get; set; }
    public long ImageId { get; set; }

    // Normalized coordinates (0.0 - 1.0 relative to image dimensions)
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public string? PersonName { get; set; }
    public long? PersonId { get; set; }

    // Face embedding for recognition (stored as float array)
    public float[]? Embedding { get; set; }
    public float Confidence { get; set; }
}
