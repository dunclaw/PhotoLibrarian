using System.Diagnostics;

namespace PhotoLibrarian.ML.Services;

public sealed class AutoTagBenchmarkProcessor
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"
        };

    private readonly IAutoTagModelProvider _modelProvider;
    private readonly IAutoTagger _autoTagger;

    public AutoTagBenchmarkProcessor(
        IAutoTagModelProvider modelProvider,
        IAutoTagger autoTagger)
    {
        _modelProvider = modelProvider;
        _autoTagger = autoTagger;
    }

    public async Task<AutoTagBenchmarkResult> RunAsync(
        string folderPath,
        AutoTaggingSettings settings,
        int maximumImages = 50,
        CancellationToken cancellationToken = default)
    {
        if (maximumImages is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumImages),
                "A benchmark must contain between 1 and 50 images.");
        }

        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException(
                $"Benchmark folder not found: {folderPath}");
        }

        AutoTagModelCatalog.ForId(settings.ProfileId);
        var imagePaths = Directory
            .EnumerateFiles(
                folderPath,
                "*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false
                })
            .Where(path => SupportedExtensions.Contains(
                Path.GetExtension(path)))
            .Take(maximumImages)
            .ToArray();

        var stopwatch = Stopwatch.StartNew();
        await _modelProvider.EnsureModelAsync(
            settings.ProfileId,
            settings.ModelDirectory,
            cancellationToken);
        _autoTagger.LoadModel(
            settings.ProfileId,
            settings.ModelDirectory);

        var examples = new List<AutoTagBenchmarkExample>(
            imagePaths.Length);
        var processed = 0;
        var failed = 0;
        foreach (var imagePath in imagePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var predictions =
                    await _autoTagger.PredictTagsAsync(
                        imagePath,
                        settings.ProfileId,
                        settings.ModelDirectory,
                        settings.MaximumTags,
                        settings.ConfidenceThreshold,
                        cancellationToken);
                processed++;
                examples.Add(new AutoTagBenchmarkExample(
                    Path.GetFileName(imagePath),
                    predictions,
                    null));
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                examples.Add(new AutoTagBenchmarkExample(
                    Path.GetFileName(imagePath),
                    [],
                    exception.Message));
            }
        }

        stopwatch.Stop();
        return new AutoTagBenchmarkResult(
            stopwatch.Elapsed,
            processed,
            failed,
            examples);
    }
}

public sealed record AutoTagBenchmarkResult(
    TimeSpan Elapsed,
    int Processed,
    int Failed,
    IReadOnlyList<AutoTagBenchmarkExample> Examples);

public sealed record AutoTagBenchmarkExample(
    string FileName,
    IReadOnlyList<TagPrediction> Predictions,
    string? Error);
