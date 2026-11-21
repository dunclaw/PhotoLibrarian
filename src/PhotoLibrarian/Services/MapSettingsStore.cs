// Copyright (c) Duncan Lawler. All Rights Reserved.

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
            var result = AppSettingsFile.ReadJson<MapSettings>(_path);
            if (result.Status == AppSettingsReadStatus.Missing)
                return MapProvider.Google;

            if (result.Value is { } settings && Enum.IsDefined(settings.Provider))
            {
                if (result.Status == AppSettingsReadStatus.RecoveredFromBackup)
                    DebugLog.WriteLine("[MAP] Recovered preferences from backup.");
                return settings.Provider;
            }

            DebugLog.WriteLine("[MAP] Invalid preferences; using Google Maps.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.WriteLine($"[MAP] Could not load preferences; using Google Maps: {ex.Message}");
        }

        return MapProvider.Google;
    }

    public void Save(MapProvider provider)
    {
        if (!Enum.IsDefined(provider))
            throw new ArgumentOutOfRangeException(nameof(provider));

        AppSettingsFile.WriteJson(_path, new MapSettings { Provider = provider });
    }

    private sealed record MapSettings
    {
        public MapProvider Provider { get; init; }
    }
}
