using Microsoft.ML.OnnxRuntime;

namespace PhotoLibrarian.ML.Services;

public sealed class FaceDetectionService : IFaceDetector
{
    private readonly OnnxSessionManager _sessionManager;
    private InferenceSession? _session;

    public string ModelFileName { get; set; } = FaceModelCatalog.Detector.FileName;
    public int MaximumInputDimension { get; set; } = 640;
    public float ConfidenceThreshold { get; set; } = 0.6f;
    public float NmsThreshold { get; set; } = 0.3f;
    public int MaximumFaces { get; set; } = 5000;

    public FaceDetectionService(OnnxSessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public bool IsModelAvailable => _sessionManager.ModelExists(ModelFileName);

    public void LoadModel()
    {
        _session = _sessionManager.LoadModel(ModelFileName);
        YuNetPostProcessor.ValidateModelOutputs(_session.OutputMetadata.Keys);
    }

    public async Task<IReadOnlyList<DetectedFace>> DetectFacesAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        var image = await ImagePixelData.LoadAsync(imagePath, cancellationToken);
        return await DetectFacesCoreAsync(image, cancellationToken);
    }

    public Task<IReadOnlyList<DetectedFace>> DetectFacesAsync(
        DecodedImage image,
        CancellationToken cancellationToken = default) =>
        image.Pixels is { } shared
            ? DetectFacesCoreAsync(shared, cancellationToken)
            : DetectFacesAsync(image.FilePath, cancellationToken);

    private async Task<IReadOnlyList<DetectedFace>> DetectFacesCoreAsync(
        ImagePixelData image,
        CancellationToken cancellationToken)
    {
        var session = _session ?? throw new InvalidOperationException("Model not loaded.");
        var resized = image.ResizeToFit(MaximumInputDimension, divisor: 32);

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputName = session.InputMetadata.Keys.Single();
            var input = NamedOnnxValue.CreateFromTensor(inputName, resized.ToBgrTensor());
            var outputs = _sessionManager.RunInference(() =>
            {
                using var results = session.Run([input]);
                return results.ToDictionary(
                    result => result.Name,
                    result => result.AsTensor<float>().ToArray(),
                    StringComparer.Ordinal);
            });
            cancellationToken.ThrowIfCancellationRequested();
            return (IReadOnlyList<DetectedFace>)YuNetPostProcessor.Decode(
                outputs,
                resized.Width,
                resized.Height,
                resized.Scale,
                image.Width,
                image.Height,
                ConfidenceThreshold,
                NmsThreshold,
                MaximumFaces);
        }, cancellationToken);
    }
}

public sealed class DetectedFace
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
    public float Confidence { get; init; }
    public IReadOnlyList<FaceLandmark> Landmarks { get; init; } = [];
}

public readonly record struct FaceLandmark(float X, float Y);
