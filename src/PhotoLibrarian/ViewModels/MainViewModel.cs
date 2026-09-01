using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Diagnostics;
using PhotoLibrarian.ML.Services;
using System.Collections.ObjectModel;

namespace PhotoLibrarian.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly CacheDatabase _db;
    private readonly ImageRepository _imageRepo;
    private readonly TagRepository _tagRepo;
    private readonly FolderScannerService _scanner;
    private readonly MetadataReaderService _metadataReader;
    private readonly LibraryIndexingService _indexingService;
    private readonly OriginalBackupService _backupService;
    private readonly FaceLibraryProcessor _faceProcessor;
    private readonly IDisposable _faceResources;
    private CancellationTokenSource? _indexingCts;
    private CancellationTokenSource? _faceDetectionCts;
    private Task? _faceDetectionTask;
    private bool _faceDetectionEnabled = true;
    private bool _faceRescanRequested;

    public FolderNavigationViewModel FolderNav { get; }
    public DateNavigationViewModel DateNav { get; }
    public TagNavigationViewModel TagNav { get; }
    public FlagNavigationViewModel FlagNav { get; }
    public ImageGridViewModel ImageGrid { get; }
    public ImageViewerViewModel ImageViewer { get; }
    public ImageEditorViewModel ImageEditor { get; }
    public MetadataPanelViewModel MetadataPanel { get; }
    public SettingsViewModel Settings { get; }
    public Services.PhotoOperationsService PhotoOps { get; }
    public OriginalBackupService BackupService => _backupService;

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial bool IsIndexing { get; set; }

    [ObservableProperty]
    public partial bool IsFaceDetectionRunning { get; set; }

    [ObservableProperty]
    public partial int TotalImages { get; set; }

    public MainViewModel(
        CacheDatabase db,
        ImageRepository imageRepo,
        TagRepository tagRepo,
        FaceRepository faceRepo,
        FolderScannerService scanner,
        MetadataReaderService metadataReader,
        LibraryIndexingService indexingService,
        OriginalBackupService backupService,
        FaceLibraryProcessor faceProcessor,
        IDisposable faceResources)
    {
        _db = db;
        _imageRepo = imageRepo;
        _tagRepo = tagRepo;
        _scanner = scanner;
        _metadataReader = metadataReader;
        _indexingService = indexingService;
        _backupService = backupService;
        _faceProcessor = faceProcessor;
        _faceResources = faceResources;

        StatusText = "Ready";

        FolderNav = new FolderNavigationViewModel(db, scanner, indexingService, this);
        DateNav = new DateNavigationViewModel(imageRepo);
        TagNav = new TagNavigationViewModel(tagRepo);
        FlagNav = new FlagNavigationViewModel(imageRepo);
        ImageGrid = new ImageGridViewModel(
            imageRepo,
            tagRepo,
            faceRepo,
            scanner,
            metadataReader,
            this);
        ImageViewer = new ImageViewerViewModel();
        ImageEditor = new ImageEditorViewModel(backupService);
        MetadataPanel = new MetadataPanelViewModel();
        MetadataPanel.Initialize(imageRepo, tagRepo, this);
        Settings = new SettingsViewModel(db);
        PhotoOps = new Services.PhotoOperationsService(imageRepo);

        _indexingService.Progress += OnIndexingProgress;
        _faceProcessor.Progress += OnFaceProcessingProgress;
        ImageViewer.CurrentEntryChanged += OnViewerEntryChanged;
        ImageEditor.EditsApplied += OnEditsApplied;
        ImageEditor.Reverted += OnEditsReverted;
    }

    private async void OnEditsReverted(object? sender, string filePath)
    {
        await RefreshFileAsync(filePath);
        StatusText = $"Reverted {System.IO.Path.GetFileName(filePath)} to the original";
    }

    /// <summary>Repaints the grid thumbnail and the open viewer after a file changed on disk.</summary>
    public async Task RefreshFileAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        try
        {
            await ImageGrid.RefreshSingleImageAsync(filePath);
            await ImageViewer.ReloadCurrentImageAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EDIT] RefreshFileAsync failed: {ex.Message}");
        }
    }

    private async void OnEditsApplied(object? sender, EditsAppliedEventArgs e)
    {
        await RefreshAfterPixelEditAsync(e.FilePath, e.PixelWidth, e.PixelHeight, "Saved edits to");
    }

    /// <summary>
    /// Keeps the metadata panel pointed at whatever the viewer is showing, so ratings, tags and
    /// the flag act on the image on screen. When the viewer closes, falls back to the grid
    /// selection.
    /// </summary>
    private void OnViewerEntryChanged(ImageEntry? entry)
    {
        if (entry is not null)
            MetadataPanel.ShowMetadata(entry);
        else
            ImageGrid.RefreshMetadataFromSelection();
    }

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        await FolderNav.LoadWatchedFoldersAsync();
        await FlagNav.LoadAsync();
        
        // Don't load images on startup - wait for user to select a folder
        // This prevents showing all 535 indexed images in random order
        TotalImages = await _imageRepo.GetCountAsync();
        StatusText = TotalImages > 0 ? "Select a folder to view photos" : "Add folders to get started";

        // Start background indexing to populate metadata (tags, dates)
        StartBackgroundIndexing();
        StartBackgroundFaceDetection();
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

    [RelayCommand]
    private void ToggleFaceDetection()
    {
        if (IsFaceDetectionRunning)
        {
            StopBackgroundFaceDetection();
            return;
        }

        _faceDetectionEnabled = true;
        StartBackgroundFaceDetection();
    }

    public void StartBackgroundFaceDetection()
    {
        if (!_faceDetectionEnabled || TotalImages == 0)
        {
            return;
        }

        if (IsFaceDetectionRunning)
        {
            _faceRescanRequested = true;
            return;
        }

        _faceDetectionCts?.Dispose();
        _faceDetectionCts = new CancellationTokenSource();
        var cancellationToken = _faceDetectionCts.Token;
        IsFaceDetectionRunning = true;

        _faceDetectionTask = Task.Run(async () =>
        {
            try
            {
                await _faceProcessor.ProcessLibraryAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                App.MainWindow?.DispatcherQueue.TryEnqueue(
                    () => StatusText = $"Face detection failed: {exception.Message}");
            }
            finally
            {
                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    IsFaceDetectionRunning = false;
                    if (_faceRescanRequested && _faceDetectionEnabled)
                    {
                        _faceRescanRequested = false;
                        StartBackgroundFaceDetection();
                    }
                });
            }
        });
    }

    private void StopBackgroundFaceDetection()
    {
        _faceDetectionEnabled = false;
        _faceRescanRequested = false;
        _faceDetectionCts?.Cancel();
        StatusText = "Stopping face detection…";
    }

    private void OnFaceProcessingProgress(object? sender, FaceProcessingProgressEventArgs e)
    {
        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            if (e.IsPreparing)
            {
                StatusText = "Preparing face detection models…";
            }
            else if (e.IsCanceled)
            {
                StatusText = $"Face detection stopped after {e.Processed:N0} photos";
            }
            else if (e.IsComplete)
            {
                StatusText = e.Failed == 0
                    ? $"Face detection complete: {e.FacesFound:N0} faces in {e.Processed:N0} photos"
                    : $"Face detection complete: {e.FacesFound:N0} faces, {e.Failed:N0} photos failed";
            }
            else if (!string.IsNullOrEmpty(e.Error))
            {
                StatusText = $"Face detection skipped {e.CurrentFile}: {e.Error}";
            }
            else
            {
                StatusText = $"Finding faces… {e.Processed:N0}/{e.Total:N0} photos, {e.FacesFound:N0} faces";
            }
        });
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

    /// <summary>
    /// Sets or clears the flag on a set of images. Single entry point used by the grid, the
    /// viewer, the context menu and the metadata panel: writes the file metadata, updates the
    /// cache DB, then refreshes the "Flagged" node count and any dependent UI.
    /// </summary>
    public async Task SetFlagAsync(IReadOnlyList<ImageEntry> entries, bool flagged)
    {
        if (entries is null || entries.Count == 0) return;

        int changed = 0;
        foreach (var entry in entries)
        {
            try
            {
                await MetadataWriterService.WriteFlagAsync(entry.FilePath, flagged);
            }
            catch (Exception ex)
            {
                DebugLog.WriteLine($"[FLAG] Failed to write flag to {entry.FilePath}: {ex.Message}");
            }

            entry.IsFlagged = flagged;
            if (entry.Id > 0)
                await _imageRepo.UpdateFlagAsync(entry.Id, flagged);
            changed++;
        }

        StatusText = flagged
            ? $"Flagged {changed:N0} item(s)"
            : $"Unflagged {changed:N0} item(s)";

        await RefreshFlagNavAsync();
        MetadataPanel.RefreshFlagState();
        await ImageGrid.OnFlagsChangedAsync(entries, flagged);
    }

    /// <summary>
    /// Reloads the flagged count and repaints the left-panel "Flagged" node.
    /// </summary>
    public async Task RefreshFlagNavAsync()
    {
        await FlagNav.LoadAsync();
        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            if (App.MainWindow is MainWindow window)
                window.RefreshFlagTree();
        });
    }

    /// <summary>
    /// Refreshes the tag navigation tree (counts + new tags) — call after tag edits.
    /// </summary>
    public async Task RefreshTagsTreeAsync()
    {
        await TagNav.LoadTagsAsync();
        if (ImageGrid.Refinement.RequiresTags)
            await ImageGrid.LoadImagesAsync();

        if (App.MainWindow is MainWindow window)
        {
            window.DispatcherQueue.TryEnqueue(async () =>
            {
                await window.RefreshMetadataTreesAsync();
            });
        }
    }

    /// <summary>
    /// Refreshes the date navigation tree and re-applies grid grouping/sorting — call after
    /// capture-date edits so the left-panel date filter and the grid's order/grouping reflect
    /// the new dates without a full reload.
    /// </summary>
    public async Task RefreshAfterDateChangeAsync()
    {
        await DateNav.LoadDatesAsync();
        if (App.MainWindow is MainWindow window)
        {
            window.DispatcherQueue.TryEnqueue(async () =>
            {
                await window.RefreshMetadataTreesAsync();
                await ImageGrid.RefreshGroupingAsync();
            });
        }
    }

    /// <summary>
    /// Applies a tag to a set of images identified by their file paths. Used by the drag-to-tag
    /// drop target on the tags tree. Writes to DB and to the image file (or sidecar for RAW),
    /// then refreshes the tag tree and the metadata panel.
    /// </summary>
    public async Task ApplyTagToImagePathsAsync(string tag, IReadOnlyList<string> filePaths)
    {
        if (string.IsNullOrWhiteSpace(tag) || filePaths == null || filePaths.Count == 0) return;
        var trimmed = tag.Trim();

        StatusText = $"Applying tag '{trimmed}' to {filePaths.Count:N0} item(s)…";

        int applied = 0;
        int skipped = 0;
        foreach (var path in filePaths)
        {
            try
            {
                var entry = await _imageRepo.GetByPathAsync(path);
                if (entry == null || entry.Id <= 0) { skipped++; continue; }

                await _tagRepo.AddTagAsync(new ImageTag
                {
                    ImageId = entry.Id,
                    Tag = trimmed,
                    Source = TagSource.Manual,
                    Confidence = 1.0f
                });

                var allTags = await _tagRepo.GetTagsAsync(entry.Id);
                await TagWriterService.WriteTagsToSidecarAsync(path,
                    allTags.Select(t => t.Tag).Distinct());

                applied++;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TAGDROP] Failed to apply '{trimmed}' to {path}: {ex.Message}");
                skipped++;
            }
        }

        StatusText = skipped == 0
            ? $"Applied tag '{trimmed}' to {applied:N0} item(s)"
            : $"Applied tag '{trimmed}' to {applied:N0} item(s) ({skipped:N0} skipped)";

        await RefreshTagsTreeAsync();
        await MetadataPanel.ReloadTagsAsync();
    }

    /// <summary>
    /// Called after the image file at <paramref name="filePath"/> has been cropped on disk.
    /// Updates DB dimensions, refreshes the grid thumbnail and the open viewer.
    /// </summary>
    public Task RefreshAfterCropAsync(string filePath, uint newPixelWidth, uint newPixelHeight) =>
        RefreshAfterPixelEditAsync(filePath, newPixelWidth, newPixelHeight, "Cropped");

    public Task RefreshAfterStraightenAsync(
        string filePath,
        uint newPixelWidth,
        uint newPixelHeight) =>
        RefreshAfterPixelEditAsync(filePath, newPixelWidth, newPixelHeight, "Straightened");

    /// <summary>
    /// Called after the pixels of <paramref name="filePath"/> have been rewritten (crop, baked
    /// adjustments). Updates DB dimensions, refreshes the grid thumbnail and the open viewer.
    /// Both paths write display-oriented pixels and reset the EXIF orientation tag to 1.
    /// </summary>
    public async Task RefreshAfterPixelEditAsync(
        string filePath, uint newPixelWidth, uint newPixelHeight, string verb)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        try
        {
            var entry = await _imageRepo.GetByPathAsync(filePath);
            if (entry != null && entry.Id > 0)
            {
                var size = new System.IO.FileInfo(filePath).Length;
                await _imageRepo.UpdateDimensionsAsync(entry.Id, (int)newPixelWidth, (int)newPixelHeight, size);
                entry.Width = (int)newPixelWidth;
                entry.Height = (int)newPixelHeight;
                entry.Orientation = 1;
                entry.FileSize = size;
            }
            await ImageGrid.RefreshSingleImageAsync(filePath);
            await ImageViewer.ReloadCurrentImageAsync();
            StartBackgroundFaceDetection();
            StatusText = $"{verb} {System.IO.Path.GetFileName(filePath)} ({newPixelWidth}×{newPixelHeight})";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EDIT] RefreshAfterPixelEditAsync failed: {ex.Message}");
            StatusText = $"Refresh failed: {ex.Message}";
        }
    }

    public async Task RefreshAfterIndexAsync()
    {
        await ImageGrid.LoadImagesAsync();
        TotalImages = await _imageRepo.GetCountAsync();
        StatusText = $"{TotalImages:N0} items";
        // Refresh date and tag navigation data
        await DateNav.LoadDatesAsync();
        await TagNav.LoadTagsAsync();
        await FlagNav.LoadAsync();
        StartBackgroundFaceDetection();
        
        // Update UI trees on main thread
        App.MainWindow?.DispatcherQueue.TryEnqueue(async () =>
        {
            if (App.MainWindow is MainWindow window)
            {
                await window.RefreshMetadataTreesAsync();
            }
        });
    }

    public async Task CleanupAsync()
    {
        DebugLog.WriteLine("MainViewModel: Cleanup - canceling background tasks");
        _indexingCts?.Cancel();
        _indexingCts?.Dispose();
        _faceDetectionEnabled = false;
        _faceDetectionCts?.Cancel();
        if (_faceDetectionTask is not null)
        {
            await _faceDetectionTask;
        }
        _faceDetectionCts?.Dispose();
        _faceResources.Dispose();
        _scanner.Dispose();
    }
}
