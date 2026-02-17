using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Diagnostics;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PhotoLibrarian.ViewModels;

public partial class ImageGridViewModel : ObservableObject
{
    private readonly ImageRepository _imageRepo;
    private readonly FolderScannerService _scanner;
    private readonly MetadataReaderService _metadataReader;
    private readonly MainViewModel _main;
    private string? _currentFolderFilter;
    private string? _currentSortBy = "date_taken";
    private bool _sortDescending = true;
    private CancellationTokenSource? _loadCts;
    
    // Limit concurrent thumbnail loading to prevent memory exhaustion
    internal static readonly SemaphoreSlim s_thumbnailLoadSemaphore = new(8, 8);

    public ObservableCollection<ImageThumbnailViewModel> Images { get; } = [];

    [ObservableProperty]
    public partial ImageThumbnailViewModel? SelectedImage { get; set; }

    [ObservableProperty]
    public partial double ThumbnailSize { get; set; }

    [ObservableProperty]
    public partial string SortField { get; set; }

    public ImageGridViewModel(
        ImageRepository imageRepo,
        FolderScannerService scanner,
        MetadataReaderService metadataReader,
        MainViewModel main)
    {
        _imageRepo = imageRepo;
        _scanner = scanner;
        _metadataReader = metadataReader;
        _main = main;

        ThumbnailSize = 180;
        SortField = "Date Taken";
    }
    
