using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PhotoLibrarian.Inference;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhotoLibrarian.ML.Services;

/// <summary>
/// Runs local content tagging with catalog profiles selected by the user.
/// Photos never leave the device.
/// </summary>
public sealed class AutoTaggingService : IAutoTagger
{
    private readonly OnnxSessionManager _sessionManager;
    private readonly IAutoTagModelProvider _modelProvider;
    private readonly object _sync = new();
    private readonly Dictionary<string, LoadedProfile> _loadedProfiles = [];

    public AutoTaggingService(
        OnnxSessionManager sessionManager,
        IAutoTagModelProvider modelProvider)
    {
        _sessionManager = sessionManager;
        _modelProvider = modelProvider;
    }

    public void LoadModel(string profileId, string? modelDirectory)
    {
        var definition = AutoTagModelCatalog.ForId(profileId);
        var modelPath = _modelProvider.GetAssetPath(
            profileId,
            definition.ModelAsset,
            modelDirectory);
        var key = Path.GetFullPath(modelPath);

        lock (_sync)
        {
            var labelsPath = _modelProvider.GetAssetPath(
                profileId,
                definition.LabelsAsset,
                modelDirectory);
            ZeroShotModelBundle? bundle = null;
            if (definition.OutputKind == AutoTagOutputKind.CosineEmbedding)
            {
                using var stream = File.OpenRead(labelsPath);
                if (!definition.LabelsAsset.HasExpectedHash(stream))
                    throw new InvalidDataException("TinyCLIP label embeddings failed checksum validation.");
                bundle = ZeroShotModelBundle.Load(labelsPath, "tinyclip");
                TinyClipCalibration.Current.ValidateBundle(bundle);
            }
            var labels = bundle?.Labels.Select(item => item.Label).ToArray() ?? ReadLabels(labelsPath);
            var session = _sessionManager.LoadModelPath(modelPath, definition.UseCpuOnly);
            if (bundle is not null)
            {
                try
                {
                    ValidateEmbeddingSession(session, bundle);
                }
                catch
                {
                    _sessionManager.UnloadModelPath(modelPath);
                    throw;
                }
            }
            if (definition.OutputKind == AutoTagOutputKind.BinaryTagMask &&
                (definition.OutputName is null ||
                 !session.OutputMetadata.TryGetValue(definition.OutputName, out var mask) ||
                 mask.ElementType != typeof(float) ||
                 !mask.Dimensions.SequenceEqual(new[] { 1, labels.Length })))
            {
                _sessionManager.UnloadModelPath(modelPath);
                throw new InvalidDataException("RAM++ must return a named binary tag mask with one value per label.");
            }
            _loadedProfiles[key] = new LoadedProfile(
                session,
                definition,
                labels,
                bundle);
        }
    }

    public Task<IReadOnlyList<TagPrediction>> PredictTagsAsync(
        string imagePath,
        string profileId,
        string? modelDirectory,
        int maximumTags,
        float confidenceThreshold,
        CancellationToken cancellationToken = default) =>
        PredictTagsAsync(
            DecodedImage.WithoutPixels(imagePath),
            profileId,
            modelDirectory,
            maximumTags,
            confidenceThreshold,
            cancellationToken);

