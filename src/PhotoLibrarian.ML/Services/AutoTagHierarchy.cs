using System.Collections.Frozen;
using System.Text.Json;

namespace PhotoLibrarian.ML.Services;

internal static class AutoTagHierarchy
{
    public const string Version = "hierarchy-v3";
    private static readonly IReadOnlyDictionary<string, string> Paths = Load();

    internal static string GetPath(string label) =>
        Paths.TryGetValue(label, out var path)
            ? path
            : throw new InvalidDataException($"No automatic-tag hierarchy is defined for '{label}'.");

    internal static IReadOnlyList<TagPrediction> Apply(
        AutoTagModelDefinition profile, IReadOnlyList<TagPrediction> predictions)
    {
        if (!profile.UsesTagHierarchy)
            return predictions;

        return predictions
            .Select(prediction => new TagPrediction(GetPath(prediction.Tag), prediction.Confidence))
            .OrderByDescending(prediction => prediction.Confidence)
            .DistinctBy(prediction => prediction.Tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string> Load()
    {
        using var stream = typeof(AutoTagHierarchy).Assembly.GetManifestResourceStream(
            "PhotoLibrarian.ML.Assets.auto-tag-hierarchy-v3.json")
            ?? throw new InvalidDataException("Automatic-tag hierarchy resource is missing.");
        var paths = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException("Automatic-tag hierarchy is empty.");
        Validate(paths);
        return paths.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    internal static void Validate(IReadOnlyDictionary<string, string> paths)
    {
        if (paths.Count == 0)
            throw new InvalidDataException("Automatic-tag hierarchy is empty.");

        foreach (var (label, path) in paths)
        {
            if (!IsSegment(label) || string.IsNullOrWhiteSpace(path))
                throw new InvalidDataException($"Invalid automatic-tag hierarchy entry for '{label}'.");

            var segments = path.Split('/');
            if (segments.Any(segment => !IsSegment(segment)) ||
                segments.Distinct(StringComparer.Ordinal).Count() != segments.Length ||
                segments[0] == "auto")
                throw new InvalidDataException($"Invalid automatic-tag hierarchy path '{path}'.");

            for (var index = 0; index < segments.Length; index++)
            {
                var prefix = string.Join('/', segments.Take(index + 1));
                if (paths.TryGetValue(segments[index], out var canonical) && canonical != prefix)
                    throw new InvalidDataException($"Inconsistent automatic-tag hierarchy parent '{prefix}'.");
            }
        }
    }

    private static bool IsSegment(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value == value.Trim() &&
        value == value.ToLowerInvariant() &&
        !value.Contains('/') && !value.Contains('\\') &&
        !value.Contains("  ", StringComparison.Ordinal) &&
        !value.Any(char.IsControl);
}
