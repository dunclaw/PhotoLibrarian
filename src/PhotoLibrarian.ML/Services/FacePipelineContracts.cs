namespace PhotoLibrarian.ML.Services;

public interface IFaceDetector
{
    void LoadModel();

    Task<IReadOnlyList<DetectedFace>> DetectFacesAsync(
        string imagePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects faces from an already-decoded source image, avoiding a second
    /// decode when the pipeline has shared pixels for this photo.
    /// </summary>
    Task<IReadOnlyList<DetectedFace>> DetectFacesAsync(
        DecodedImage image,
        CancellationToken cancellationToken = default) =>
        DetectFacesAsync(image.FilePath, cancellationToken);
}

public interface IFaceEmbedder
{
    void LoadModel();

    Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        string imagePath,
        IReadOnlyList<DetectedFace> faces,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates embeddings from an already-decoded source image, reusing the
    /// pixels the detector just ran against.
    /// </summary>
    Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        DecodedImage image,
        IReadOnlyList<DetectedFace> faces,
        CancellationToken cancellationToken = default) =>
        GenerateEmbeddingsAsync(image.FilePath, faces, cancellationToken);
}
