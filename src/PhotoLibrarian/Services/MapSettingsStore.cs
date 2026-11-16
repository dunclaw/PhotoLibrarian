// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Text.Json;
using PhotoLibrarian.Diagnostics;

namespace PhotoLibrarian.Services;

public enum MapProvider
{
    Google,
    Bing
}

public sealed class MapSettingsStore
{
    private readonly string _path;

    public MapSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoLibrarian", "map-settings.json");
    }

    public MapProvider Load()
    {
        try
        {
            if (!File.Exists(_path))
                return MapProvider.Google;

            var settings = JsonSerializer.Deserialize<MapSettings>(File.ReadAllText(_path));
            if (settings is not null && Enum.IsDefined(settings.Provider))
                return settings.Provider;

            DebugLog.WriteLine("[MAP] Invalid preferences; using Google Maps.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            DebugLog.WriteLine($"[MAP] Could not load preferences; using Google Maps: {ex.Message}");
        }

        return MapProvider.Google;
    }

    public void Save(MapProvider provider)
    {
        if (!Enum.IsDefined(provider))
            throw new ArgumentOutOfRangeException(nameof(provider));

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = _path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new MapSettings { Provider = provider }));
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed record MapSettings
    {
        public MapProvider Provider { get; init; }
    }
}
