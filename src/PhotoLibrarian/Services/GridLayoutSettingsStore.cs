// Copyright (c) Duncan Lawler. All Rights Reserved.

using PhotoLibrarian.Diagnostics;
using PhotoLibrarian.ViewModels;
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
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoLibrarian",
            "grid-layout.json");
    }

    public GridLayoutSettings Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_settingsPath))
                    return new GridLayoutSettings();

                var settings = JsonSerializer.Deserialize<GridLayoutSettings>(
                    File.ReadAllText(_settingsPath));
                if (settings is null)
                {
                    DebugLog.WriteLine("[GRID LAYOUT] Empty preferences; using defaults.");
                    return new GridLayoutSettings();
                }

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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
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
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temporaryPath = _settingsPath + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(
                    settings, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporaryPath, _settingsPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }
}
