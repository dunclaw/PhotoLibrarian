namespace PhotoLibrarian.ML.Services;

public interface IFaceDetector
{
    void LoadModel();

    Task<IReadOnlyList<DetectedFace>> DetectFacesAsync(
        string imagePath,
        CancellationToken cancellationToken = default);
}

public interface IFaceEmbedder
{
    void LoadModel();

    Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        string imagePath,
        IReadOnlyList<DetectedFace> faces,
        CancellationToken cancellationToken = default);
}
