using System.Text.Json;

namespace PhotoLibrarian.ML.Services;

public sealed class AutoTaggingSettingsStore
{
    private readonly string _settingsPath;
    private readonly object _sync = new();

    public AutoTaggingSettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoLibrarian",
            "auto-tagging.json");
    }

    public AutoTaggingSettings Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return new AutoTaggingSettings();
                }

                using var document = JsonDocument.Parse(
                    File.ReadAllText(_settingsPath));
                return Normalize(
                    document.RootElement.TryGetProperty(
                        nameof(AutoTaggingSettings.ProfileId),
                        out _)
                        ? document.RootElement.Deserialize<AutoTaggingSettings>()
                        : MigrateLegacySettings(document.RootElement));
            }
            catch (IOException)
            {
                return new AutoTaggingSettings();
            }
            catch (UnauthorizedAccessException)
            {
                return new AutoTaggingSettings();
            }
            catch (JsonException)
            {
                return new AutoTaggingSettings();
            }
            catch (ArgumentException)
            {
                return new AutoTaggingSettings();
            }
            catch (NotSupportedException)
            {
                return new AutoTaggingSettings();
            }
        }
    }

    public void Save(AutoTaggingSettings settings)
    {
        lock (_sync)
        {
            var normalized = Normalize(settings);
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _settingsPath + ".tmp";
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(
                        normalized,
                        new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporaryPath, _settingsPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    private static AutoTaggingSettings MigrateLegacySettings(
        JsonElement root)
    {
        var isEnabled =
            root.TryGetProperty("IsEnabled", out var enabledElement) &&
            enabledElement.ValueKind is JsonValueKind.True;
        var profileId = AutoTagModelCatalog.MobileNetV2ProfileId;
        if (root.TryGetProperty("Mode", out var modeElement))
        {
            var isDetailed =
                modeElement.ValueKind == JsonValueKind.Number &&
                modeElement.TryGetInt32(out var numericMode) &&
                numericMode == 1;
            isDetailed |=
                modeElement.ValueKind == JsonValueKind.String &&
                string.Equals(
                    modeElement.GetString(),
                    "Detailed",
                    StringComparison.OrdinalIgnoreCase);
            if (isDetailed)
            {
                profileId = AutoTagModelCatalog.EfficientNetLite4ProfileId;
            }
        }

        var definition = AutoTagModelCatalog.ForId(profileId);
        return new AutoTaggingSettings(
            isEnabled,
            profileId,
            definition.DefaultMaximumTags,
            definition.DefaultConfidenceThreshold);
    }

    private static AutoTaggingSettings Normalize(
        AutoTaggingSettings? settings)
    {
        if (settings is null ||
            !AutoTagModelCatalog.TryGet(
                settings.ProfileId,
                out var definition))
        {
            return new AutoTaggingSettings();
        }

        var qualityStatuses =
            settings.ProfileQualityStatuses?
                .Where(entry =>
                    AutoTagModelCatalog.Profiles.Any(profile =>
                        profile.ApprovalKey.Equals(
                            entry.Key,
                            StringComparison.Ordinal)) &&
                    Enum.IsDefined(entry.Value))
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value,
                    StringComparer.Ordinal)
            ?? new Dictionary<string, AutoTagQualityStatus>(
                StringComparer.Ordinal);

        return settings with
        {
            MaximumTags = Math.Clamp(
                settings.MaximumTags,
                1,
                definition.MaximumSupportedTags),
            ConfidenceThreshold = definition.UsesFixedThresholds
                ? definition.DefaultConfidenceThreshold
                : definition.Id == AutoTagModelCatalog.TinyClipProfileId
                    ? Math.Clamp(
                        settings.ConfidenceThreshold,
                        AutoTagModelCatalog.TinyClipOpenThreshold,
                        AutoTagModelCatalog.TinyClipConservativeThreshold)
                    : Math.Clamp(settings.ConfidenceThreshold, 0.01f, 1f),
            ModelDirectory = string.IsNullOrWhiteSpace(
                settings.ModelDirectory)
                ? null
                : Path.GetFullPath(settings.ModelDirectory),
            ProfileQualityStatuses = qualityStatuses
        };
    }
}
