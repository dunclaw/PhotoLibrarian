using System.Globalization;

namespace PhotoLibrarian.ModelBench;

public enum ExecutionProvider
{
    Cpu,
    DirectML
}

public enum LabelMode
{
    Raw,
    Mapped
}

public sealed record BenchmarkOptions(
    string ImagesDirectory,
    string ModelsDirectory,
    string OutputDirectory,
    IReadOnlySet<string> ModelIds,
    ExecutionProvider Provider,
    LabelMode LabelMode,
    int MaximumPredictions,
    float? ConfidenceThreshold,
    int ImageLimit,
    bool Recursive,
    bool WarmUp)
{
    public static string Usage =>
        """
        PhotoLibrarian.ModelBench

        Runs local ONNX models against the same images and creates:
          report.html      visual side-by-side scoring report
          scorecard.csv    one editable row per image/model
          predictions.csv  one editable row per prediction
          results.json     complete machine-readable results

        Required:
          --images <folder>       Folder containing the controlled image set
          --models-dir <folder>   Folder containing the ONNX models and labels

        Optional:
          --output <folder>       Output folder (default: .\model-bench-output)
          --models <ids>          Comma-separated IDs or "all" (default: all)
          --provider <value>      cpu or directml (default: cpu)
          --labels <value>        raw or mapped (default: raw)
          --top <number>          Maximum predictions per model (default: 10)
          --threshold <0..1>      Override each model's confidence threshold
          --limit <number>        Maximum images (default: 50)
          --no-recursive          Do not search image subfolders
          --no-warmup             Do not discard a warm-up inference per model
          --list-models           Print supported model IDs
          --help                  Print this help

        Supported model IDs:
          efficientnet, yolov8-cls, yolov8-detect, yolov10-detect,
          joytag, ram-plus, siglip2, tinyclip

        SigLIP 2 and TinyCLIP require prepared image-encoder/label-embedding
        bundles (prepare_zero_shot.py). Their thresholds are provisional;
        TinyCLIP returns cosine similarity, not a probability.
        """;

    public static BenchmarkOptions Parse(string[] args)
    {
        string? imagesDirectory = null;
        string? modelsDirectory = null;
        var outputDirectory = Path.GetFullPath("model-bench-output");
        IReadOnlySet<string> modelIds = new HashSet<string>(
            ModelCatalog.All.Select(model => model.Id),
            StringComparer.OrdinalIgnoreCase);
        var provider = ExecutionProvider.Cpu;
        var labelMode = LabelMode.Raw;
        var maximumPredictions = 10;
        float? confidenceThreshold = null;
        var imageLimit = 50;
        var recursive = true;
        var warmUp = true;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument.ToLowerInvariant())
            {
                case "--images":
                    imagesDirectory = ReadValue(args, ref index, argument);
                    break;
                case "--models-dir":
                    modelsDirectory = ReadValue(args, ref index, argument);
                    break;
                case "--output":
                    outputDirectory = Path.GetFullPath(
                        ReadValue(args, ref index, argument));
                    break;
                case "--models":
                    modelIds = ParseModelIds(
                        ReadValue(args, ref index, argument));
                    break;
                case "--provider":
                    provider = ParseProvider(
                        ReadValue(args, ref index, argument));
                    break;
                case "--labels":
                    labelMode = ParseLabelMode(
                        ReadValue(args, ref index, argument));
                    break;
                case "--top":
                    maximumPredictions = ParseInteger(
                        ReadValue(args, ref index, argument),
                        argument,
                        1,
                        100);
                    break;
                case "--threshold":
                    confidenceThreshold = ParseThreshold(
                        ReadValue(args, ref index, argument));
                    break;
                case "--limit":
                    imageLimit = ParseInteger(
                        ReadValue(args, ref index, argument),
                        argument,
                        1,
                        10_000);
                    break;
                case "--no-recursive":
                    recursive = false;
                    break;
                case "--no-warmup":
                    warmUp = false;
                    break;
                case "--help":
                case "-h":
                case "/?":
                case "--list-models":
                    throw new HelpRequestedException(argument);
                default:
                    throw new ArgumentException(
                        $"Unknown argument '{argument}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(imagesDirectory))
        {
            throw new ArgumentException("--images is required.");
        }

        if (string.IsNullOrWhiteSpace(modelsDirectory))
        {
            throw new ArgumentException("--models-dir is required.");
        }

        return new BenchmarkOptions(
            Path.GetFullPath(imagesDirectory),
            Path.GetFullPath(modelsDirectory),
            outputDirectory,
            modelIds,
            provider,
            labelMode,
            maximumPredictions,
            confidenceThreshold,
            imageLimit,
            recursive,
            warmUp);
    }

    private static string ReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string argument)
    {
        index++;
        if (index >= args.Count || args[index].StartsWith("--"))
        {
            throw new ArgumentException(
                $"{argument} requires a value.");
        }

        return args[index];
    }

    private static IReadOnlySet<string> ParseModelIds(string value)
    {
        if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return new HashSet<string>(
                ModelCatalog.All.Select(model => model.Id),
                StringComparer.OrdinalIgnoreCase);
        }

        var ids = value.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (ids.Length == 0)
        {
            throw new ArgumentException(
                "--models must contain at least one model ID.");
        }

        var knownIds = ModelCatalog.All
            .Select(model => model.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownIds = ids.Where(id => !knownIds.Contains(id)).ToArray();
        if (unknownIds.Length > 0)
        {
            throw new ArgumentException(
                $"Unknown model ID(s): {string.Join(", ", unknownIds)}.");
        }

        return ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static ExecutionProvider ParseProvider(string value) =>
        value.ToLowerInvariant() switch
        {
            "cpu" => ExecutionProvider.Cpu,
            "directml" => ExecutionProvider.DirectML,
            _ => throw new ArgumentException(
                "--provider must be 'cpu' or 'directml'.")
        };

    private static LabelMode ParseLabelMode(string value) =>
        value.ToLowerInvariant() switch
        {
            "raw" => LabelMode.Raw,
            "mapped" => LabelMode.Mapped,
            _ => throw new ArgumentException(
                "--labels must be 'raw' or 'mapped'.")
        };

    private static int ParseInteger(
        string value,
        string argument,
        int minimum,
        int maximum)
    {
        if (!int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var result) ||
            result < minimum ||
            result > maximum)
        {
            throw new ArgumentException(
                $"{argument} must be between {minimum} and {maximum}.");
        }

        return result;
    }

    private static float ParseThreshold(string value)
    {
        if (!float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var threshold) ||
            !float.IsFinite(threshold) || threshold is < 0 or > 1)
        {
            throw new ArgumentException(
                "--threshold must be between 0 and 1.");
        }

        return threshold;
    }
}

public sealed class HelpRequestedException(string argument) : Exception
{
    public string Argument { get; } = argument;
}