    public async Task LoadImagesAsync()
    {
        // Cancel any pending thumbnail loads from previous folder
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        var images = await _imageRepo.GetAllAsync(_currentSortBy, _sortDescending);
        
        // Clear old images and dispose thumbnails to free memory
        foreach (var img in Images)
        {
            if (img.Thumbnail is not null)
            {
                // Clear the source to allow GC to collect the bitmap data
                img.Thumbnail = null;
            }
        }
        Images.Clear();
        
        // Hint to GC to collect disposed bitmaps
        if (images.Count == 0) // Only if we'll be loading new ones
        {
            GC.Collect(0, GCCollectionMode.Optimized);
        }

        // Ensure folder filter ends with separator for correct prefix matching
        var filter = _currentFolderFilter;
        if (filter is not null && !filter.EndsWith(Path.DirectorySeparatorChar))
            filter += Path.DirectorySeparatorChar;

        DebugLog.WriteLine($"LoadImagesAsync: Total images from DB: {images.Count}, Filter: '{filter ?? "null"}'");

        int matchCount = 0;
        int skipCount = 0;
        foreach (var img in images)
        {
            if (filter is not null &&
                !img.FilePath.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
            {
                skipCount++;
                if (skipCount <= 2) // Log first 2 skips
                    DebugLog.WriteLine($"  Skipping '{img.FilePath}' (doesn't start with '{filter}')");
                continue;
            }

            matchCount++;
            if (matchCount <= 3) // Log first 3 matches
                DebugLog.WriteLine($"  Including '{img.FilePath}'");

            Images.Add(new ImageThumbnailViewModel(img, ct));
        }
        
        if (skipCount > 2)
            DebugLog.WriteLine($"  ... and {skipCount - 2} more skipped");
        
        DebugLog.WriteLine($"LoadImagesAsync: Added {Images.Count} images to collection (matched {matchCount}, skipped {skipCount})");

        // Update status bar with actual grid count
        if (Images.Count > 0)
        {
            _main.StatusText = $"{Images.Count:N0} items";
        }
        else
        {
            _main.StatusText = "No items match the current filter";
        }

        // HYBRID APPROACH: Always scan folder to find any missing/new files not in database
        // This ensures we show all images even if database is stale/incomplete
        if (filter is not null && Directory.Exists(filter.TrimEnd(Path.DirectorySeparatorChar)))
        {
            var folderPath = filter.TrimEnd(Path.DirectorySeparatorChar);
            DebugLog.WriteLine($"LoadImagesAsync: Scanning folder for any new/missing files...");
            
            // Get indexed file paths for quick lookup
            var indexedPaths = new HashSet<string>(Images.Select(i => i.Entry.FilePath), StringComparer.OrdinalIgnoreCase);
            var initialCount = Images.Count;
            
            // Scan for files not in database
            var missingFiles = await Task.Run(() =>
            {
                var result = new List<string>();
                foreach (var filePath in Directory.EnumerateFiles(folderPath, "*.*",
                    new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    if (ct.IsCancellationRequested) break;
                    
                    if (FolderScannerService.IsSupportedFile(filePath) && !indexedPaths.Contains(filePath))
                    {
                        result.Add(filePath);
                    }
                }
                return result;
            }, ct);
            
            if (missingFiles.Count > 0)
            {
                DebugLog.WriteLine($"LoadImagesAsync: Found {missingFiles.Count} files not in database, adding them...");
                
                // Create view models for missing files
                foreach (var filePath in missingFiles)
                {
                    if (ct.IsCancellationRequested) break;
                    
                    var entry = new ImageEntry
                    {
                        Id = 0, // Not indexed
                        FilePath = filePath,
                        FileName = Path.GetFileName(filePath),
                        FileSize = 0,
                        DateModified = DateTime.UtcNow,
                        DateIndexed = DateTime.UtcNow,
                        MediaType = FolderScannerService.IsVideoFile(filePath) ? MediaType.Video : MediaType.Image
                    };
                    
                    var vm = new ImageThumbnailViewModel(entry, ct);
                    Images.Add(vm);
                }
                
                // Update status bar
                _main.StatusText = $"{Images.Count:N0} items ({missingFiles.Count} not indexed)";
            }
            else
            {
                DebugLog.WriteLine($"LoadImagesAsync: All files are indexed, showing {Images.Count} items");
            }
        }
        
        // Load thumbnails in batches (sequential, but fast with Windows cache)
        DebugLog.WriteLine($"LoadImagesAsync: Starting thumbnail loading for {Images.Count} items");
        await LoadThumbnailsInBatchesAsync(Images.ToList(), ct);
    }

    private async Task ScanFolderDirectlyAsync(string folderPath, CancellationToken ct)
    {
        var overallSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            DebugLog.WriteLine($"[PERF] Starting file enumeration");
            
            // Scan files on background thread
            var enumSw = System.Diagnostics.Stopwatch.StartNew();
            var files = await Task.Run(() =>
            {
                var result = new List<string>();
                foreach (var filePath in Directory.EnumerateFiles(folderPath, "*.*", 
                    new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    if (ct.IsCancellationRequested) break;
                    
                    if (FolderScannerService.IsSupportedFile(filePath))
                    {
                        result.Add(filePath);
                        // No limit - ItemsRepeater virtualizes UI, thumbnails load in batches
                    }
                }
                return result;
            }, ct);
            
            DebugLog.WriteLine($"[PERF] File enumeration complete: {enumSw.ElapsedMilliseconds}ms, found {files.Count} files");

            if (files.Count == 0)
            {
                DebugLog.WriteLine($"ScanFolderDirectlyAsync: No supported files found in '{folderPath}'");
                _main.StatusText = "No photos or videos in this folder";
                return;
            }
            
            // Update status bar with grid count
            _main.StatusText = $"{files.Count:N0} items";
            
            // Step 1: Create placeholder view models immediately and add to UI
            var placeholderSw = System.Diagnostics.Stopwatch.StartNew();
            var viewModels = new List<ImageThumbnailViewModel>();
            foreach (var filePath in files)
            {
                if (ct.IsCancellationRequested) break;
                
                var entry = new ImageEntry
                {
                    Id = 0,
                    FilePath = filePath,
                    FileName = Path.GetFileName(filePath),
                    FileSize = 0,
                    DateModified = DateTime.UtcNow,
                    DateIndexed = DateTime.UtcNow,
                    MediaType = FolderScannerService.IsVideoFile(filePath) ? MediaType.Video : MediaType.Image
                };
                
                var vm = new ImageThumbnailViewModel(entry, ct);
                viewModels.Add(vm);
                Images.Add(vm); // Add immediately to show spinner
            }
            DebugLog.WriteLine($"[PERF] Added {viewModels.Count} placeholders to UI: {placeholderSw.ElapsedMilliseconds}ms");
            
            // Update status bar
            _main.StatusText = $"{viewModels.Count:N0} items";
            
            // NOTE: Thumbnails load on-demand via viewport-aware loading (OnItemBecameVisible)
            
            DebugLog.WriteLine($"[PERF] TOTAL TIME: {overallSw.ElapsedMilliseconds}ms for {files.Count} images");
        }
        catch (OperationCanceledException)
        {
            DebugLog.WriteLine($"[PERF] Scan cancelled after {overallSw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"[PERF] Error scanning folder: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Loads thumbnails for a list of view models in batches of 20.
    /// Used for both indexed images (from database) and unindexed images (scanned directly).
    /// </summary>
    private async Task LoadThumbnailsInBatchesAsync(List<ImageThumbnailViewModel> viewModels, CancellationToken ct)
    {
        const int batchSize = 20;
        int batchNumber = 0;
        
        for (int i = 0; i < viewModels.Count; i += batchSize)
        {
            if (ct.IsCancellationRequested) break;
            
            batchNumber++;
            var batchVMs = viewModels.Skip(i).Take(batchSize).ToList();
            await LoadBatchAsync(batchVMs, batchNumber, ct);
        }
    }
    
    private async Task LoadBatchAsync(List<ImageThumbnailViewModel> viewModels, int batchNumber, CancellationToken ct)
    {
        using var profiler = new Diagnostics.PerformanceProfiler($"Batch{batchNumber}");
        profiler.Log("BATCH_START", $"Count={viewModels.Count}");
        
        var batchSw = System.Diagnostics.Stopwatch.StartNew();
        DebugLog.WriteLine($"[PERF] Batch {batchNumber}: Starting thumbnail generation for {viewModels.Count} images");
        
        // Step 1: Get thumbnail streams (encoded PNG/BMP from cache) on background threads
        profiler.Log("STEP1_START", "Getting thumbnail streams from cache");
        var thumbGenSw = System.Diagnostics.Stopwatch.StartNew();
        var thumbnailData = await Task.Run(async () =>
        {
            // Use semaphore to limit concurrent operations (8 is optimal)
            var semaphore = new SemaphoreSlim(8, 8);
            var tasks = new List<Task<(ImageThumbnailViewModel vm, byte[]? streamBytes)>>();
            
            foreach (var vm in viewModels)
            {
                tasks.Add(Task.Run(async () =>
                {
                    using (profiler.StartTimer("CACHE_WAIT", vm.FileName))
                    {
                        await semaphore.WaitAsync(ct);
                    }
                    
                    try
                    {
                        using (profiler.StartTimer("CACHE_READ", vm.FileName))
                        {
                            // Get encoded thumbnail from Windows cache (instant if cached)
                            var streamBytes = await WindowsThumbnailService.GetThumbnailStreamAsync(vm.Entry.FilePath, 180);
                            if (streamBytes == null)
                            {
                                DebugLog.WriteLine($"[WARN] Failed to get thumbnail for: {vm.FileName} at {vm.Entry.FilePath}");
                            }
                            return (vm, streamBytes);
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLog.WriteLine($"[ERROR] Exception loading thumbnail for {vm.FileName}: {ex.Message}");
                        return (vm, null);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }
            
            return await Task.WhenAll(tasks);
        }, ct);
        
        thumbGenSw.Stop();
        var successCount = thumbnailData.Count(t => t.streamBytes != null);
        profiler.Log("STEP1_COMPLETE", $"Success={successCount}/{viewModels.Count}", thumbGenSw.ElapsedMilliseconds);
        DebugLog.WriteLine($"[PERF] Batch {batchNumber}: Cache read: {thumbGenSw.ElapsedMilliseconds}ms ({successCount}/{viewModels.Count} succeeded)");
        
        // Step 2: Create BitmapImages on UI thread from encoded streams (fast - no decode)
        profiler.Log("STEP2_START", "Creating BitmapImages on UI thread");
        var bitmapSw = System.Diagnostics.Stopwatch.StartNew();
        
        foreach (var (vm, streamBytes) in thumbnailData)
        {
            if (ct.IsCancellationRequested) break;
            
            if (streamBytes != null && streamBytes.Length > 0)
            {
                using (profiler.StartTimer("UI_CREATE_BITMAP", vm.FileName))
                {
                    await vm.LoadThumbnailFromStreamAsync(streamBytes);
                }
            }
            else
            {
                vm.IsLoading = false;
            }
        }
        
        bitmapSw.Stop();
        profiler.Log("STEP2_COMPLETE", $"Created {successCount} bitmaps", bitmapSw.ElapsedMilliseconds);
        DebugLog.WriteLine($"[PERF] Batch {batchNumber}: BitmapImage creation: {bitmapSw.ElapsedMilliseconds}ms");
        
        profiler.Log("BATCH_COMPLETE", $"Total time", batchSw.ElapsedMilliseconds);
        DebugLog.WriteLine($"[PERF] Batch {batchNumber}: TOTAL: {batchSw.ElapsedMilliseconds}ms");
    }

    public async Task FilterByFolderAsync(string? folderPath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DebugLog.WriteLine($"FilterByFolderAsync: folderPath='{folderPath ?? "null"}'");
        
        // Pause background indexing while user is browsing
        _main.PauseBackgroundIndexing();
        
        _currentFolderFilter = folderPath;
        DebugLog.WriteLine($"  T+{sw.ElapsedMilliseconds}ms: Starting LoadImagesAsync");
        await LoadImagesAsync();
        DebugLog.WriteLine($"  T+{sw.ElapsedMilliseconds}ms: LoadImagesAsync complete, Images.Count={Images.Count}");
        
        // Don't auto-resume indexing - let user manually trigger via Refresh button
        // _ = Task.Run(async () =>
        // {
        //     await Task.Delay(5000); // Wait 5 seconds
        //     _main.StartBackgroundIndexing();
        // });
    }

    [RelayCommand]
    private async Task SortByDateAsync()
    {
        _currentSortBy = "date_taken";
        _sortDescending = true;
        SortField = "Date Taken";
        await LoadImagesAsync();
    }

    [RelayCommand]
    private async Task SortByNameAsync()
    {
        _currentSortBy = "file_name";
        _sortDescending = false;
        SortField = "Name";
        await LoadImagesAsync();
    }

    [RelayCommand]
    private async Task SortByRatingAsync()
    {
        _currentSortBy = "rating";
        _sortDescending = true;
        SortField = "Rating";
        await LoadImagesAsync();
    }

    [RelayCommand]
    private void ClearFilter()
    {
        _currentFolderFilter = null;
        _ = LoadImagesAsync();
    }

    [RelayCommand]
    private void IncreaseThumbnailSize()
    {
        ThumbnailSize = Math.Min(ThumbnailSize + 40, 400);
    }

    [RelayCommand]
    private void DecreaseThumbnailSize()
    {
        ThumbnailSize = Math.Max(ThumbnailSize - 40, 100);
    }

    partial void OnSelectedImageChanged(ImageThumbnailViewModel? value)
    {
        if (value is not null)
        {
            _main.MetadataPanel.ShowMetadata(value.Entry);
        }
    }

    [RelayCommand]
    private void OpenViewer()
    {
        if (SelectedImage is not null)
        {
            _main.ImageViewer.OpenImage(SelectedImage.Entry, Images.Select(i => i.Entry).ToList());
        }
    }
}

public partial class ImageThumbnailViewModel : ObservableObject
{
    private readonly CancellationToken _cancellationToken;
    private bool _thumbnailLoaded;

    public ImageEntry Entry { get; }

    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Media.ImageSource? Thumbnail { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public string FileName => Entry.FileName;
    public string DateDisplay => Entry.DateTaken?.ToString("MMM d, yyyy") ?? "";
    public int? Rating => Entry.Rating;
    public bool IsVideo => Entry.MediaType == MediaType.Video;

    public ImageThumbnailViewModel(ImageEntry entry, CancellationToken cancellationToken = default)
    {
        Entry = entry;
        _cancellationToken = cancellationToken;

        IsLoading = true;
    }

    /// <summary>
    /// OBSOLETE: Lazy-loading replaced by batch loading with Windows thumbnail cache.
    /// This method is kept for backward compatibility but should not be called.
    /// </summary>
    [Obsolete("Use LoadThumbnailFromStreamAsync instead - called from LoadBatchAsync")]
    public async Task LoadThumbnailAsync()
    {
        if (_thumbnailLoaded)
            return;
        
        _thumbnailLoaded = true;

        try
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                IsLoading = false;
                return;
            }

            // Fallback: use Windows thumbnail cache
            var streamBytes = await WindowsThumbnailService.GetThumbnailStreamAsync(Entry.FilePath, 180);
            if (streamBytes != null)
            {
                await LoadThumbnailFromStreamAsync(streamBytes);
            }
            else
            {
                IsLoading = false;
            }
        }
        catch
        {
            IsLoading = false;
        }
    }
    
    /// <summary>
    /// Loads a thumbnail from encoded stream bytes (PNG/BMP from Windows cache).
    /// Much faster than pixel decode - just uses BitmapImage.SetSourceAsync on pre-encoded data.
    /// </summary>
    public async Task LoadThumbnailFromStreamAsync(byte[] streamBytes)
    {
        if (_thumbnailLoaded) return;
        _thumbnailLoaded = true;
        
        try
        {
            var bitmapImage = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using (var stream = new MemoryStream(streamBytes))
            {
                await bitmapImage.SetSourceAsync(stream.AsRandomAccessStream());
            }
            Thumbnail = bitmapImage;
            IsLoading = false;
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"LoadThumbnailFromStreamAsync failed for {FileName}: {ex.Message}");
            IsLoading = false;
        }
    }
    
    /// <summary>
    /// Loads a thumbnail from raw BGRA8 pixel data. Uses WriteableBitmap for fast rendering.
    /// Pixels are already decoded on background thread, UI thread only does memory copy.
    /// </summary>
    public void LoadThumbnailFromPixels(byte[] pixels, int width, int height, Diagnostics.PerformanceProfiler profiler)
    {
        if (_thumbnailLoaded) return;
        _thumbnailLoaded = true;
        
        try
        {
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                using (profiler.StartTimer("UI_CREATE_WRITEABLEBITMAP", FileName))
                {
                    try
                    {
                        // Create WriteableBitmap and write pixels directly - should be <5ms
                        var wb = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(width, height);
                        
                        using (profiler.StartTimer("UI_PIXEL_COPY", FileName))
                        {
                            using (var pixelStream = wb.PixelBuffer.AsStream())
                            {
                                pixelStream.Write(pixels, 0, pixels.Length);
                            }
                        }
                        
                        using (profiler.StartTimer("UI_SET_THUMBNAIL", FileName))
                        {
                            Thumbnail = wb;
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLog.WriteLine($"LoadThumbnailFromPixels FAILED: {FileName} - {ex.Message}");
                    }
                    finally
                    {
                        IsLoading = false;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"LoadThumbnailFromPixels dispatch FAILED: {FileName} - {ex.Message}");
            IsLoading = false;
        }
    }
}
