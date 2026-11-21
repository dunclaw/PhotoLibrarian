// Copyright (c) Duncan Lawler. All Rights Reserved.

using PhotoLibrarian.Diagnostics;
using PhotoLibrarian.Core.Services;
using System.Text.Json;

namespace PhotoLibrarian.Services;

public sealed record GridLayoutSettings
{
    public GroupByOption GroupBy { get; init; } = GroupByOption.None;
    public SortByOption SortBy { get; init; } = SortByOption.DateTaken;
    public bool SortDescending { get; init; } = true;
}

public sealed class GridLayoutSettingsStore
{
    private readonly string _settingsPath;
    private readonly object _sync = new();

    public GridLayoutSettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? AppDataPaths.SettingsFile("grid-layout.json");
    }

    public GridLayoutSettings Load()
    {
        lock (_sync)
        {
            try
            {
                var result = AppSettingsFile.ReadJson<GridLayoutSettings>(_settingsPath);
                if (result.Status == AppSettingsReadStatus.Missing)
                    return new GridLayoutSettings();

                if (result.Value is not { } settings)
                {
                    DebugLog.WriteLine("[GRID LAYOUT] Invalid preferences; using defaults.");
                    return new GridLayoutSettings();
                }

                if (result.Status == AppSettingsReadStatus.RecoveredFromBackup)
                    DebugLog.WriteLine("[GRID LAYOUT] Recovered preferences from backup.");

                var groupIsValid = Enum.IsDefined(settings.GroupBy);
                var sortIsValid = Enum.IsDefined(settings.SortBy);
                if (!groupIsValid || !sortIsValid)
                    DebugLog.WriteLine("[GRID LAYOUT] Unknown layout option; using its default.");

                return settings with
                {
                    GroupBy = groupIsValid ? settings.GroupBy : GroupByOption.None,
                    SortBy = sortIsValid ? settings.SortBy : SortByOption.DateTaken
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DebugLog.WriteLine($"[GRID LAYOUT] Could not load preferences; using defaults: {ex.Message}");
                return new GridLayoutSettings();
            }
        }
    }

    public void Save(GridLayoutSettings settings)
    {
        lock (_sync)
        {
            AppSettingsFile.WriteJson(
                _settingsPath,
                settings,
                new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
