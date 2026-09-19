namespace PhotoLibrarian.ML.Services;

/// <summary>
/// A single source decode shared by every recognition stage that runs for a
/// photo. Holding the oriented pixels once keeps the pipeline from re-opening
/// and re-decoding the same file for face detection, face embedding and
/// automatic tagging.
/// </summary>
public sealed class DecodedImage
{
    internal DecodedImage(string filePath, ImagePixelData? pixels)
    {
        FilePath = filePath;
        Pixels = pixels;
    }

    /// <summary>
    /// Creates a handle that carries no shared pixels. Stages fall back to
    /// their own (usually downsampled) decode, which is the cheaper option
    /// when only one stage needs the photo.
    /// </summary>
    public static DecodedImage WithoutPixels(string filePath) =>
        new(filePath, null);

    public string FilePath { get; }

    public bool HasSharedPixels => Pixels is not null;

    internal ImagePixelData? Pixels { get; }
}

public interface IImageDecoder
{
    /// <summary>
    /// Decodes <paramref name="filePath"/> once, applying EXIF orientation.
    /// When <paramref name="requiresSharedPixels"/> is false no full-resolution
    /// decode is performed, letting a single stage decode straight to its own
    /// (smaller) input size instead.
    /// </summary>
    Task<DecodedImage> DecodeAsync(
        string filePath,
        bool requiresSharedPixels,
        CancellationToken cancellationToken = default);
}

public sealed class WindowsImageDecoder : IImageDecoder
{
    public async Task<DecodedImage> DecodeAsync(
        string filePath,
        bool requiresSharedPixels,
        CancellationToken cancellationToken = default)
    {
        if (!requiresSharedPixels)
        {
            return DecodedImage.WithoutPixels(filePath);
        }

        var pixels = await ImagePixelData.LoadAsync(filePath, cancellationToken);
        return new DecodedImage(filePath, pixels);
    }
}
