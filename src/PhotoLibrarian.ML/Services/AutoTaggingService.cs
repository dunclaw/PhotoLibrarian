using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PhotoLibrarian.ML.Services;

/// <summary>
/// Auto-tagging service using ONNX-based image classification/tagging models.
/// Supports RAM++ style models that output multi-label tag predictions.
/// </summary>
public sealed class AutoTaggingService
{
    private readonly OnnxSessionManager _sessionManager;
    private InferenceSession? _session;
    private string[]? _tagLabels;

    // Model configuration
    public string ModelFileName { get; set; } = "ram_plus.onnx";
    public string TagLabelsFileName { get; set; } = "ram_plus_tags.txt";
    public int InputSize { get; set; } = 384;
    public float ConfidenceThreshold { get; set; } = 0.5f;

    public AutoTaggingService(OnnxSessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public bool IsModelLoaded => _session is not null;
    public bool IsModelAvailable => _sessionManager.ModelExists(ModelFileName);

    /// <summary>
    /// Loads the tagging model and tag label list.
    /// </summary>
    public void LoadModel()
    {
        _session = _sessionManager.LoadModel(ModelFileName);

        var labelsPath = Path.Combine(_sessionManager.ModelDirectory, TagLabelsFileName);
        if (File.Exists(labelsPath))
        {
            _tagLabels = File.ReadAllLines(labelsPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Trim())
                .ToArray();
        }
    }

    /// <summary>
    /// Runs inference on a single image and returns predicted tags with confidence scores.
    /// </summary>
    public async Task<List<TagPrediction>> PredictTagsAsync(string imagePath)
    {
        if (_session is null)
            throw new InvalidOperationException("Model not loaded. Call LoadModel() first.");

        var tensor = await ImagePreprocessor.PreprocessImageAsync(
            imagePath, InputSize,
            ImagePreprocessor.ImageNetMean,
            ImagePreprocessor.ImageNetStd);

        var inputName = _session.InputNames[0];
        var inputs = new[] { ImagePreprocessor.CreateInput(inputName, tensor) };

        using var results = _session.Run(inputs);
        var output = results.First().AsTensor<float>();

        return ExtractTags(output);
    }

    /// <summary>
    /// Batch processes multiple images for tagging.
    /// </summary>
    public async IAsyncEnumerable<(string FilePath, List<TagPrediction> Tags)> PredictBatchAsync(
        IEnumerable<string> imagePaths,
        IProgress<int>? progress = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken ct = default)
    {
        int count = 0;
        foreach (var path in imagePaths)
        {
            ct.ThrowIfCancellationRequested();
            List<TagPrediction> tags;
            try
            {
                tags = await PredictTagsAsync(path);
            }
            catch
            {
                tags = [];
            }

            count++;
            if (count % 10 == 0) progress?.Report(count);
            yield return (path, tags);
        }
        progress?.Report(count);
    }

    private List<TagPrediction> ExtractTags(Tensor<float> output)
    {
        var predictions = new List<TagPrediction>();
        var length = (int)output.Length;

        for (int i = 0; i < length; i++)
        {
            // Apply sigmoid for multi-label classification
            var score = Sigmoid(output[0, i]);
            if (score >= ConfidenceThreshold)
            {
                var tagName = (_tagLabels is not null && i < _tagLabels.Length)
                    ? _tagLabels[i]
                    : $"tag_{i}";

                predictions.Add(new TagPrediction(tagName, score));
            }
        }

        return predictions.OrderByDescending(t => t.Confidence).ToList();
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));
}

public sealed record TagPrediction(string Tag, float Confidence);
