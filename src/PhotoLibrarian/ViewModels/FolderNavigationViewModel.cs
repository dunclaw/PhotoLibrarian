using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Diagnostics;
using System.Collections.ObjectModel;

namespace PhotoLibrarian.ViewModels;

public partial class FolderNavigationViewModel : ObservableObject
{
    private readonly CacheDatabase _db;
    private readonly FolderScannerService _scanner;
    private readonly LibraryIndexingService _indexingService;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _indexCts;

    public ObservableCollection<FolderNode> RootFolders { get; } = [];

    [ObservableProperty]
    public partial FolderNode? SelectedFolder { get; set; }

    public FolderNavigationViewModel(
        CacheDatabase db,
        FolderScannerService scanner,
        LibraryIndexingService indexingService,
        MainViewModel main)
    {
        _db = db;
        _scanner = scanner;
        _indexingService = indexingService;
        _main = main;
    }

    public async Task LoadWatchedFoldersAsync()
    {
        RootFolders.Clear();
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, path, include_sub FROM watched_folders ORDER BY path";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var path = reader.GetString(1);
            var node = new FolderNode
            {
                Id = reader.GetInt64(0),
                Path = path,
                Name = System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path,
                IncludeSubfolders = reader.GetInt64(2) == 1
            };
            BuildChildNodes(node);
            RootFolders.Add(node);
        }
    }

    public static void BuildChildNodes(FolderNode parent)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(parent.Path))
            {
                var child = new FolderNode
                {
                    Path = dir,
                    Name = System.IO.Path.GetFileName(dir)
                };
                // Only one level deep initially; expand on demand
                if (Directory.GetDirectories(dir).Length > 0)
                    child.Children.Add(new FolderNode { Name = "Loading…", Path = "" }); // placeholder
                parent.Children.Add(child);
            }
        }
        catch { /* Access denied, etc. */ }
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        DebugLog.WriteLine("AddFolderAsync: Starting folder picker");
        
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
        picker.FileTypeFilter.Add("*");

        // Initialize picker with window handle
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            DebugLog.WriteLine("AddFolderAsync: User cancelled");
            return;
        }

        DebugLog.WriteLine($"AddFolderAsync: User selected '{folder.Path}'");

        // Insert into watched_folders
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO watched_folders (path, include_sub)
            VALUES ($path, 1)
            """;
        cmd.Parameters.AddWithValue("$path", folder.Path);
        var rowsAffected = await cmd.ExecuteNonQueryAsync();
        DebugLog.WriteLine($"AddFolderAsync: Inserted into DB (rows affected: {rowsAffected})");

        await LoadWatchedFoldersAsync();

        // Start indexing in background
        DebugLog.WriteLine($"AddFolderAsync: Starting background indexing for '{folder.Path}'");
        _indexCts?.Cancel();
        _indexCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                DebugLog.WriteLine($"AddFolderAsync [Background]: Calling IndexFolderAsync");
                await _indexingService.IndexFolderAsync(folder.Path, true, _indexCts.Token);
                DebugLog.WriteLine($"AddFolderAsync [Background]: IndexFolderAsync completed");
                App.MainWindow?.DispatcherQueue.TryEnqueue(async () =>
                {
                    DebugLog.WriteLine($"AddFolderAsync [UI]: Calling RefreshAfterIndexAsync");
                    await _main.RefreshAfterIndexAsync();
                });
            }
            catch (Exception ex)
            {
                DebugLog.WriteLine($"AddFolderAsync [Background]: ERROR - {ex.Message}\n{ex.StackTrace}");
            }
        });
    }

    [RelayCommand]
    private async Task RemoveFolderAsync()
    {
        if (SelectedFolder is null) return;

        // Find the root folder for the selected folder
        FolderNode? rootToRemove = null;
        if (SelectedFolder.Id > 0)
        {
            // This is a root folder
            rootToRemove = SelectedFolder;
        }
        else
        {
            // This is a subfolder - find its root
            foreach (var root in RootFolders)
            {
                if (SelectedFolder.Path.StartsWith(root.Path, StringComparison.OrdinalIgnoreCase))
                {
                    rootToRemove = root;
                    break;
                }
            }
        }

        if (rootToRemove is null || rootToRemove.Id <= 0) return;

        using var conn = _db.CreateConnection();

        // Remove images from this folder
        using var delImages = conn.CreateCommand();
        delImages.CommandText = "DELETE FROM images WHERE file_path LIKE $prefix || '%'";
        delImages.Parameters.AddWithValue("$prefix", rootToRemove.Path);
        await delImages.ExecuteNonQueryAsync();

        // Remove watched folder
        using var delFolder = conn.CreateCommand();
        delFolder.CommandText = "DELETE FROM watched_folders WHERE id = $id";
        delFolder.Parameters.AddWithValue("$id", rootToRemove.Id);
        await delFolder.ExecuteNonQueryAsync();

        _scanner.StopWatching(rootToRemove.Path);
        await LoadWatchedFoldersAsync();
        await _main.RefreshAfterIndexAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        DebugLog.WriteLine("RefreshAsync: Starting refresh");
        _indexCts?.Cancel();
        _indexCts = new CancellationTokenSource();

        foreach (var folder in RootFolders)
        {
            DebugLog.WriteLine($"RefreshAsync: Starting background indexing for '{folder.Path}'");
            _ = Task.Run(async () =>
            {
                try
                {
                    DebugLog.WriteLine($"RefreshAsync [Background]: Calling IndexFolderAsync for '{folder.Path}'");
                    await _indexingService.IndexFolderAsync(folder.Path, folder.IncludeSubfolders, _indexCts.Token);
                    DebugLog.WriteLine($"RefreshAsync [Background]: IndexFolderAsync completed for '{folder.Path}'");
                    App.MainWindow?.DispatcherQueue.TryEnqueue(async () =>
                    {
                        await _main.RefreshAfterIndexAsync();
                    });
                }
                catch (Exception ex)
                {
                    DebugLog.WriteLine($"RefreshAsync [Background]: ERROR - {ex.Message}");
                }
            });
        }
        
        DebugLog.WriteLine("RefreshAsync: All background tasks started");
    }

    partial void OnSelectedFolderChanged(FolderNode? value)
    {
        DebugLog.WriteLine($"OnSelectedFolderChanged: value={value?.Path ?? "null"}");
        if (value is not null && value.Path.Length > 0)
        {
            // Expand lazy children
            if (value.Children.Count == 1 && value.Children[0].Path == "")
            {
                value.Children.Clear();
                BuildChildNodes(value);
            }

            // Filter image grid to this folder
            DebugLog.WriteLine($"OnSelectedFolderChanged: Calling FilterByFolderAsync with path='{value.Path}'");
            _ = _main.ImageGrid.FilterByFolderAsync(value.Path);
        }
    }
}

public partial class FolderNode : ObservableObject
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public required string Path { get; set; }
    public bool IncludeSubfolders { get; set; }
    public ObservableCollection<FolderNode> Children { get; } = [];

    public override string ToString() => Name;
}