    public async Task<IReadOnlyList<TagPrediction>> PredictTagsAsync(
        DecodedImage image,
        string profileId,
        string? modelDirectory,
        int maximumTags,
        float confidenceThreshold,
        CancellationToken cancellationToken = default)
    {
        var imagePath = image.FilePath;
        var definition = AutoTagModelCatalog.ForId(profileId);
        var modelPath = _modelProvider.GetAssetPath(
            profileId,
            definition.ModelAsset,
            modelDirectory);
        LoadedProfile loadedProfile;
        lock (_sync)
        {
            if (!_loadedProfiles.TryGetValue(
                Path.GetFullPath(modelPath),
                out loadedProfile!))
            {
                throw new InvalidOperationException(
                    "The requested automatic-tagging model has not been loaded.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        DenseTensor<float> tensor = definition.PixelNormalization switch
        {
            AutoTagPixelNormalization.Clip when loadedProfile.Bundle is not null =>
                (image.Pixels is { } sharedPixels
                    ? await ZeroShotImagePreprocessor.CreateFromPixelsAsync(
                        sharedPixels.Bgra,
                        sharedPixels.Width,
                        sharedPixels.Height,
                        loadedProfile.Bundle,
                        cancellationToken)
                    : await ZeroShotImagePreprocessor.CreateAsync(
                        imagePath, loadedProfile.Bundle, cancellationToken)).Tensor,
            AutoTagPixelNormalization.ImageNet =>
                await ImagePreprocessor.PreprocessImageAsync(
                    imagePath,
                    definition.InputSize,
                    ImagePreprocessor.ImageNetMean,
                    ImagePreprocessor.ImageNetStd,
                    cancellationToken),
            AutoTagPixelNormalization.EfficientNetLite =>
                await ImagePreprocessor.PreprocessImageNhwcAsync(
                    imagePath,
                    definition.InputSize,
                    127f,
                    128f,
                    cancellationToken),
            AutoTagPixelNormalization.Unit
                when definition.TensorLayout ==
                    AutoTagTensorLayout.Nchw &&
                    definition.PreserveAspectRatio =>
                await ImagePreprocessor
                    .PreprocessImageUnitNchwLetterboxAsync(
                        imagePath,
                        definition.InputSize,
                        cancellationToken),
            _ => throw new InvalidOperationException(
                "Unsupported model preprocessing configuration.")
        };

        cancellationToken.ThrowIfCancellationRequested();
        var inputName = loadedProfile.Session.InputNames[0];
        var output = _sessionManager.RunInference(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var results = loadedProfile.Session.Run(
                [ImagePreprocessor.CreateInput(inputName, tensor)]);
            var outputName = loadedProfile.Bundle?.OutputName ?? definition.OutputName;
            return (outputName is null ? results.First() :
                results.Single(result => result.Name == outputName))
                .AsTensor<float>().ToArray();
        });
        cancellationToken.ThrowIfCancellationRequested();
        var predictions = loadedProfile.Bundle is not null
            ? TinyClipCalibration.Current.SelectPredictions(
                loadedProfile.Bundle,
                output,
                maximumTags,
                confidenceThreshold)
            : SelectPredictions(
                output,
                loadedProfile.Labels,
                confidenceThreshold,
                Math.Clamp(maximumTags, 1, definition.MaximumSupportedTags),
                definition.SafeLabelMappings,
                definition.OutputKind);
        return AutoTagHierarchy.Apply(definition, predictions);
    }

    internal static IReadOnlyList<TagPrediction> SelectPredictions(
        IReadOnlyList<float> rawScores,
        IReadOnlyList<string> labels,
        float confidenceThreshold,
        int maximumTags,
        IReadOnlyDictionary<string, string>? safeLabelMappings = null,
        AutoTagOutputKind outputKind =
            AutoTagOutputKind.SingleLabelClassification)
    {
        var binaryMask = outputKind == AutoTagOutputKind.BinaryTagMask;
        if (binaryMask && (rawScores.Count != labels.Count ||
            rawScores.Any(score => score is not (0 or 1))))
            throw new InvalidDataException("Binary tag output must contain exactly one 0/1 value per label.");
        if (rawScores.Count == 0 || maximumTags <= 0)
        {
            return [];
        }

        var scores = rawScores.Select(score => (double)score).ToArray();
        if (outputKind ==
            AutoTagOutputKind.SingleLabelClassification)
        {
            var sum = scores.Sum();
            var areProbabilities =
                scores.All(score => score is >= 0 and <= 1) &&
                Math.Abs(sum - 1) < 0.05;
            if (!areProbabilities)
            {
                var max = scores.Max();
                var denominator = scores.Sum(
                    score => Math.Exp(score - max));
                for (var index = 0; index < scores.Length; index++)
                {
                    scores[index] =
                        Math.Exp(scores[index] - max) / denominator;
                }
            }
        }

        return scores
            .Select((score, index) => new
            {
                Score = (float)score,
                Index = index
            })
            .Where(item =>
                item.Index < labels.Count &&
                (!binaryMask || item.Score == 1) &&
                item.Score >= confidenceThreshold)
            .OrderByDescending(item => item.Score)
            .Select(item => new
            {
                Tag = MapSafeLabel(
                    NormalizeLabel(labels[item.Index]),
                    safeLabelMappings),
                item.Score
            })
            .Where(item => item.Tag is not null)
            .Select(item => new TagPrediction(item.Tag!, item.Score))
            .DistinctBy(
                prediction => prediction.Tag,
                StringComparer.OrdinalIgnoreCase)
            .Take(maximumTags)
            .ToArray();
    }

    internal static string[] ReadLabels(string labelsPath)
    {
        var content = File.ReadAllText(labelsPath);
        try
        {
            var labelMap =
                JsonSerializer.Deserialize<Dictionary<string, string>>(
                    content);
            if (labelMap is not null)
            {
                return Enumerable.Range(0, labelMap.Count)
                    .Select(index =>
                        labelMap.GetValueOrDefault(index.ToString()) ??
                        string.Empty)
                    .ToArray();
            }
        }
        catch (JsonException)
        {
        }

        var matches = Regex.Matches(
            content,
            @"['""]?(?<index>\d+)['""]?\s*[:=]\s*(?<quote>['""])(?<label>(?:\\.|.)*?)\k<quote>(?=\s*(?:[,}\]\r\n]|$))",
            RegexOptions.CultureInvariant);
        if (matches.Count == 0)
        {
            throw new InvalidDataException(
                "The automatic-tag label file is invalid.");
        }

        var indexed = new SortedDictionary<int, string>();
        foreach (Match match in matches)
        {
            if (int.TryParse(
                match.Groups["index"].Value,
                out var index))
            {
                indexed[index] = match.Groups["label"].Value
                    .Replace("\\'", "'")
                    .Replace("\\\"", "\"")
                    .Replace("\\\\", "\\");
            }
        }

        var labels = Enumerable
            .Repeat(string.Empty, indexed.Keys.Max() + 1)
            .ToArray();
        foreach (var (index, label) in indexed)
        {
            labels[index] = label;
        }

        return labels;
    }

    private static string? MapSafeLabel(
        string normalizedLabel,
        IReadOnlyDictionary<string, string>? safeLabelMappings)
    {
        if (safeLabelMappings is null)
        {
            return normalizedLabel;
        }

        return safeLabelMappings.TryGetValue(
            normalizedLabel,
            out var safeLabel)
            ? safeLabel
            : null;
    }

    private static string NormalizeLabel(string label)
    {
        var preferredName = label.Split(',', 2)[0]
            .Replace('_', ' ')
            .Replace('/', ' ')
            .Trim();
        return string.Join(
            ' ',
            preferredName.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }

    private sealed record LoadedProfile(
        InferenceSession Session,
        AutoTagModelDefinition Definition,
        string[] Labels,
        ZeroShotModelBundle? Bundle = null);

    private static void ValidateEmbeddingSession(InferenceSession session, ZeroShotModelBundle bundle)
    {
        if (session.InputNames.Count != 1 || session.InputNames[0] != bundle.InputName ||
            !session.OutputMetadata.TryGetValue(bundle.OutputName, out var output))
            throw new InvalidDataException("TinyCLIP model input/output names do not match the profile.");
        var input = session.InputMetadata[bundle.InputName];
        var dimensions = input.Dimensions;
        int[] expected = [1, 3, bundle.InputSize, bundle.InputSize];
        if (input.ElementType != typeof(float) || dimensions.Length != 4 ||
            dimensions.Where((dimension, index) => dimension > 0 && dimension != expected[index]).Any() ||
            output.ElementType != typeof(float) || output.Dimensions.Length != 2 ||
            output.Dimensions[0] > 1 || output.Dimensions[1] is not (512 or -1))
            throw new InvalidDataException("TinyCLIP tensor dimensions do not match the tested image encoder.");
    }
}

public sealed record TagPrediction(string Tag, float Confidence);
