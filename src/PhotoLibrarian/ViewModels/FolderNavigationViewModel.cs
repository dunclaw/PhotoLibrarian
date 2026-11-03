// Copyright (c) Duncan Lawler. All Rights Reserved.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Windows.ApplicationModel.Resources;
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
    private readonly SemaphoreSlim _removeGate = new(1, 1);
    private static readonly ResourceLoader Resources = new(
        Path.Combine(AppContext.BaseDirectory, "PhotoLibrarian.pri"), "Resources");

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
        var folders = await new WatchedFolderRepository(_db).GetAllAsync();
        RootFolders.Clear();
        foreach (var folder in folders)
        {
            var path = folder.Path;
            var node = new FolderNode
            {
                Id = folder.Id,
                Path = path,
                Name = path, // Show full path for root folders
                IncludeSubfolders = folder.IncludeSubfolders,
                IsRootFolder = true
            };
            BuildChildNodes(node);
            RootFolders.Add(node);
        }
        _main.SyncWatchedFolders();
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
        var ct = _indexCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                DebugLog.WriteLine($"AddFolderAsync [Background]: Calling IndexFolderAsync");
                await _indexingService.IndexFolderAsync(folder.Path, true, ct);
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
                if (WatchedFolderScope.ContainsFile(
                    Path.Combine(SelectedFolder.Path, "_"), root.Path, root.IncludeSubfolders))
                {
                    rootToRemove = root;
                    break;
                }
            }
        }

        if (rootToRemove is null || rootToRemove.Id <= 0) return;

        await RemoveWatchedFolderAsync(rootToRemove);
    }

    public async Task RemoveWatchedFolderAsync(FolderNode folder)
    {
        await _removeGate.WaitAsync();
        try
        {
            _main.PauseBackgroundIndexing();
            await _main.PauseRecognitionForFolderChangeAsync();
            _indexCts?.Cancel();
            _main.ImageGrid.InvalidateWatchedFolders();
            await new WatchedFolderRepository(_db).RemoveAsync(folder.Id);
            _scanner.StopWatching(folder.Path);
            if (SelectedFolder is not null &&
                WatchedFolderScope.ContainsFile(
                    Path.Combine(SelectedFolder.Path, "_"), folder.Path, true))
                SelectedFolder = null;
            await LoadWatchedFoldersAsync();
            if (_main.ImageViewer.CurrentEntry is { } current &&
                !RootFolders.Any(root => WatchedFolderScope.ContainsFile(
                    current.FilePath, root.Path, root.IncludeSubfolders)))
                _main.ImageViewer.CloseCommand.Execute(null);
            await _main.RefreshAfterIndexAsync();
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"RemoveWatchedFolderAsync failed: {ex}");
            _main.StatusText = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Resources.GetString("WatchedFolderUpdateFailed"), ex.Message);
        }
        finally
        {
            _main.ResumeRecognitionAfterFolderChange();
            _removeGate.Release();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        DebugLog.WriteLine("RefreshAsync: Starting refresh");
        _indexCts?.Cancel();
        _indexCts = new CancellationTokenSource();
        var ct = _indexCts.Token;

        foreach (var folder in RootFolders)
        {
            DebugLog.WriteLine($"RefreshAsync: Starting background indexing for '{folder.Path}'");
            _ = Task.Run(async () =>
            {
                try
                {
                    DebugLog.WriteLine($"RefreshAsync [Background]: Calling IndexFolderAsync for '{folder.Path}'");
                    await _indexingService.IndexFolderAsync(folder.Path, folder.IncludeSubfolders, ct);
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
    public bool IsRootFolder { get; set; }
    public ObservableCollection<FolderNode> Children { get; } = [];

    public string DisplayName => IsRootFolder ? Name : System.IO.Path.GetFileName(Path);
    
    // Use Windows Explorer folder icon
    public string IconGlyph => IsRootFolder ? "\uE8B7" : "\uE8D5"; // Library for root, folder for children
    
    public Windows.UI.Text.FontWeight FontWeight => IsRootFolder 
        ? new Windows.UI.Text.FontWeight { Weight = 600 } // SemiBold
        : new Windows.UI.Text.FontWeight { Weight = 400 }; // Normal
    
    public Microsoft.UI.Xaml.Visibility ShowPathVisibility => IsRootFolder 
        ? Microsoft.UI.Xaml.Visibility.Visible 
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    public override string ToString() => DisplayName;
}
