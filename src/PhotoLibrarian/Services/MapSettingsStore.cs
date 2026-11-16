// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Text.Json;
using PhotoLibrarian.Diagnostics;
using PhotoLibrarian.Core.Services;

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
        _path = path ?? AppDataPaths.SettingsFile("map-settings.json");
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

        AppSettingsFile.WriteAllText(
            _path,
            JsonSerializer.Serialize(new MapSettings { Provider = provider }));
    }

    private sealed record MapSettings
    {
        public MapProvider Provider { get; init; }
    }
}
