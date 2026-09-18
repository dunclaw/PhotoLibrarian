namespace PhotoLibrarian.ModelBench;

public sealed record PredictionResult(
    int Rank,
    string Label,
    float Confidence,
    float? X = null,
    float? Y = null,
    float? Width = null,
    float? Height = null,
    bool AboveThreshold = true);

public sealed record ImageModelResult(
    string ImagePath,
    string RelativeImagePath,
    string ModelId,
    string ModelName,
    ModelTask Task,
    string Provider,
    string LabelSource,
    double PreprocessMilliseconds,
    double InferenceMilliseconds,
    IReadOnlyList<PredictionResult> Predictions,
    string? Error = null,
    string ScoreKind = "confidence",
    float? Threshold = null,
    double PostprocessMilliseconds = 0);

public sealed record ModelRunSummary(
    string ModelId,
    string ModelName,
    string ModelPath,
    string Provider,
    double LoadMilliseconds,
    int SuccessfulImages,
    int FailedImages,
    double AveragePreprocessMilliseconds,
    double AverageInferenceMilliseconds,
    double MedianInferenceMilliseconds,
    double P95InferenceMilliseconds,
    string? Error = null,
    long ModelBytes = 0,
    long AuxiliaryBytes = 0,
    string ScoreKind = "confidence",
    float? Threshold = null,
    double AveragePostprocessMilliseconds = 0,
    string? Precision = null,
    string? ModelSha256 = null,
    string? VocabularySha256 = null);

public sealed record BenchmarkRun(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string ImagesDirectory,
    string ModelsDirectory,
    string OutputDirectory,
    LabelMode LabelMode,
    IReadOnlyList<string> Images,
    IReadOnlyList<ModelRunSummary> Models,
    IReadOnlyList<ImageModelResult> Results,
    string BuildConfiguration = "Unknown");
