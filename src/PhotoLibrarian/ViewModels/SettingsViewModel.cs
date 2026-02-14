using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;

namespace PhotoLibrarian.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly CacheDatabase _db;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial int ThumbnailCacheSizeMB { get; set; }

    [ObservableProperty]
    public partial string CacheLocation { get; set; }

    public SettingsViewModel(CacheDatabase db)
    {
        _db = db;

        CacheLocation = "";
    }

    [RelayCommand]
    private void Open()
    {
        IsOpen = true;
        LoadSettings();
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
    }

    private void LoadSettings()
    {
        // Get database file size
        try
        {
            var dbPath = GetDatabasePath();
            CacheLocation = dbPath;
            if (File.Exists(dbPath))
            {
                var info = new FileInfo(dbPath);
                ThumbnailCacheSizeMB = (int)(info.Length / (1024 * 1024));
            }
        }
        catch { }
    }

    [RelayCommand]
    private async Task RebuildCacheAsync()
    {
        // Clear all thumbnails and re-index
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM thumbnails";
        await cmd.ExecuteNonQueryAsync();

        // Vacuum to reclaim space
        using var vacuum = conn.CreateCommand();
        vacuum.CommandText = "VACUUM";
        await vacuum.ExecuteNonQueryAsync();

        LoadSettings();
    }

    private static string GetDatabasePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "PhotoLibrarian", "cache.db");
    }
}
