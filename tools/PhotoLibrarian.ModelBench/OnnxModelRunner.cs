using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PhotoLibrarian.Inference;
using System.Diagnostics;

namespace PhotoLibrarian.ModelBench;

public sealed class OnnxModelRunner : IDisposable
{
    private readonly ModelDefinition _model;
    private readonly InferenceSession _session;
    private readonly ResolvedLabels _labels;
    private readonly string _inputName;
    private readonly int _inputWidth;
    private readonly int _inputHeight;
    private readonly float _threshold;
    private readonly int _maximumPredictions;
    private readonly ZeroShotModelBundle? _bundle;
    private readonly bool _deduplicateMappedLabels;

    public OnnxModelRunner(
        ModelDefinition model,
        string modelPath,
        string modelsDirectory,
        BenchmarkOptions options,
        out double loadMilliseconds)
    {
        _model = model;
        _threshold =
            options.ConfidenceThreshold ?? model.DefaultThreshold;
        _maximumPredictions = options.MaximumPredictions;
        _deduplicateMappedLabels = options.LabelMode == LabelMode.Mapped;

        var stopwatch = Stopwatch.StartNew();
        if (model.Task == ModelTask.ZeroShotTagging)
        {
            _bundle = ZeroShotModelBundle.Load(modelPath, model.Id);
            AuxiliaryBytes = new FileInfo(modelPath).Length;
            modelPath = Path.Combine(modelsDirectory, _bundle.ModelFile);
        }

        ModelPath = modelPath;
        ModelBytes = new FileInfo(modelPath).Length;
        using var sessionOptions = CreateSessionOptions(options.Provider);
        _session = new InferenceSession(modelPath, sessionOptions);
        try
        {
            _labels = _bundle is null
                ? LabelResolver.Resolve(_session, model, modelsDirectory, options.LabelMode)
                : new ResolvedLabels(
                    _bundle.Labels.Select(label => label.Label).ToArray(),
                    $"{model.ModelFileName}; vocabulary SHA256 {_bundle.VocabularySha256}");
            if (_bundle is null && File.Exists(Path.Combine(modelsDirectory, _labels.Source)))
            {
                AuxiliaryBytes = new FileInfo(Path.Combine(modelsDirectory, _labels.Source)).Length;
            }
            if (model.BinaryTagMask &&
                (model.OutputName is null ||
                 !_session.OutputMetadata.TryGetValue(model.OutputName, out var mask) ||
                 mask.ElementType != typeof(float) ||
                 !mask.Dimensions.SequenceEqual(new[] { 1, _labels.Labels.Count })))
                throw new InvalidDataException("RAM++ must return a named binary tag mask with one value per label.");
            _inputName = _session.InputNames.Single();
            var dimensions = _session.InputMetadata[_inputName].Dimensions;
            (_inputWidth, _inputHeight) = ResolveInputSize(dimensions, model);
            if (_bundle is not null &&
                (_inputName != _bundle.InputName ||
                 !_session.OutputNames.Contains(_bundle.OutputName) ||
                 _inputWidth != _bundle.InputSize || _inputHeight != _bundle.InputSize))
            {
                throw new InvalidDataException("ONNX input/output does not match the prepared bundle.");
            }

            stopwatch.Stop();
            loadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    public string LabelSource => _labels.Source;
    public string ModelPath { get; }
    public long ModelBytes { get; }
    public long AuxiliaryBytes { get; }
    public string ScoreKind => _bundle?.ScoreKind ?? (_model.BinaryTagMask ? "binary" : "confidence");
    public float Threshold => _threshold;
    public string? Precision => _bundle?.Precision;
    public string? ModelSha256 => _bundle?.ModelSha256;
    public string? VocabularySha256 => _bundle?.VocabularySha256;

    public ImageModelResult Run(
        string imagePath,
        string relativeImagePath,
        string provider)
    {
        var stopwatch = Stopwatch.StartNew();
        var prepared = ImageTensorBuilder.Create(
            imagePath,
            _inputWidth,
            _inputHeight,
            _model,
            _bundle);
        stopwatch.Stop();
        var preprocessMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        using var results = _session.Run(
            [NamedOnnxValue.CreateFromTensor(
                _inputName,
                prepared.Tensor)]);
        var outputName = _bundle?.OutputName ?? _model.OutputName;
        var outputTensor = (outputName is null
            ? results.First()
            : results.Single(result => result.Name == outputName))
            .AsTensor<float>();
        var values = outputTensor.ToArray();
        var dimensions = outputTensor.Dimensions.ToArray();
        stopwatch.Stop();
        var inferenceMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        stopwatch.Restart();

        var predictions = _model.Task switch
        {
            ModelTask.ZeroShotTagging =>
                _bundle!.Predict(values, _maximumPredictions, _threshold)
                    .Select(item => new PredictionResult(item.Rank, item.Label,
                        item.Confidence, AboveThreshold: item.AboveThreshold)).ToArray(),
            ModelTask.Classification => SelectClassification(values),
            ModelTask.MultiLabelTagging => SelectTags(values),
            ModelTask.ObjectDetectionV8 => SelectYoloV8(
                values,
                dimensions,
                prepared.Transform),
            ModelTask.ObjectDetectionV10 => SelectYoloV10(
                values,
                dimensions,
                prepared.Transform),
            _ => throw new InvalidOperationException(
                $"Unsupported model task {_model.Task}.")
        };
        stopwatch.Stop();

        return new ImageModelResult(
            imagePath,
            relativeImagePath,
            _model.Id,
            _model.DisplayName,
            _model.Task,
            provider,
            _labels.Source,
            preprocessMilliseconds,
            inferenceMilliseconds,
            predictions,
            ScoreKind: ScoreKind,
            Threshold: _threshold,
            PostprocessMilliseconds: stopwatch.Elapsed.TotalMilliseconds);
    }

    public void Dispose() => _session.Dispose();

    private IReadOnlyList<PredictionResult> SelectClassification(
        IReadOnlyList<float> rawScores)
    {
        var probabilities = ToSoftmaxIfNeeded(rawScores);
        return SelectRanked(probabilities, 0);
    }

    private IReadOnlyList<PredictionResult> SelectTags(
        IReadOnlyList<float> rawScores)
    {
        return SelectRanked(
            rawScores.Select(value => (double)value).ToArray(),
            _threshold,
            includeBelowThreshold: true);
    }

    private IReadOnlyList<PredictionResult> SelectRanked(
        IReadOnlyList<double> probabilities,
        float threshold,
        bool includeBelowThreshold = false) =>
        SelectLabelScores(probabilities, _labels.Labels, threshold, _maximumPredictions,
            includeBelowThreshold, _deduplicateMappedLabels, _model.BinaryTagMask);

    internal static IReadOnlyList<PredictionResult> SelectLabelScores(
        IReadOnlyList<double> probabilities,
        IReadOnlyList<string> labels,
        float threshold,
        int maximumPredictions,
        bool includeBelowThreshold,
        bool deduplicate,
        bool binaryMask = false)
    {
        if (binaryMask && (probabilities.Count != labels.Count ||
            probabilities.Any(value => value is not (0 or 1))))
            throw new InvalidDataException("Binary tag output must contain exactly one 0/1 value per label.");
        var ranked = probabilities
            .Select((confidence, index) => new
            {
                Index = index,
                Confidence = (float)confidence
            })
            .Where(item =>
                item.Index < labels.Count &&
                (!binaryMask || item.Confidence == 1) &&
                (includeBelowThreshold ||
                    item.Confidence >= threshold) &&
                !string.IsNullOrWhiteSpace(
                    labels[item.Index]))
            .OrderByDescending(item => item.Confidence)
            .Select(item => new PredictionResult(0, labels[item.Index], item.Confidence,
                AboveThreshold: item.Confidence >= threshold));
        var distinct = deduplicate
            ? ranked.DistinctBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            : ranked;
        return distinct
            .Take(maximumPredictions)
            .Select((item, rank) => item with { Rank = rank + 1 })
            .ToArray();
    }

    private IReadOnlyList<PredictionResult> SelectYoloV8(
        IReadOnlyList<float> values,
        IReadOnlyList<int> dimensions,
        ImageTransform transform)
    {
        if (dimensions.Count != 3)
        {
            throw new InvalidDataException(
                $"YOLOv8 output shape [{string.Join(", ", dimensions)}] " +
                "is not supported.");
        }

        var first = dimensions[1];
        var second = dimensions[2];
        var channelsFirst = first <= second;
        var channels = channelsFirst ? first : second;
        var candidates = channelsFirst ? second : first;
        if (channels < 5)
        {
            throw new InvalidDataException(
                "YOLOv8 output does not contain box and class channels.");
        }

        float At(int channel, int candidate) =>
            channelsFirst
                ? values[channel * candidates + candidate]
                : values[candidate * channels + channel];

        var boxes = new List<PredictionResult>();
        for (var candidate = 0; candidate < candidates; candidate++)
        {
            var bestClass = -1;
            var confidence = float.MinValue;
            for (var channel = 4; channel < channels; channel++)
            {
                var score = At(channel, candidate);
                if (score > confidence)
                {
                    confidence = score;
                    bestClass = channel - 4;
                }
            }

            if (confidence < _threshold ||
                bestClass < 0 ||
                bestClass >= _labels.Labels.Count)
            {
                continue;
            }

            var centerX = At(0, candidate);
            var centerY = At(1, candidate);
            var width = At(2, candidate);
            var height = At(3, candidate);
            boxes.Add(CreateBox(
                _labels.Labels[bestClass],
                confidence,
                centerX - width / 2,
                centerY - height / 2,
                centerX + width / 2,
                centerY + height / 2,
                transform));
        }

        return ApplyNms(boxes);
    }

    private IReadOnlyList<PredictionResult> SelectYoloV10(
        IReadOnlyList<float> values,
        IReadOnlyList<int> dimensions,
        ImageTransform transform)
    {
        if (dimensions.Count < 2 || values.Count % 6 != 0)
        {
            throw new InvalidDataException(
                $"YOLOv10 output shape [{string.Join(", ", dimensions)}] " +
                "is not supported.");
        }

        var rows = values.Count / 6;
        var boxes = new List<PredictionResult>();
        for (var row = 0; row < rows; row++)
        {
            var offset = row * 6;
            var confidence = values[offset + 4];
            var labelIndex = (int)values[offset + 5];
            if (confidence < _threshold ||
                labelIndex < 0 ||
                labelIndex >= _labels.Labels.Count)
            {
                continue;
            }

            boxes.Add(CreateBox(
                _labels.Labels[labelIndex],
                confidence,
                values[offset],
                values[offset + 1],
                values[offset + 2],
                values[offset + 3],
                transform));
        }

        return ApplyNms(boxes);
    }

    private PredictionResult CreateBox(
        string label,
        float confidence,
        float left,
        float top,
        float right,
        float bottom,
        ImageTransform transform)
    {
        if (Math.Max(
                Math.Max(Math.Abs(left), Math.Abs(top)),
                Math.Max(Math.Abs(right), Math.Abs(bottom))) <= 2)
        {
            left *= transform.InputWidth;
            right *= transform.InputWidth;
            top *= transform.InputHeight;
            bottom *= transform.InputHeight;
        }

        left = Math.Clamp(
            (left - transform.PaddingX) / transform.Scale,
            0,
            transform.OriginalWidth);
        right = Math.Clamp(
            (right - transform.PaddingX) / transform.Scale,
            0,
            transform.OriginalWidth);
        top = Math.Clamp(
            (top - transform.PaddingY) / transform.Scale,
            0,
            transform.OriginalHeight);
        bottom = Math.Clamp(
            (bottom - transform.PaddingY) / transform.Scale,
            0,
            transform.OriginalHeight);

        return new PredictionResult(
            0,
            label,
            confidence,
            left,
            top,
            Math.Max(0, right - left),
            Math.Max(0, bottom - top));
    }

    private IReadOnlyList<PredictionResult> ApplyNms(
        IReadOnlyList<PredictionResult> boxes)
    {
        var selected = new List<PredictionResult>();
        foreach (var candidate in boxes
            .OrderByDescending(box => box.Confidence))
        {
            if (selected.Any(existing =>
                existing.Label.Equals(
                    candidate.Label,
                    StringComparison.OrdinalIgnoreCase) &&
                IntersectionOverUnion(existing, candidate) > 0.7f))
            {
                continue;
            }

            selected.Add(candidate with { Rank = selected.Count + 1 });
            if (selected.Count >= _maximumPredictions)
            {
                break;
            }
        }

        return selected;
    }

    private static float IntersectionOverUnion(
        PredictionResult first,
        PredictionResult second)
    {
        var left = Math.Max(first.X!.Value, second.X!.Value);
        var top = Math.Max(first.Y!.Value, second.Y!.Value);
        var right = Math.Min(
            first.X.Value + first.Width!.Value,
            second.X.Value + second.Width!.Value);
        var bottom = Math.Min(
            first.Y.Value + first.Height!.Value,
            second.Y.Value + second.Height!.Value);
        var intersection =
            Math.Max(0, right - left) *
            Math.Max(0, bottom - top);
        var union =
            first.Width.Value * first.Height.Value +
            second.Width.Value * second.Height.Value -
            intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static double[] ToSoftmaxIfNeeded(
        IReadOnlyList<float> rawScores)
    {
        var sum = rawScores.Sum(value => (double)value);
        if (rawScores.All(value => value is >= 0 and <= 1) &&
            Math.Abs(sum - 1) < 0.05)
        {
            return rawScores.Select(value => (double)value).ToArray();
        }

        var maximum = rawScores.Max();
        var exponentials = rawScores
            .Select(value => Math.Exp(value - maximum))
            .ToArray();
        var denominator = exponentials.Sum();
        return exponentials
            .Select(value => value / denominator)
            .ToArray();
    }

    private static SessionOptions CreateSessionOptions(
        ExecutionProvider provider)
    {
        var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel =
                GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        if (provider == ExecutionProvider.DirectML)
        {
            options.EnableMemoryPattern = false;
            options.AppendExecutionProvider_DML(0);
        }
        else
        {
            options.AppendExecutionProvider_CPU();
        }

        return options;
    }

    private static (int Width, int Height) ResolveInputSize(
        IReadOnlyList<int> dimensions,
        ModelDefinition model)
    {
        if (dimensions.Count != 4)
        {
            throw new InvalidDataException(
                $"Input shape [{string.Join(", ", dimensions)}] " +
                "is not a four-dimensional image tensor.");
        }

        var width = model.Layout == TensorLayout.Nchw
            ? dimensions[3]
            : dimensions[2];
        var height = model.Layout == TensorLayout.Nchw
            ? dimensions[2]
            : dimensions[1];
        return (
            width > 0 ? width : model.FallbackInputSize,
            height > 0 ? height : model.FallbackInputSize);
    }
}
