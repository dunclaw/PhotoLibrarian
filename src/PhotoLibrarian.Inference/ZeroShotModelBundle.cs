using System.Security.Cryptography;
using System.Text.Json;

namespace PhotoLibrarian.Inference;

public sealed record ZeroShotLabel(string Label, float[] Embedding);
public sealed record ZeroShotPrediction(int Rank, string Label, float Confidence, bool AboveThreshold);

public sealed class ZeroShotModelBundle
{
    public string ModelId { get; init; } = "";
    public string ModelFile { get; init; } = "";
    public string ModelSha256 { get; init; } = "";
    public string VocabularySha256 { get; init; } = "";
    public string InputName { get; init; } = "";
    public string OutputName { get; init; } = "";
    public int InputSize { get; init; }
    public string ResizeMode { get; init; } = "";
    public string Interpolation { get; init; } = "";
    public float[] NormalizationMean { get; init; } = [];
    public float[] NormalizationStd { get; init; } = [];
    public string ScoreKind { get; init; } = "";
    public double LogitScale { get; init; } = 1;
    public double LogitBias { get; init; }
    public string Precision { get; init; } = "";
    public ZeroShotLabel[] Labels { get; init; } = [];

    public static ZeroShotModelBundle Load(string path, string expectedModelId)
    {
        var bundle = JsonSerializer.Deserialize<ZeroShotModelBundle>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Empty zero-shot model bundle.");
        bundle.Validate(expectedModelId);
        var modelPath = Path.Combine(Path.GetDirectoryName(path)!, bundle.ModelFile);
        using var modelStream = File.OpenRead(modelPath);
        var hash = Convert.ToHexString(SHA256.HashData(modelStream));
        if (!hash.Equals(bundle.ModelSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Model checksum does not match '{Path.GetFileName(path)}'. Re-run asset preparation.");
        }

        return bundle;
    }

    public void Validate(string expectedModelId)
    {
        if (ModelId != expectedModelId ||
            string.IsNullOrWhiteSpace(ModelFile) ||
            ModelFile != Path.GetFileName(ModelFile) ||
            ModelFile.Contains(':') ||
            !ModelFile.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(InputName) ||
            string.IsNullOrWhiteSpace(OutputName) ||
            InputSize is < 16 or > 2048 ||
            ResizeMode is not ("squash" or "shortest-center-crop") ||
            Interpolation is not ("cubic" or "linear") ||
            ScoreKind is not ("cosine" or "sigmoid") ||
            !double.IsFinite(LogitScale) || LogitScale <= 0 ||
            !double.IsFinite(LogitBias) ||
            NormalizationMean is not { Length: 3 } ||
            NormalizationStd is not { Length: 3 } ||
            NormalizationMean.Any(value => !float.IsFinite(value)) ||
            NormalizationStd.Any(value => !float.IsFinite(value) || value <= 0) ||
            Labels is not { Length: > 0 })
        {
            throw new InvalidDataException("Invalid zero-shot model bundle configuration.");
        }

        var dimension = Labels[0]?.Embedding?.Length ?? 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in Labels)
        {
            if (label is null || string.IsNullOrWhiteSpace(label.Label) ||
                !names.Add(label.Label) || dimension == 0 ||
                label.Embedding is null || label.Embedding.Length != dimension ||
                label.Embedding.Any(value => !float.IsFinite(value)) ||
                SquaredNorm(label.Embedding) == 0)
            {
                throw new InvalidDataException("Invalid or duplicate label embedding in zero-shot bundle.");
            }
        }
    }

    public IReadOnlyList<ZeroShotPrediction> Predict(
        IReadOnlyList<float> imageEmbedding,
        int maximumPredictions,
        float threshold)
    {
        if (imageEmbedding.Count != Labels[0].Embedding.Length ||
            imageEmbedding.Any(value => !float.IsFinite(value)) ||
            SquaredNorm(imageEmbedding) == 0)
        {
            throw new InvalidDataException("Model returned an invalid image embedding.");
        }

        var imageNorm = Math.Sqrt(SquaredNorm(imageEmbedding));
        return Labels.Select(label =>
        {
            double dot = 0;
            for (var index = 0; index < imageEmbedding.Count; index++)
            {
                dot += (double)imageEmbedding[index] * label.Embedding[index];
            }

            var cosine = Math.Clamp(
                dot / (imageNorm * Math.Sqrt(SquaredNorm(label.Embedding))),
                -1,
                1);
            var score = ScoreKind == "sigmoid"
                ? Sigmoid(LogitScale * cosine + LogitBias)
                : cosine;
            return (label.Label, Score: (float)score);
        })
        .OrderByDescending(item => item.Score)
        .Take(maximumPredictions)
        .Select((item, rank) => new ZeroShotPrediction(
            rank + 1, item.Label, item.Score,
            AboveThreshold: item.Score >= threshold))
        .ToArray();
    }

    private static double SquaredNorm(IReadOnlyList<float> values) =>
        values.Sum(value => (double)value * value);

    private static double Sigmoid(double value) =>
        value >= 0
            ? 1 / (1 + Math.Exp(-value))
            : Math.Exp(value) / (1 + Math.Exp(value));
}
