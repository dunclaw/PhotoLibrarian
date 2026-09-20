using Microsoft.ML.OnnxRuntime;

namespace PhotoLibrarian.ML.Services;

public sealed class FaceEmbeddingService : IFaceEmbedder
{
    private readonly OnnxSessionManager _sessionManager;
    private InferenceSession? _session;

    public string ModelFileName { get; set; } = FaceModelCatalog.Recognizer.FileName;
    public int InputSize { get; set; } = 112;

    public FaceEmbeddingService(OnnxSessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public bool IsModelAvailable => _sessionManager.ModelExists(ModelFileName);

    public void LoadModel()
    {
        _session = _sessionManager.LoadModel(ModelFileName);
    }

    public async Task<float[]?> GenerateEmbeddingAsync(
        string imagePath,
        DetectedFace faceRegion,
        CancellationToken cancellationToken = default)
    {
        var embeddings = await GenerateEmbeddingsAsync(
            imagePath,
            [faceRegion],
            cancellationToken);
        return embeddings.SingleOrDefault();
    }

    public async Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        string imagePath,
        IReadOnlyList<DetectedFace> faces,
        CancellationToken cancellationToken = default)
    {
        if (faces.Count == 0)
        {
            return [];
        }

        var image = await ImagePixelData.LoadAsync(imagePath, cancellationToken);
        return await GenerateEmbeddingsCoreAsync(image, faces, cancellationToken);
    }

    public Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        DecodedImage image,
        IReadOnlyList<DetectedFace> faces,
        CancellationToken cancellationToken = default)
    {
        if (faces.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<float[]>>([]);
        }

        return image.Pixels is { } shared
            ? GenerateEmbeddingsCoreAsync(shared, faces, cancellationToken)
            : GenerateEmbeddingsAsync(image.FilePath, faces, cancellationToken);
    }

    private async Task<IReadOnlyList<float[]>> GenerateEmbeddingsCoreAsync(
        ImagePixelData image,
        IReadOnlyList<DetectedFace> faces,
        CancellationToken cancellationToken)
    {
        var session = _session ?? throw new InvalidOperationException("Model not loaded.");
        return await Task.Run(() =>
        {
            var embeddings = new List<float[]>(faces.Count);
            var inputName = session.InputMetadata.Keys.Single();
            foreach (var face in faces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var input = NamedOnnxValue.CreateFromTensor(
                    inputName,
                    image.CreateAlignedFaceTensor(face, InputSize));
                var embedding = _sessionManager.RunInference(() =>
                {
                    using var results = session.Run([input]);
                    return results.Single().AsTensor<float>().ToArray();
                });
                Normalize(embedding);
                embeddings.Add(embedding);
            }

            return (IReadOnlyList<float[]>)embeddings;
        }, cancellationToken);
    }

    public static float CosineSimilarity(ReadOnlySpan<float> first, ReadOnlySpan<float> second)
    {
        if (first.Length == 0 || first.Length != second.Length)
        {
            return 0;
        }

        float dot = 0;
        float normFirst = 0;
        float normSecond = 0;
        for (var index = 0; index < first.Length; index++)
        {
            dot += first[index] * second[index];
            normFirst += first[index] * first[index];
            normSecond += second[index] * second[index];
        }

        var denominator = MathF.Sqrt(normFirst) * MathF.Sqrt(normSecond);
        return denominator > 0 ? dot / denominator : 0;
    }

    private static void Normalize(Span<float> embedding)
    {
        float squaredNorm = 0;
        foreach (var value in embedding)
        {
            squaredNorm += value * value;
        }

        var norm = MathF.Sqrt(squaredNorm);
        if (norm <= 0)
        {
            return;
        }

        for (var index = 0; index < embedding.Length; index++)
        {
            embedding[index] /= norm;
        }
    }
}
