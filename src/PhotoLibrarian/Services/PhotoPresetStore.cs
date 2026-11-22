// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Diagnostics;

namespace PhotoLibrarian.Services;

public sealed record PhotoPreset(
    string Id,
    string Name,
    EditSettingGroup Groups,
    EditParameters Parameters,
    string? SourceIdentity,
    bool IsFavorite);

public enum PhotoPresetCatalogStatus
{
    Loaded,
    Missing,
    Invalid,
    UnsupportedVersion
}

public sealed record PhotoPresetCatalogResult(
    PhotoPresetCatalogStatus Status,
    IReadOnlyList<PhotoPreset> Presets,
    string? Message = null);

public sealed class PhotoPresetStore
{
    private sealed record CatalogDocument(int Version, List<PhotoPreset> Presets);

    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly object _sync = new();

    public PhotoPresetStore(string? path = null)
    {
        _path = path ?? AppDataPaths.PresetCatalogFile;
    }

    public PhotoPresetCatalogResult Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_path))
                return new(PhotoPresetCatalogStatus.Missing, []);

            try
            {
                var document = JsonSerializer.Deserialize<CatalogDocument>(
                    File.ReadAllText(_path), JsonOptions)
                    ?? throw new InvalidDataException("The preset catalog is empty.");
                if (document.Version != CurrentVersion)
                    return new(PhotoPresetCatalogStatus.UnsupportedVersion, [],
                        $"Preset catalog version {document.Version} is not supported.");
                ValidatePresets(document.Presets);
                return new(PhotoPresetCatalogStatus.Loaded, document.Presets);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                DebugLog.WriteLine($"[PRESETS] Could not load the preset catalog: {exception.Message}");
                return new(PhotoPresetCatalogStatus.Invalid, [],
                    $"The preset catalog could not be loaded: {exception.Message}");
            }
        }
    }

    public IReadOnlyList<PhotoPreset> Save(PhotoPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ValidatePreset(preset);
        lock (_sync)
        {
            var loaded = Load();
            if (loaded.Status is not (PhotoPresetCatalogStatus.Loaded or PhotoPresetCatalogStatus.Missing))
                throw new InvalidDataException(loaded.Message);
            if (loaded.Presets.Any(item => string.Equals(item.Id, preset.Id, StringComparison.Ordinal)))
                throw new InvalidOperationException("A preset with this identifier already exists.");
            var presets = loaded.Presets.Append(preset).ToList();
            Write(presets);
            return presets;
        }
    }

    public IReadOnlyList<PhotoPreset> SetFavorite(string id, bool isFavorite)
    {
        lock (_sync)
        {
            var loaded = Load();
            if (loaded.Status != PhotoPresetCatalogStatus.Loaded)
                throw new InvalidDataException(loaded.Message ?? "The preset catalog is not available.");
            var presets = loaded.Presets.ToList();
            var index = presets.FindIndex(item => item.Id == id);
            if (index < 0) throw new KeyNotFoundException("The preset no longer exists.");
            presets[index] = presets[index] with { IsFavorite = isFavorite };
            Write(presets);
            return presets;
        }
    }

    public IReadOnlyList<PhotoPreset> Delete(string id)
    {
        lock (_sync)
        {
            var loaded = Load();
            if (loaded.Status != PhotoPresetCatalogStatus.Loaded)
                throw new InvalidDataException(loaded.Message ?? "The preset catalog is not available.");
            var presets = loaded.Presets.Where(item => item.Id != id).ToList();
            if (presets.Count == loaded.Presets.Count)
                throw new KeyNotFoundException("The preset no longer exists.");
            Write(presets);
            return presets;
        }
    }

    private void Write(List<PhotoPreset> presets)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The preset catalog path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                new CatalogDocument(CurrentVersion, presets), JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ValidatePresets(IReadOnlyList<PhotoPreset>? presets)
    {
        if (presets is null) throw new InvalidDataException("The preset list is missing.");
        if (presets.Any(preset => preset is null))
            throw new InvalidDataException("The preset list contains a missing entry.");
        if (presets.Select(preset => preset.Id).Distinct(StringComparer.Ordinal).Count() != presets.Count)
            throw new InvalidDataException("Preset identifiers must be unique.");
        foreach (var preset in presets) ValidatePreset(preset);
    }

    private static void ValidatePreset(PhotoPreset preset)
    {
        if (!Guid.TryParseExact(preset.Id, "N", out _) ||
            string.IsNullOrWhiteSpace(preset.Name) ||
            preset.Name != preset.Name.Trim())
            throw new InvalidDataException("A preset must have a valid identifier and a non-empty trimmed name.");
        if (preset.Groups == EditSettingGroup.None ||
            (preset.Groups & ~EditSettingGroup.All) != 0)
            throw new InvalidDataException("A preset must select supported setting groups.");
        if (preset.Parameters is null)
            throw new InvalidDataException("The preset recipe is missing.");
        if (!Enum.IsDefined(preset.Parameters.Effect) ||
            !Enum.IsDefined(preset.Parameters.SharpeningMode))
            throw new InvalidDataException("The preset contains an unsupported discrete setting.");
        if (preset.Groups.HasFlag(EditSettingGroup.Retouch) &&
            string.IsNullOrWhiteSpace(preset.SourceIdentity))
            throw new InvalidDataException("A preset containing retouch strokes must include its source identity.");
        preset.Parameters.ToneCurves.Validate();
        preset.Parameters.ColorAdjustments.Validate();
        SpotRetouchProcessor.Validate(preset.Parameters.RetouchStrokes);
        if (preset.Groups.HasFlag(EditSettingGroup.RawDevelopment))
            RawToneMappingService.ValidateRecipe(preset.Parameters);
        var selected = EditRecipeComposer.SelectGroups(preset.Parameters, preset.Groups);
        if (!EditRecipeComposer.ContentEquals(preset.Parameters, selected))
            throw new InvalidDataException("The preset contains values outside its selected setting groups.");
    }
}
