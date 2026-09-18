using System.Text.Json;
using PhotoLibrarian.Inference;

namespace PhotoLibrarian.ML.Services;

internal sealed class TinyClipCalibration
{
    public const string ModelHash = "9aa0c0c8d4abdb4f80e8f26e74236bb1bf13d43ffaab4e17fb03e48c4e01be0d";
    public static string VocabularyHash => GeneralPhotoVocabulary.Current.VocabularySha256;
    public static TinyClipCalibration Current { get; } = Load();

    public string ModelId { get; init; } = "";
    public string Provider { get; init; } = "";
    public string ScoreKind { get; init; } = "";
    public string Precision { get; init; } = "";
    public string ModelSha256 { get; init; } = "";
    public string VocabularySha256 { get; init; } = "";
    public int CandidateLimit { get; init; }
    public float FallbackThreshold { get; init; }
    public Dictionary<string, float?> Thresholds { get; init; } = [];
    public string[] Labels { get; init; } = [];

    private static TinyClipCalibration Load()
    {
        using var stream = typeof(TinyClipCalibration).Assembly.GetManifestResourceStream(
            "PhotoLibrarian.ML.Assets.tinyclip-calibration-general-v2-coverage.json")
            ?? throw new InvalidDataException("TinyCLIP calibration resource is missing.");
        var profile = JsonSerializer.Deserialize<TinyClipCalibration>(stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("TinyCLIP calibration is empty.");
        if (profile.ModelId != "tinyclip" || profile.Provider != "Cpu" ||
            profile.ScoreKind != "cosine" || profile.Precision != "int8" ||
            profile.ModelSha256 != ModelHash || profile.VocabularySha256 != VocabularyHash ||
            profile.CandidateLimit != 10 || !float.IsFinite(profile.FallbackThreshold) ||
            profile.FallbackThreshold is < 0 or > 1 ||
            !profile.Labels.SequenceEqual(GeneralPhotoVocabulary.Current.Labels) ||
            profile.Labels.Contains("beak") || !profile.Labels.Contains("bird") ||
            profile.Thresholds.Any(pair => !profile.Labels.Contains(pair.Key) ||
                pair.Value is float value && (!float.IsFinite(value) || value is < 0 or > 1)))
        {
            throw new InvalidDataException("TinyCLIP calibration configuration is invalid.");
        }
        return profile;
    }

    public void ValidateBundle(ZeroShotModelBundle bundle)
    {
        if (bundle.ModelId != ModelId || bundle.ModelSha256 != ModelHash ||
            bundle.VocabularySha256 != VocabularyHash || bundle.Precision != Precision ||
            bundle.ScoreKind != ScoreKind || bundle.InputSize != 224 ||
            bundle.InputName != "pixel_values" || bundle.OutputName != "image_embeds" ||
            bundle.ResizeMode != "shortest-center-crop" || bundle.Interpolation != "cubic" ||
            !bundle.NormalizationMean.SequenceEqual(new[] { 0.48145466f, 0.4578275f, 0.40821073f }) ||
            !bundle.NormalizationStd.SequenceEqual(new[] { 0.26862954f, 0.26130258f, 0.27577711f }) ||
            !bundle.Labels.Select(label => label.Label).SequenceEqual(Labels) ||
            bundle.Labels.Any(label => label.Embedding.Length != 512))
        {
            throw new InvalidDataException("TinyCLIP assets do not match the calibrated CPU vocabulary and preprocessing.");
        }
    }

    public IReadOnlyList<TagPrediction> SelectPredictions(
        ZeroShotModelBundle bundle,
        IReadOnlyList<float> embedding,
        int maximumTags,
        float? fallbackThreshold = null)
    {
        var configuredFallback = fallbackThreshold ?? FallbackThreshold;
        if (!float.IsFinite(configuredFallback) || configuredFallback is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(fallbackThreshold));
        var candidates = bundle.Predict(embedding, CandidateLimit, configuredFallback);
        return candidates
            .Where(item => IsAccepted(item.Label, item.Confidence, configuredFallback))
            .Take(Math.Clamp(maximumTags, 1, CandidateLimit))
            .Select(item => new TagPrediction(item.Label, item.Confidence))
            .ToArray();
    }

    internal bool IsAccepted(string label, float score, float? fallbackThreshold = null)
    {
        if (!Labels.Contains(label, StringComparer.Ordinal))
            return false;
        var configuredFallback = fallbackThreshold ?? FallbackThreshold;
        if (!float.IsFinite(configuredFallback) || configuredFallback is < 0 or > 1)
            return false;
        var calibratedCutoff = Thresholds.TryGetValue(label, out var value)
            ? value
            : FallbackThreshold;
        var adjustment = configuredFallback - FallbackThreshold;
        float? cutoff = calibratedCutoff.HasValue
            ? adjustment == 0
                ? calibratedCutoff.Value
                : Math.Clamp(calibratedCutoff.Value + adjustment, 0, 1)
            : null;
        return cutoff.HasValue && float.IsFinite(score) && score >= cutoff.Value;
    }
}
