using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Diagnostics;
using System.Collections.ObjectModel;

namespace PhotoLibrarian.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly CacheDatabase _db;
    private readonly ImageRepository _imageRepo;
    private readonly FolderScannerService _scanner;
    private readonly MetadataReaderService _metadataReader;
    private readonly LibraryIndexingService _indexingService;
    private readonly OriginalBackupService _backupService;
    private CancellationTokenSource? _indexingCts;

    public FolderNavigationViewModel FolderNav { get; }
    public DateNavigationViewModel DateNav { get; }
    public TagNavigationViewModel TagNav { get; }
    public ImageGridViewModel ImageGrid { get; }
    public ImageViewerViewModel ImageViewer { get; }
    public ImageEditorViewModel ImageEditor { get; }
    public MetadataPanelViewModel MetadataPanel { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial bool IsIndexing { get; set; }

    [ObservableProperty]
    public partial int TotalImages { get; set; }

    public MainViewModel(
        CacheDatabase db,
        ImageRepository imageRepo,
        TagRepository tagRepo,
        FolderScannerService scanner,
        MetadataReaderService metadataReader,
        LibraryIndexingService indexingService,
        OriginalBackupService backupService)
    {
        _db = db;
        _imageRepo = imageRepo;
        _scanner = scanner;
        _metadataReader = metadataReader;
        _indexingService = indexingService;
        _backupService = backupService;

        StatusText = "Ready";

        FolderNav = new FolderNavigationViewModel(db, scanner, indexingService, this);
        DateNav = new DateNavigationViewModel(imageRepo);
        TagNav = new TagNavigationViewModel(tagRepo);
        ImageGrid = new ImageGridViewModel(imageRepo, scanner, metadataReader, this);
        ImageViewer = new ImageViewerViewModel();
        ImageEditor = new ImageEditorViewModel(backupService);
        MetadataPanel = new MetadataPanelViewModel();
        Settings = new SettingsViewModel(db);

        _indexingService.Progress += OnIndexingProgress;
    }

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        await FolderNav.LoadWatchedFoldersAsync();
        
        // Don't load images on startup - wait for user to select a folder
        // This prevents showing all 535 indexed images in random order
        TotalImages = await _imageRepo.GetCountAsync();
        StatusText = TotalImages > 0 ? "Select a folder to view photos" : "Add folders to get started";

        // Start background indexing to populate metadata (tags, dates)
        StartBackgroundIndexing();
    }
    
    [RelayCommand]
    public async Task RunBenchmarkAsync()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("=== STARTING WINUI BENCHMARK ===");
            StatusText = "Running benchmark...";
            
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Task.Run(async () =>
            {
                try
                {
                    await Services.BenchmarkService.RunWICBenchmark();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Benchmark error: {ex.Message}");
                }
            });
            sw.Stop();
            
            StatusText = $"Benchmark complete in {sw.ElapsedMilliseconds}ms - check debug output";
            System.Diagnostics.Debug.WriteLine($"=== BENCHMARK COMPLETE: {sw.ElapsedMilliseconds}ms ===");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Benchmark failed: {ex.Message}");
            StatusText = $"Benchmark failed: {ex.Message}";
        }
    }

    public void PauseBackgroundIndexing()
    {
        DebugLog.WriteLine("MainViewModel: Pausing background indexing");
        _indexingCts?.Cancel();
        _indexingCts = null;
    }

    public void StartBackgroundIndexing()
    {
        if (FolderNav.RootFolders.Count == 0) return;

        _indexingCts?.Cancel();
        _indexingCts = new CancellationTokenSource();
        var ct = _indexingCts.Token;

        _ = Task.Run(async () =>
        {
            // Wait a bit before starting to let UI settle
            await Task.Delay(2000, ct);
            
            DebugLog.WriteLine($"StartBackgroundIndexing: Starting scan of {FolderNav.RootFolders.Count} folders");

            foreach (var folder in FolderNav.RootFolders)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    DebugLog.WriteLine($"  Indexing folder: {folder.Path}");
                    await _indexingService.IndexFolderAsync(folder.Path, folder.IncludeSubfolders, ct);
                    DebugLog.WriteLine($"  Completed folder: {folder.Path}");
                    
                    if (!ct.IsCancellationRequested)
                    {
                        App.MainWindow?.DispatcherQueue.TryEnqueue(async () =>
                        {
                            await RefreshAfterIndexAsync();
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    DebugLog.WriteLine($"  Indexing canceled");
                    break;
                }
                catch (Exception ex)
                {
                    DebugLog.WriteLine($"  ERROR indexing {folder.Path}: {ex.Message}");
                    // Continue with next folder on error
                }
            }
            
            DebugLog.WriteLine($"StartBackgroundIndexing: All folders complete");
        }, ct);
    }

    private void OnIndexingProgress(object? sender, IndexingProgressEventArgs e)
    {
        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            IsIndexing = !e.IsComplete;
            StatusText = e.IsComplete
                ? $"Indexed {e.Processed:N0} new items ({e.Skipped:N0} unchanged)"
                : $"Indexing… {e.Processed:N0} processed";
        });
    }

    public async Task RefreshAfterIndexAsync()
    {
        await ImageGrid.LoadImagesAsync();
        TotalImages = await _imageRepo.GetCountAsync();
        StatusText = $"{TotalImages:N0} items";
        
        // Refresh date and tag navigation data
        await DateNav.LoadDatesAsync();
        await TagNav.LoadTagsAsync();
        
        // Update UI trees on main thread
        App.MainWindow?.DispatcherQueue.TryEnqueue(async () =>
        {
            if (App.MainWindow is MainWindow window)
            {
                await window.RefreshMetadataTreesAsync();
            }
        });
    }

    public void Cleanup()
    {
        DebugLog.WriteLine("MainViewModel: Cleanup - canceling background tasks");
        _indexingCts?.Cancel();
        _indexingCts?.Dispose();
        _scanner.Dispose();
    }
}
