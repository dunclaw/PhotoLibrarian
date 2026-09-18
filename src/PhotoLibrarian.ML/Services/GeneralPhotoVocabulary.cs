using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PhotoLibrarian.ML.Services;

internal sealed class GeneralPhotoVocabulary
{
    public static GeneralPhotoVocabulary Current { get; } = Load();
    public int SchemaVersion { get; init; }
    public string TaxonomyVersion { get; init; } = "";
    public string RamLabelAssetSha256 { get; init; } = "";
    public string VocabularySha256 { get; init; } = "";
    public string[] RamLabels { get; init; } = [];
    public string[] Labels { get; init; } = [];
    public Dictionary<string, string> RamLabelMappings { get; init; } = [];
    public Dictionary<string, string> ExcludedLabels { get; init; } = [];
    public IReadOnlyDictionary<string, string> AllowedRamLabels { get; private set; } =
        FrozenDictionary<string, string>.Empty;

    private static GeneralPhotoVocabulary Load()
    {
        using var stream = typeof(GeneralPhotoVocabulary).Assembly.GetManifestResourceStream(
            "PhotoLibrarian.ML.Assets.general-photo-profile-v2.json")
            ?? throw new InvalidDataException("General photo vocabulary resource is missing.");
        var profile = JsonSerializer.Deserialize<GeneralPhotoVocabulary>(stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("General photo vocabulary is empty.");
        var json = JsonSerializer.Serialize(profile.Labels, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var labels = profile.Labels.ToHashSet(StringComparer.Ordinal);
        var ramLabels = profile.RamLabels.ToHashSet(StringComparer.Ordinal);
        if (profile.SchemaVersion != 1 || profile.TaxonomyVersion != AutoTagHierarchy.Version ||
            profile.RamLabelAssetSha256 != "f76e8c8ccc9d2b1bedece326953a4f1002f91da1391ab5440eb031c4b2f9c244" ||
            profile.RamLabels.Length != 4585 || ramLabels.Count != 4585 ||
            labels.Count != profile.Labels.Length || labels.Count == 0 ||
            !fingerprint.Equals(profile.VocabularySha256, StringComparison.OrdinalIgnoreCase) ||
            profile.RamLabelMappings.Count == 0 ||
            profile.RamLabelMappings.Any(pair => !ramLabels.Contains(pair.Key) ||
                !labels.Contains(pair.Value) || profile.ExcludedLabels.ContainsKey(pair.Key)) ||
            profile.ExcludedLabels.Any(pair => string.IsNullOrWhiteSpace(pair.Value)))
            throw new InvalidDataException("General photo vocabulary identity or eligibility is invalid.");

        foreach (var label in profile.RamLabels.Concat(profile.Labels))
            AutoTagHierarchy.GetPath(label);
        profile.AllowedRamLabels = profile.RamLabelMappings.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        return profile;
    }
}
