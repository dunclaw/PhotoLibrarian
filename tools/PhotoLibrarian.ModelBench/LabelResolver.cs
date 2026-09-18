using Microsoft.ML.OnnxRuntime;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhotoLibrarian.ModelBench;

public sealed record ResolvedLabels(
    IReadOnlyList<string> Labels,
    string Source);

public static partial class LabelResolver
{
    public static ResolvedLabels Resolve(
        InferenceSession session,
        ModelDefinition model,
        string modelsDirectory,
        LabelMode labelMode)
    {
        if (labelMode == LabelMode.Mapped &&
            model.MappingFileName is not null)
        {
            var mappingPath = Path.Combine(
                modelsDirectory,
                model.MappingFileName);
            if (File.Exists(mappingPath))
            {
                return new ResolvedLabels(
                    Parse(File.ReadAllText(mappingPath)),
                    Path.GetFileName(mappingPath));
            }
        }

        if (model.LabelsFileName is not null)
        {
            var labelsPath = Path.Combine(
                modelsDirectory,
                model.LabelsFileName);
            if (File.Exists(labelsPath))
            {
                return new ResolvedLabels(
                    Parse(File.ReadAllText(labelsPath)),
                    Path.GetFileName(labelsPath));
            }
        }

        var metadata = session.ModelMetadata.CustomMetadataMap;
        foreach (var key in new[] { "names", "labels" })
        {
            if (metadata.TryGetValue(key, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return new ResolvedLabels(
                    Parse(value),
                    $"ONNX metadata '{key}'");
            }
        }

        throw new InvalidDataException(
            $"No labels were found for {model.DisplayName}. " +
            "Supply its label file or use a model containing label metadata.");
    }

    public static IReadOnlyList<string> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        if (TryParseJson(content, out var jsonLabels))
        {
            return jsonLabels;
        }

        var indexed = new SortedDictionary<int, string>();
        foreach (Match match in IndexedLabelRegex().Matches(content))
        {
            if (int.TryParse(match.Groups["index"].Value, out var index))
            {
                indexed[index] = Normalize(match.Groups["label"].Value);
            }
        }

        if (indexed.Count > 0)
        {
            return ToIndexedList(indexed);
        }

        return content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(label => label.Length > 0)
            .ToArray();
    }

    private static bool TryParseJson(
        string content,
        out IReadOnlyList<string> labels)
    {
        try
        {
            var dictionary =
                JsonSerializer.Deserialize<Dictionary<string, string>>(
                    content);
            if (dictionary is not null)
            {
                var indexed = dictionary
                    .Where(pair => int.TryParse(pair.Key, out _))
                    .ToDictionary(
                        pair => int.Parse(pair.Key),
                        pair => Normalize(pair.Value));
                if (indexed.Count > 0)
                {
                    labels = ToIndexedList(indexed);
                    return true;
                }
            }
        }
        catch (JsonException)
        {
        }

        try
        {
            var array = JsonSerializer.Deserialize<string[]>(content);
            if (array is not null)
            {
                labels = array.Select(Normalize).ToArray();
                return true;
            }
        }
        catch (JsonException)
        {
        }

        labels = [];
        return false;
    }

    private static IReadOnlyList<string> ToIndexedList(
        IReadOnlyDictionary<int, string> indexed)
    {
        var labels = Enumerable
            .Repeat(string.Empty, indexed.Keys.Max() + 1)
            .ToArray();
        foreach (var (index, label) in indexed)
        {
            labels[index] = label;
        }

        return labels;
    }

    private static string Normalize(string value) =>
        value.Trim()
            .Trim('"', '\'', '{', '}', '[', ']', ',')
            .Replace("\\'", "'")
            .Replace("\\\"", "\"")
            .Replace("\\\\", "\\")
            .Replace('_', ' ')
            .Trim();

    [GeneratedRegex(
        @"['""]?(?<index>\d+)['""]?\s*[:=]\s*(?<quote>['""])(?<label>(?:\\.|.)*?)\k<quote>(?=\s*(?:[,}\]\r\n]|$))",
        RegexOptions.CultureInvariant)]
    private static partial Regex IndexedLabelRegex();
}
