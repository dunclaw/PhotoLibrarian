using System.Diagnostics;

namespace PhotoLibrarian.ModelBench;

public static class ModelBenchApplication
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg",
            ".jpeg",
            ".png",
            ".bmp",
            ".gif",
            ".tif",
            ".tiff",
            ".webp"
        };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(argument =>
            argument.Equals("--list-models", StringComparison.OrdinalIgnoreCase)))
        {
            PrintModels();
            return 0;
        }

        BenchmarkOptions options;
        try
        {
            options = BenchmarkOptions.Parse(args);
        }
        catch (HelpRequestedException)
        {
            Console.WriteLine(BenchmarkOptions.Usage);
            return 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(BenchmarkOptions.Usage);
            return 2;
        }

        if (!Directory.Exists(options.ImagesDirectory))
        {
            Console.Error.WriteLine(
                $"Image folder does not exist: {options.ImagesDirectory}");
            return 2;
        }

        if (!Directory.Exists(options.ModelsDirectory))
        {
            Console.Error.WriteLine(
                $"Model folder does not exist: {options.ModelsDirectory}");
            return 2;
        }

        var images = DiscoverImages(options);
        if (images.Count == 0)
        {
            Console.Error.WriteLine(
                "No supported images were found in the selected folder.");
            return 2;
        }

        Directory.CreateDirectory(options.OutputDirectory);
        Console.WriteLine(
            $"Comparing {options.ModelIds.Count} model(s) against " +
            $"{images.Count} image(s).");
        Console.WriteLine($"Provider: {options.Provider}");
        Console.WriteLine($"Labels: {options.LabelMode}");
        Console.WriteLine();

        var startedAt = DateTimeOffset.Now;
        var results = new List<ImageModelResult>();
        var summaries = new List<ModelRunSummary>();
        foreach (var model in ModelCatalog.All.Where(model =>
            options.ModelIds.Contains(model.Id)))
        {
            await RunModelAsync(
                model,
                images,
                options,
                results,
                summaries);
        }

        var run = new BenchmarkRun(
            startedAt,
            DateTimeOffset.Now,
            options.ImagesDirectory,
            options.ModelsDirectory,
            options.OutputDirectory,
            options.LabelMode,
            images,
            summaries,
            results,
            System.Reflection.CustomAttributeExtensions.GetCustomAttribute<
                System.Reflection.AssemblyConfigurationAttribute>(
                    typeof(ModelBenchApplication).Assembly)?.Configuration ?? "Unknown");
        await BenchmarkReportWriter.WriteAsync(run);

        Console.WriteLine();
        Console.WriteLine(
            $"Visual report: {Path.Combine(options.OutputDirectory, "report.html")}");
        Console.WriteLine(
            $"Scorecard: {Path.Combine(options.OutputDirectory, "scorecard.csv")}");
        Console.WriteLine(
            $"Prediction detail: {Path.Combine(options.OutputDirectory, "predictions.csv")}");

        var failedModels = summaries.Count(summary =>
            summary.Error is not null);
        var failedImages = results.Count(result => result.Error is not null);
        if (failedModels > 0 || failedImages > 0)
        {
            Console.Error.WriteLine(
                $"Completed with {failedModels} model failure(s) and " +
                $"{failedImages} image failure(s). See the report for details.");
            return 1;
        }

        return 0;
    }

    private static async Task RunModelAsync(
        ModelDefinition model,
        IReadOnlyList<string> images,
        BenchmarkOptions options,
        ICollection<ImageModelResult> results,
        ICollection<ModelRunSummary> summaries)
    {
        var modelPath = Path.Combine(
            options.ModelsDirectory,
            model.ModelFileName);
        Console.WriteLine($"[{model.Id}] {model.DisplayName}");
        if (!File.Exists(modelPath))
        {
            var error = $"Model file not found: {modelPath}";
            Console.Error.WriteLine($"  Skipped: {error}");
            summaries.Add(new ModelRunSummary(
                model.Id,
                model.DisplayName,
                modelPath,
                options.Provider.ToString(),
                0,
                0,
                images.Count,
                0,
                0,
                0,
                0,
                error));
            return;
        }

        OnnxModelRunner? runner = null;
        double loadMilliseconds = 0;
        try
        {
            runner = new OnnxModelRunner(
                model,
                modelPath,
                options.ModelsDirectory,
                options,
                out loadMilliseconds);
            modelPath = runner.ModelPath;
            Console.WriteLine(
                $"  Loaded in {loadMilliseconds:N0} ms; labels: " +
                runner.LabelSource);
            if (options.WarmUp)
            {
                Console.Write("  Warming up...");
                _ = runner.Run(
                    images[0],
                    Path.GetRelativePath(
                        options.ImagesDirectory,
                        images[0]),
                    options.Provider.ToString());
                Console.WriteLine(" done");
            }

            var successful = 0;
            var failed = 0;
            for (var index = 0; index < images.Count; index++)
            {
                var imagePath = images[index];
                var relativePath = Path.GetRelativePath(
                    options.ImagesDirectory,
                    imagePath);
                Console.Write(
                    $"  {index + 1,3}/{images.Count}: {relativePath} ... ");
                try
                {
                    var result = runner.Run(
                        imagePath,
                        relativePath,
                        options.Provider.ToString());
                    results.Add(result);
                    successful++;
                    Console.WriteLine(
                        $"{result.Predictions.Count} result(s), " +
                        $"{result.InferenceMilliseconds:N1} ms");
                }
                catch (Exception exception)
                {
                    failed++;
                    results.Add(new ImageModelResult(
                        imagePath,
                        relativePath,
                        model.Id,
                        model.DisplayName,
                        model.Task,
                        options.Provider.ToString(),
                        runner.LabelSource,
                        0,
                        0,
                        [],
                        exception.Message));
                    Console.Error.WriteLine($"failed: {exception.Message}");
                }
            }

            var successfulResults = results
                .Where(result =>
                    result.ModelId == model.Id &&
                    result.Error is null)
                .ToArray();
            var inferenceTimes = successfulResults
                .Select(result => result.InferenceMilliseconds)
                .Order()
                .ToArray();
            summaries.Add(new ModelRunSummary(
                model.Id,
                model.DisplayName,
                modelPath,
                options.Provider.ToString(),
                loadMilliseconds,
                successful,
                failed,
                successfulResults.Length == 0
                    ? 0
                    : successfulResults.Average(result =>
                        result.PreprocessMilliseconds),
                successfulResults.Length == 0
                    ? 0
                    : successfulResults.Average(result =>
                        result.InferenceMilliseconds),
                Percentile(inferenceTimes, 0.50),
                Percentile(inferenceTimes, 0.95),
                ModelBytes: runner.ModelBytes,
                AuxiliaryBytes: runner.AuxiliaryBytes,
                ScoreKind: runner.ScoreKind,
                Threshold: runner.Threshold,
                AveragePostprocessMilliseconds: successfulResults.Length == 0
                ? 0
                : successfulResults.Average(result =>
                    result.PostprocessMilliseconds),
                Precision: runner.Precision,
                ModelSha256: runner.ModelSha256,
                VocabularySha256: runner.VocabularySha256));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"  Failed: {exception.Message}");
            summaries.Add(new ModelRunSummary(
                model.Id,
                model.DisplayName,
                modelPath,
                options.Provider.ToString(),
                loadMilliseconds,
                0,
                images.Count,
                0,
                0,
                0,
                0,
                exception.Message));
        }
        finally
        {
            runner?.Dispose();
        }

        await Task.Yield();
    }

    private static double Percentile(
        IReadOnlyList<double> sortedValues,
        double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(
            percentile * sortedValues.Count) - 1;
        return sortedValues[Math.Clamp(index, 0, sortedValues.Count - 1)];
    }

    private static IReadOnlyList<string> DiscoverImages(
        BenchmarkOptions options)
    {
        var searchOption = options.Recursive
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;
        return Directory
            .EnumerateFiles(
                options.ImagesDirectory,
                "*",
                searchOption)
            .Where(path =>
                SupportedExtensions.Contains(
                    Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(options.ImageLimit)
            .ToArray();
    }

    private static void PrintModels()
    {
        foreach (var model in ModelCatalog.All)
        {
            Console.WriteLine(
                $"{model.Id,-16} {model.DisplayName,-30} " +
                model.ModelFileName);
        }
    }
}
