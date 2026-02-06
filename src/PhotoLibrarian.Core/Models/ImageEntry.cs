namespace PhotoLibrarian.Core.Models;

/// <summary>
/// Represents a cached image in the library.
/// </summary>
public sealed class ImageEntry
{
    public long Id { get; set; }
    public required string FilePath { get; set; }
    public required string FileName { get; set; }
    public string? FileHash { get; set; }
    public long FileSize { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime? DateTaken { get; set; }
    public DateTime DateModified { get; set; }
    public DateTime DateIndexed { get; set; }

    // EXIF metadata
    public string? CameraMake { get; set; }
    public string? CameraModel { get; set; }
    public string? LensModel { get; set; }
    public double? FocalLength { get; set; }
    public double? Aperture { get; set; }
    public string? ExposureTime { get; set; }
    public int? Iso { get; set; }
    public double? GpsLatitude { get; set; }
    public double? GpsLongitude { get; set; }
    public int? Rating { get; set; }
    public int Orientation { get; set; } = 1;

    // Media type
    public MediaType MediaType { get; set; } = MediaType.Image;
    public double? VideoDuration { get; set; }
}

public enum MediaType
{
    Image,
    Video
}
