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

/// <summary>
/// Options for grouping photos in the grid
/// </summary>
public enum GroupByOption
{
    None,           // No grouping (flat list - current behavior)
    FileType,       // Group by file extension: CR3, JPG, PNG, MP4, MOV, etc.
    MediaType,      // Group by media type: Images, Videos
    YearTaken,      // Group by year taken: 2024, 2023, etc.
    MonthTaken,     // Group by month taken: January 2024, February 2024, etc.
    FileSize,       // Group by file size: Large (>10MB), Medium (1-10MB), Small (<1MB)
    ImageSize,      // Group by resolution: 4K+ (>8MP), Full HD (2-8MP), HD (<2MP)
    Rating,         // Group by rating: 5 stars, 4 stars, Unrated, etc.
    Camera          // Group by camera model: Canon EOS R5, Sony A7III, etc.
}

/// <summary>
/// Options for sorting photos within groups
/// </summary>
public enum SortByOption
{
    FileName,       // Alphabetical by file name
    DateTaken,      // Chronological by date taken (newest first by default)
    DateModified,   // By file system modified date
    FileSize,       // By file size (largest first by default)
    Rating          // By rating (highest first)
}

/// <summary>
/// Represents a group of photos with a header
/// </summary>
public partial class PhotoGroup : ObservableObject
{
    [ObservableProperty]
    private string _header = string.Empty;
    
    [ObservableProperty]
    private ObservableCollection<ImageThumbnailViewModel> _items = [];
    
    public int Count => Items.Count;
}

public partial class ImageGridViewModel : ObservableObject
{
    private readonly ImageRepository _imageRepo;
    private readonly FolderScannerService _scanner;
    private readonly MetadataReaderService _metadataReader;
    private readonly MainViewModel _main;
    private List<string>? _currentFolderFilters; // Changed to support multiple folders
    private bool _currentDateRootSelected; // Root "Dates" node selected (show all dated images)
    private List<int>? _currentYearFilters;
    private List<(int Year, int Month)>? _currentMonthFilters;
    private bool _currentTagRootSelected; // Root "Tags" node selected (show all tagged images)
    private List<string>? _currentTagFilters;
    private bool _currentFlaggedSelected; // "Flagged" node selected (show flagged working set)
    private string? _currentSortBy = "date_taken";
    private CancellationTokenSource? _loadCts;
    
    // Queue management for viewport-aware loading
    private readonly Queue<ImageThumbnailViewModel> _loadQueue = new();
    private readonly HashSet<ImageThumbnailViewModel> _queuedItems = new();
    private readonly object _queueLock = new();
    private DateTime _lastScrollReorder = DateTime.MinValue;
    private const int ScrollReorderThrottleMs = 150; // Don't reorder more than every 150ms
    private Task? _backgroundLoadTask;
    
    // Limit concurrent thumbnail loading to prevent memory exhaustion
    internal static readonly SemaphoreSlim s_thumbnailLoadSemaphore = new(8, 8);

    public ObservableCollection<ImageThumbnailViewModel> Images { get; } = [];
    public ObservableCollection<PhotoGroup> GroupedImages { get; } = [];

    [ObservableProperty]
    private GroupByOption _groupBy = GroupByOption.None;
    
    [ObservableProperty]
    private SortByOption _sortBy = SortByOption.DateTaken;
    
    [ObservableProperty]
    private bool _sortDescending = true;
    
    partial void OnGroupByChanged(GroupByOption value)
    {
        _ = ApplyGroupingAsync();
    }
    
    partial void OnSortByChanged(SortByOption value)
    {
        _ = ApplyGroupingAsync();
    }
    
    partial void OnSortDescendingChanged(bool value)
    {
        _ = ApplyGroupingAsync();
    }

    [ObservableProperty]
    public partial ImageThumbnailViewModel? SelectedImage { get; set; }

    /// <summary>
    /// All currently-selected items in the grid (multi-select aware).
    /// Updated by ImageGridView when the grid raises SelectionChanged.
    /// </summary>
    public List<ImageThumbnailViewModel> SelectedImages { get; } = new();

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
    
    /// <summary>
    /// Cleanup method to cancel background loading on app shutdown.
    /// </summary>
    public void Cleanup()
    {
        _loadCts?.Cancel();
        lock (_queueLock)
        {
            _loadQueue.Clear();
            _queuedItems.Clear();
        }
    }
    
    public async Task LoadImagesAsync()
    {
        // Cancel any pending thumbnail loads from previous folder
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        
        // Wait for previous background task to complete
        if (_backgroundLoadTask != null)
        {
            try
            {
                await _backgroundLoadTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelling
            }
        }
        
        // Clear the queue
        lock (_queueLock)
        {
            _loadQueue.Clear();
            _queuedItems.Clear();
        }

        // Get all images from DB (we'll filter to union of all criteria)
        var allImages = await _imageRepo.GetAllAsync(_currentSortBy, SortDescending);
        
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
        if (allImages.Count == 0) // Only if we'll be loading new ones
        {
            GC.Collect(0, GCCollectionMode.Optimized);
        }

        // Check if any filters are active
        bool hasFolderFilter = _currentFolderFilters is not null && _currentFolderFilters.Count > 0;
        bool hasDateFilter = _currentDateRootSelected || 
                            (_currentYearFilters is not null && _currentYearFilters.Count > 0) ||
                            (_currentMonthFilters is not null && _currentMonthFilters.Count > 0);
        bool hasTagFilter = _currentTagRootSelected || 
                           (_currentTagFilters is not null && _currentTagFilters.Count > 0);
        bool hasFlagFilter = _currentFlaggedSelected;

        // If no filters active, show nothing
        if (!hasFolderFilter && !hasDateFilter && !hasTagFilter && !hasFlagFilter)
        {
            DebugLog.WriteLine($"LoadImagesAsync: No filters active, showing empty grid");
            return;
        }

        // Pre-load tag data if tag filtering active
        HashSet<long>? taggedImageIds = null;
        if (hasTagFilter)
        {
            var taggedImages = await _imageRepo.GetFilteredAsync(_currentTagRootSelected, _currentTagFilters, _currentSortBy, SortDescending);
            taggedImageIds = new HashSet<long>(taggedImages.Select(i => i.Id));
            DebugLog.WriteLine($"LoadImagesAsync: Loaded {taggedImageIds.Count} images matching tag filters");
        }

        // Prepare folder filters with separators for correct prefix matching
        var folderFilters = _currentFolderFilters?.Select(f => 
            f.EndsWith(Path.DirectorySeparatorChar) ? f : f + Path.DirectorySeparatorChar
        ).ToList();

        DebugLog.WriteLine($"LoadImagesAsync: Total images from DB: {allImages.Count}, FolderFilters: {folderFilters?.Count ?? 0}, DateFilters: {hasDateFilter}, TagFilters: {hasTagFilter}");
        DebugLog.WriteLine($"  UNION logic: Show images matching ANY filter (folder OR date OR tag)");

        int matchCount = 0;
        int skipCount = 0;
        int index = 0;
        foreach (var img in allImages)
        {
            bool matchesAnyFilter = false;

            // Check folder filter
            if (hasFolderFilter && folderFilters is not null)
            {
                bool matchesFolder = folderFilters.Any(f => 
                    img.FilePath.StartsWith(f, StringComparison.OrdinalIgnoreCase));
                
                if (matchesFolder)
                {
                    matchesAnyFilter = true;
                }
            }

            // Check date filter
            if (!matchesAnyFilter && hasDateFilter && img.DateTaken.HasValue)
            {
                if (_currentDateRootSelected)
                {
                    // Date root selected - matches all dated images
                    matchesAnyFilter = true;
                }
                else
                {
                    // Check year/month filters
                    int year = img.DateTaken.Value.Year;
                    var yearMonth = (year, img.DateTaken.Value.Month);

                    bool matchesYear = _currentYearFilters?.Contains(year) ?? false;
                    bool matchesMonth = _currentMonthFilters?.Contains(yearMonth) ?? false;

                    if (matchesYear || matchesMonth)
                    {
                        matchesAnyFilter = true;
                    }
                }
            }

            // Check tag filter
            if (!matchesAnyFilter && hasTagFilter && taggedImageIds is not null)
            {
                if (taggedImageIds.Contains(img.Id))
                {
                    matchesAnyFilter = true;
                }
            }

            // Check flag filter
            if (!matchesAnyFilter && hasFlagFilter && img.IsFlagged)
            {
                matchesAnyFilter = true;
            }

            if (matchesAnyFilter)
            {
                matchCount++;
                if (matchCount <= 3)
                    DebugLog.WriteLine($"  Including '{img.FilePath}'");

                Images.Add(new ImageThumbnailViewModel(img, ct) { Index = index });
                index++;
            }
            else
            {
                skipCount++;
            }
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

        // HYBRID APPROACH: Scan folders to find any missing/new files not in database
        // This ensures we show all images even if database is stale/incomplete
        if (hasFolderFilter && folderFilters is not null)
        {
            // Get indexed file paths for quick lookup
            var indexedPaths = new HashSet<string>(Images.Select(i => i.Entry.FilePath), StringComparer.OrdinalIgnoreCase);
            var initialCount = Images.Count;

            foreach (var filter in folderFilters)
            {
                var folderPath = filter.TrimEnd(Path.DirectorySeparatorChar);
                if (!Directory.Exists(folderPath)) continue;

                DebugLog.WriteLine($"LoadImagesAsync: Scanning folder '{folderPath}' for any new/missing files...");
                
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
                    DebugLog.WriteLine($"LoadImagesAsync: Found {missingFiles.Count} files not in database for '{folderPath}', adding them...");
                    
                    // Create view models for missing files
                    int currentIndex = Images.Count; // Continue from current count
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
                        
                        var vm = new ImageThumbnailViewModel(entry, ct) { Index = currentIndex };
                        Images.Add(vm);
                        indexedPaths.Add(filePath); // Prevent duplicates across multiple filters
                        currentIndex++;
                    }
                }
            }

            if (Images.Count > initialCount)
            {
                DebugLog.WriteLine($"LoadImagesAsync: Added {Images.Count - initialCount} previously unindexed files from disk scan");
                _main.StatusText = $"{Images.Count:N0} items";
            }
            else
            {
                DebugLog.WriteLine($"LoadImagesAsync: All files are indexed, showing {Images.Count} items");
            }
        }
        
        // Load thumbnails in batches (sequential, but fast with Windows cache)
        DebugLog.WriteLine($"LoadImagesAsync: Starting thumbnail loading for {Images.Count} items");
        
        // Build initial queue with all items
        lock (_queueLock)
        {
            _loadQueue.Clear();
            _queuedItems.Clear();
            foreach (var vm in Images)
            {
                _loadQueue.Enqueue(vm);
                _queuedItems.Add(vm);
            }
        }
        
        // Start background processing
        StartBackgroundLoadingIfNeeded();
        
        // Apply grouping to populate GroupedImages collection
        // Use fire-and-forget pattern to avoid blocking
        _ = Task.Run(async () =>
        {
            try
            {
                await ApplyGroupingAsync();
            }
            catch (Exception ex)
            {
                DebugLog.WriteLine($"[ERROR] ApplyGroupingAsync failed: {ex.Message}");
            }
        });
    }
    
    
    /// <summary>
    /// Called when viewport changes in grouped layout.
    /// Reorders thumbnail loading queue to prioritize visible items.
    /// </summary>
    public void OnViewportChangedGrouped(List<ImageThumbnailViewModel> visibleItems)
    {
        // Throttle to avoid excessive reordering
        var now = DateTime.UtcNow;
        if ((now - _lastScrollReorder).TotalMilliseconds < ScrollReorderThrottleMs)
            return;
            
        _lastScrollReorder = now;
        
        lock (_queueLock)
        {
            // Clear and rebuild queue: visible items first, then the rest
            var oldQueue = new List<ImageThumbnailViewModel>(_loadQueue);
            _loadQueue.Clear();
            _queuedItems.Clear();
            
            // Add visible items first
            int visibleCount = 0;
            foreach (var item in visibleItems)
            {
                if (item.Thumbnail == null || item.IsLoading)
                {
                    _loadQueue.Enqueue(item);
                    _queuedItems.Add(item);
                    visibleCount++;
                }
            }
            
            // Add non-visible items
            int nonVisibleCount = 0;
            foreach (var group in GroupedImages)
            {
                foreach (var item in group.Items)
                {
                    if (!_queuedItems.Contains(item) && (item.Thumbnail == null || item.IsLoading))
                    {
                        _loadQueue.Enqueue(item);
                        _queuedItems.Add(item);
                        nonVisibleCount++;
                    }
                }
            }
            
            DebugLog.WriteLine($"[VIEWPORT] Reordered queue: {visibleCount} visible, {nonVisibleCount} non-visible");
        }
    }
    
    /// <summary>
    /// Called from view when scroll position changes. Reorders queue to prioritize visible items.
    /// Now uses polling instead of events to avoid layout cycles.
    /// </summary>
    public void OnViewportChanged(int firstVisibleIndex, int lastVisibleIndex)
    {
        // Throttle queue reordering to avoid excessive work
        var timeSinceLastReorder = (DateTime.UtcNow - _lastScrollReorder).TotalMilliseconds;
        if (timeSinceLastReorder < ScrollReorderThrottleMs)
        {
            return;
        }
        
        _lastScrollReorder = DateTime.UtcNow;
        
        lock (_queueLock)
        {
            if (_queuedItems.Count == 0) return;
            
            // Reorder queue: visible items first, rest stay in order
            // Use vm.Index to avoid accessing ObservableCollection
            var visibleItems = new List<ImageThumbnailViewModel>();
            var nonVisibleItems = new List<ImageThumbnailViewModel>();
            
            foreach (var item in _loadQueue)
            {
                if (item.Index >= firstVisibleIndex && item.Index <= lastVisibleIndex)
                {
                    visibleItems.Add(item);
                }
                else
                {
                    nonVisibleItems.Add(item);
                }
            }
            
            _loadQueue.Clear();
            foreach (var item in visibleItems) _loadQueue.Enqueue(item);
            foreach (var item in nonVisibleItems) _loadQueue.Enqueue(item);
            
            DebugLog.WriteLine($"[VIEWPORT] Reordered queue: {visibleItems.Count} visible, {nonVisibleItems.Count} non-visible");
        }
    }
    
    private void StartBackgroundLoadingIfNeeded()
    {
        if (_backgroundLoadTask?.IsCompleted == false) return; // Already running
        
        var ct = _loadCts?.Token ?? CancellationToken.None;
        _backgroundLoadTask = Task.Run(async () =>
        {
            while (true)
            {
                if (ct.IsCancellationRequested) break;
                
                ImageThumbnailViewModel? vm = null;
                lock (_queueLock)
                {
                    if (_loadQueue.Count == 0)
                    {
                        DebugLog.WriteLine($"[QUEUE] Queue empty, exiting");
                        break;
                    }
                    vm = _loadQueue.Dequeue();
                    _queuedItems.Remove(vm);
                    DebugLog.WriteLine($"[QUEUE] Dequeued item: {vm?.Entry?.FileName ?? "null"}, Thumbnail={vm?.Thumbnail != null}, IsLoading={vm?.IsLoading}");
                }
                
                if (vm != null && vm.Thumbnail == null)
                {
                    await LoadSingleThumbnailAsync(vm, ct);
                }
                else if (vm != null)
                {
                    DebugLog.WriteLine($"[QUEUE] Skipping {vm.Entry?.FileName}: Thumbnail already set");
                }
            }
            
            DebugLog.WriteLine($"[QUEUE] Background loading completed");
        }, ct);
    }
    
    private async Task LoadSingleThumbnailAsync(ImageThumbnailViewModel vm, CancellationToken ct)
    {
        try
        {
            DebugLog.WriteLine($"[LOAD] Loading thumbnail for {vm.Entry.FileName}");
            await s_thumbnailLoadSemaphore.WaitAsync(ct);
            try
            {
                // Check if we're shutting down
                if (ct.IsCancellationRequested)
                {
                    DebugLog.WriteLine($"[LOAD] Cancelled for {vm.Entry.FileName}");
                    return;
                }
                
                // Load thumbnail stream on background thread
                var streamBytes = await WindowsThumbnailService.GetThumbnailStreamAsync(vm.Entry.FilePath, 180);
                DebugLog.WriteLine($"[LOAD] Got {streamBytes?.Length ?? 0} bytes for {vm.Entry.FileName}");
                
                if (streamBytes != null && !ct.IsCancellationRequested)
                {
                    // Marshal to UI thread to create BitmapImage
                    if (App.MainWindow?.DispatcherQueue == null)
                    {
                        DebugLog.WriteLine($"[LOAD] No dispatcher queue for {vm.Entry.FileName}");
                        vm.IsLoading = false;
                        return;
                    }
                    
                    var tcs = new TaskCompletionSource<bool>();
                    App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
                    {
                        try
                        {
                            if (!ct.IsCancellationRequested)
                            {
                                DebugLog.WriteLine($"[LOAD] Creating BitmapImage for {vm.Entry.FileName}");
                                // Create BitmapImage on UI thread
                                var bitmapImage = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                                using (var stream = new MemoryStream(streamBytes))
                                {
                                    await bitmapImage.SetSourceAsync(stream.AsRandomAccessStream());
                                }
                                
                                DebugLog.WriteLine($"[LOAD] Setting Thumbnail directly for {vm.Entry.FileName}");
                                // Apply directly - no buffer/flush needed with custom virtualization
                                vm.Thumbnail = bitmapImage;
                                vm.IsLoading = false;
                            }
                            tcs.SetResult(true);
                        }
                        catch (Exception ex)
                        {
                            DebugLog.WriteLine($"[ERROR] Failed to load thumbnail: {ex.Message}");
                            vm.IsLoading = false;
                            tcs.SetResult(false);
                        }
                    });
                    await tcs.Task;
                }
                else
                {
                    vm.IsLoading = false;
                }
            }
            finally
            {
                s_thumbnailLoadSemaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when switching folders or shutting down
            vm.IsLoading = false;
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"[ERROR] Failed to load thumbnail for {vm.FileName}: {ex.Message}");
            vm.IsLoading = false;
        }
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
            int index = 0;
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
                
                var vm = new ImageThumbnailViewModel(entry, ct) { Index = index };
                viewModels.Add(vm);
                Images.Add(vm); // Add immediately to show spinner
                index++;
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
        await FilterByFoldersAsync(folderPath != null ? new List<string> { folderPath } : null);
    }

    public async Task FilterByFoldersAsync(List<string>? folderPaths)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DebugLog.WriteLine($"FilterByFoldersAsync: folderPaths={{{(folderPaths != null ? string.Join(", ", folderPaths.Select(p => $"'{p}'")) : "null")}}}");
        
        // Pause background indexing while user is browsing
        _main.PauseBackgroundIndexing();
        
        _currentFolderFilters = folderPaths;
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

    public async Task FilterByMultipleCriteriaAsync(
        List<string>? folderPaths,
        bool dateRootSelected,
        List<int>? years,
        List<(int Year, int Month)>? months,
        bool tagRootSelected,
        List<string>? tags,
        bool flaggedSelected = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DebugLog.WriteLine($"FilterByMultipleCriteriaAsync: folders={folderPaths?.Count ?? 0}, years={years?.Count ?? 0}, months={months?.Count ?? 0}, tags={tags?.Count ?? 0}, flagged={flaggedSelected}");
        
        // Pause background indexing while user is browsing
        _main.PauseBackgroundIndexing();
        
        _currentFolderFilters = folderPaths;
        _currentDateRootSelected = dateRootSelected;
        _currentYearFilters = years;
        _currentMonthFilters = months;
        _currentTagRootSelected = tagRootSelected;
        _currentTagFilters = tags;
        _currentFlaggedSelected = flaggedSelected;
        
        DebugLog.WriteLine($"  T+{sw.ElapsedMilliseconds}ms: Starting LoadImagesAsync");
        await LoadImagesAsync();
        DebugLog.WriteLine($"  T+{sw.ElapsedMilliseconds}ms: LoadImagesAsync complete, Images.Count={Images.Count}");
        
        // Apply grouping if enabled (async to avoid UI blocking)
        await ApplyGroupingAsync();
        
        // Update empty state visibility
        if (App.MainWindow?.DispatcherQueue != null)
        {
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                // EmptyState visibility handled in view
            });
        }
    }
    
    /// <summary>
    /// Re-runs grouping/sorting on the current in-memory <see cref="Images"/> collection.
    /// Call after metadata edits that affect sort/group order (e.g. date taken, rating).
    /// Does NOT re-fetch from DB or reload thumbnails — it just re-arranges existing items.
    /// </summary>
    public Task RefreshGroupingAsync() => ApplyGroupingAsync();

    /// <summary>
    /// Forces a single image's thumbnail and metadata to be re-fetched from disk. Call after
    /// in-place edits to a file (e.g. crop, rotate) so the grid shows the new content.
    /// </summary>
    public async Task RefreshSingleImageAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        var vm = Images.FirstOrDefault(v => string.Equals(v.Entry.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (vm == null) return;
        vm.InvalidateThumbnail();
        var streamBytes = await WindowsThumbnailService.GetThumbnailStreamAsync(filePath, 180);
        if (streamBytes != null)
        {
            await vm.LoadThumbnailFromStreamAsync(streamBytes);
        }
    }

    /// <summary>
    /// Groups the Images collection into GroupedImages based on the current GroupBy setting.
    /// Sorts items within each group based on SortBy setting.
    /// Runs on background thread to avoid UI blocking with large collections.
    /// </summary>
    private async Task ApplyGroupingAsync()
    {
        var currentGroupBy = GroupBy;
        var currentSortBy = SortBy;
        var currentSortDesc = SortDescending;
        var imageCount = Images.Count;
        
        DebugLog.WriteLine($"[GROUPING] ApplyGroupingAsync called, GroupBy={currentGroupBy}, Images.Count={imageCount}");
        
        if (imageCount == 0)
        {
            if (App.MainWindow?.DispatcherQueue != null)
            {
                var tcs = new TaskCompletionSource<bool>();
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    GroupedImages.Clear();
                    tcs.SetResult(true);
                });
                await tcs.Task;
            }
            DebugLog.WriteLine($"[GROUPING] No images to group");
            return;
        }
        
        // Do expensive grouping/sorting on background thread
        var groups = await Task.Run(() =>
        {
            try
            {
                // Take snapshot of Images collection
                var imageSnapshot = Images.ToList();
                
                if (currentGroupBy == GroupByOption.None)
                {
                    // No grouping - single flat group. Still apply sort so dates/ratings reorder
                    // after edits without requiring a full DB reload.
                    var sortedFlat = ApplySortToGroup(imageSnapshot, currentSortBy, currentSortDesc);
                    return new List<PhotoGroup>
                    {
                        new PhotoGroup
                        {
                            Header = $"All Photos ({sortedFlat.Count:N0})",
                            Items = new ObservableCollection<ImageThumbnailViewModel>(sortedFlat)
                        }
                    };
                }
                
                // Group images based on GroupBy option
                var groupedData = currentGroupBy switch
                {
                    GroupByOption.FileType => GroupByFileType(imageSnapshot),
                    GroupByOption.MediaType => GroupByMediaType(imageSnapshot),
                    GroupByOption.YearTaken => GroupByYearTaken(imageSnapshot),
                    GroupByOption.MonthTaken => GroupByMonthTaken(imageSnapshot),
                    GroupByOption.FileSize => GroupByFileSize(imageSnapshot),
                    GroupByOption.ImageSize => GroupByImageSize(imageSnapshot),
                    GroupByOption.Rating => GroupByRating(imageSnapshot),
                    GroupByOption.Camera => GroupByCamera(imageSnapshot),
                    _ => new List<(string header, List<ImageThumbnailViewModel> items)>()
                };
                
                // Sort items within each group and create PhotoGroup objects
                var result = new List<PhotoGroup>();
                foreach (var group in groupedData)
                {
                    var sortedItems = ApplySortToGroup(group.items, currentSortBy, currentSortDesc);
                    result.Add(new PhotoGroup
                    {
                        Header = $"{group.header} ({group.items.Count:N0})",
                        Items = new ObservableCollection<ImageThumbnailViewModel>(sortedItems)
                    });
                }
                
                return result;
            }
            catch (Exception ex)
            {
                DebugLog.WriteLine($"[GROUPING] Error: {ex.Message}");
                return new List<PhotoGroup>();
            }
        });
        
        // Update UI on main thread
        if (App.MainWindow?.DispatcherQueue != null)
        {
            var tcs = new TaskCompletionSource<bool>();
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    GroupedImages.Clear();
                    foreach (var group in groups)
                    {
                        GroupedImages.Add(group);
                    }
                    DebugLog.WriteLine($"[GROUPING] Added {GroupedImages.Count} groups to UI");
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    DebugLog.WriteLine($"[GROUPING] Error updating UI: {ex.Message}");
                    tcs.SetException(ex);
                }
            });
            await tcs.Task;
        }
    }
    
    private List<(string header, List<ImageThumbnailViewModel> items)> GroupByFileType(List<ImageThumbnailViewModel> items)
    {
        return Images
            .GroupBy(vm => Path.GetExtension(vm.Entry.FilePath).ToUpperInvariant())
            .OrderBy(g => g.Key)
            .Select(g => (g.Key.TrimStart('.') + " Files", g.ToList()))
            .ToList();
    }
    
    private List<(string header, List<ImageThumbnailViewModel> items)> GroupByMediaType(List<ImageThumbnailViewModel> items)
    {
        return items
            .GroupBy(vm => vm.Entry.MediaType)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key == MediaType.Video ? "Videos" : "Images", g.ToList()))
            .ToList();
    }
    
    private List<(string header, List<ImageThumbnailViewModel> items)> GroupByYearTaken(List<ImageThumbnailViewModel> items)
    {
        return items
            .GroupBy(vm => vm.Entry.DateTaken?.Year ?? 0)
            .OrderByDescending(g => g.Key)
            .Select(g => (g.Key == 0 ? "Unknown Date" : g.Key.ToString(), g.ToList()))
            .ToList();
    }
    
    private List<(string header, List<ImageThumbnailViewModel> items)> GroupByMonthTaken(List<ImageThumbnailViewModel> items)
    {
        return items
            .GroupBy(vm => vm.Entry.DateTaken != null 
                ? new DateTime(vm.Entry.DateTaken.Value.Year, vm.Entry.DateTaken.Value.Month, 1)
                : DateTime.MinValue)
            .OrderByDescending(g => g.Key)
            .Select(g => (g.Key == DateTime.MinValue ? "Unknown Date" : g.Key.ToString("MMMM yyyy"), g.ToList()))
            .ToList();
    }
    
    private List<(string header, List<ImageThumbnailViewModel> items)> GroupByFileSize(List<ImageThumbnailViewModel> items)
    {
        return items
            .GroupBy(vm =>
            {
                var sizeMB = vm.Entry.FileSize / (1024.0 * 1024.0);
                return sizeMB switch
                {
                    > 10 => (0, "Large (>10 MB)"),
                    > 1 => (1, "Medium (1-10 MB)"),
                    _ => (2, "Small (<1 MB)")
                };
            })
            .OrderBy(g => g.Key.Item1)
            .Select(g => (g.Key.Item2, g.ToList()))
            .ToList();
    }
    
    private List<(string header, List<ImageThumbnailViewModel> items)> GroupByImageSize(List<ImageThumbnailViewModel> items)
    {
        return items
            .GroupBy(vm =>
            {
                var megapixels = (vm.Entry.Width * vm.Entry.Height) / 1_000_000.0;
                return megapixels switch
                {
                    > 8 => (0, "4K+ (>8 MP)"),
                    > 2 => (1, "Full HD (2-8 MP)"),
                    _ => (2, "HD (<2 MP)")
                };
            })
            .OrderBy(g => g.Key.Item1)
            .Select(g => (g.Key.Item2, g.ToList()))
            .ToList();
    }
    
    private List<(string header, List<ImageThumbnailViewModel> items)> GroupByRating(List<ImageThumbnailViewModel> items)
    {
        return items
            .GroupBy(vm => vm.Entry.Rating ?? 0)
            .OrderByDescending(g => g.Key)
            .Select(g => (g.Key == 0 ? "Unrated" : $"{g.Key} Stars", g.ToList()))
            .ToList();
    }
    
    private List<(string header, List<ImageThumbnailViewModel> items)> GroupByCamera(List<ImageThumbnailViewModel> items)
    {
        return items
            .GroupBy(vm => string.IsNullOrWhiteSpace(vm.Entry.CameraModel) 
                ? "Unknown Camera" 
                : vm.Entry.CameraModel)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, g.ToList()))
            .ToList();
    }
    
    private List<ImageThumbnailViewModel> ApplySortToGroup(List<ImageThumbnailViewModel> items, SortByOption sortBy, bool sortDescending)
    {
        var sorted = sortBy switch
        {
            SortByOption.FileName => items.OrderBy(vm => vm.Entry.FileName),
            SortByOption.DateTaken => items.OrderBy(vm => vm.Entry.DateTaken ?? DateTime.MaxValue),
            SortByOption.DateModified => items.OrderBy(vm => vm.Entry.DateModified),
            SortByOption.FileSize => items.OrderBy(vm => vm.Entry.FileSize),
            SortByOption.Rating => items.OrderBy(vm => vm.Entry.Rating ?? 0),
            _ => items.AsEnumerable()
        };
        
        if (sortDescending)
            sorted = sorted.Reverse();
            
        return sorted.ToList();
    }

    [RelayCommand]
    private async Task SortByDateAsync()
    {
        _currentSortBy = "date_taken";
        SortDescending = true;
        SortField = "Date Taken";
        await LoadImagesAsync();
    }

    [RelayCommand]
    private async Task SortByNameAsync()
    {
        _currentSortBy = "file_name";
        SortDescending = false;
        SortField = "Name";
        await LoadImagesAsync();
    }

    [RelayCommand]
    private async Task SortByRatingAsync()
    {
        _currentSortBy = "rating";
        SortDescending = true;
        SortField = "Rating";
        await LoadImagesAsync();
    }

    [RelayCommand]
    private async Task ClearFilter()
    {
        _currentFolderFilters = null;
        _currentDateRootSelected = false;
        _currentYearFilters = null;
        _currentMonthFilters = null;
        _currentTagRootSelected = false;
        _currentTagFilters = null;
        _currentFlaggedSelected = false;
        Images.Clear();
        SelectedImages.Clear();
        SelectedImage = null;
        await ApplyGroupingAsync();
        // Don't set status text here - let MainViewModel handle it
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
        // Push selection (single or multi) to the metadata panel.
        if (SelectedImages.Count > 0)
        {
            _main.MetadataPanel.ShowMetadata(SelectedImages.Select(vm => vm.Entry).ToList());
        }
        else if (value is not null)
        {
            _main.MetadataPanel.ShowMetadata(new[] { value.Entry });
        }
        else
        {
            _main.MetadataPanel.Clear();
        }
    }

    /// <summary>
    /// Called by ImageGridView when the grid's SelectionChanged event fires.
    /// Updates the multi-select list and routes the primary item through OnSelectedImageChanged.
    /// </summary>
    public void UpdateSelection(IReadOnlyList<ImageThumbnailViewModel> selected, ImageThumbnailViewModel? primary)
    {
        SelectedImages.Clear();
        SelectedImages.AddRange(selected);
        // Setting SelectedImage drives the metadata panel via OnSelectedImageChanged
        SelectedImage = primary;
    }

    [RelayCommand]
    private void OpenViewer()
    {
        if (SelectedImage is not null)
        {
            _main.ImageViewer.OpenImage(SelectedImage.Entry, Images.Select(i => i.Entry).ToList());
        }
    }

    /// <summary>
    /// Returns the items a flag action applies to: the multi-selection when there is one,
    /// otherwise the single selected item.
    /// </summary>
    private List<ImageThumbnailViewModel> GetFlagTargets()
    {
        if (SelectedImages.Count > 0) return SelectedImages.ToList();
        return SelectedImage is null ? new List<ImageThumbnailViewModel>() : new() { SelectedImage };
    }

    /// <summary>
    /// Toggles the flag on the current selection (keyboard shortcut: F). Mixed selections are
    /// flagged first — a second press clears them, matching Photo Gallery's toggle behaviour.
    /// </summary>
    [RelayCommand]
    private async Task ToggleFlagAsync()
    {
        var targets = GetFlagTargets();
        if (targets.Count == 0) return;

        bool newValue = !targets.All(t => t.Entry.IsFlagged);
        await SetFlagAsync(targets, newValue);
    }

    /// <summary>
    /// Explicitly sets the flag on the given grid items (used by the context menu).
    /// </summary>
    public async Task SetFlagAsync(IReadOnlyList<ImageThumbnailViewModel> targets, bool flagged)
    {
        if (targets.Count == 0) return;

        await _main.SetFlagAsync(targets.Select(t => t.Entry).ToList(), flagged);

        foreach (var t in targets) t.NotifyFlagChanged();

        // The flagged working set is a live filter — drop items that no longer qualify.
        if (_currentFlaggedSelected && !flagged)
            await LoadImagesAsync();
    }

    /// <summary>
    /// Re-reads the flag state of the grid item for the given path (used after the viewer or
    /// metadata panel changes a flag) so the thumbnail badge stays in sync.
    /// </summary>
    public void NotifyFlagChanged(string filePath)
    {
        var vm = Images.FirstOrDefault(v => string.Equals(v.Entry.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        vm?.NotifyFlagChanged();
    }
}

public partial class ImageThumbnailViewModel : ObservableObject
{
    private readonly CancellationToken _cancellationToken;
    private bool _thumbnailLoaded;

    public ImageEntry Entry { get; }
    
    /// <summary>
    /// Current index in the Images collection. Updated when items are added/removed.
    /// Used for viewport-aware loading without touching ObservableCollection during scroll.
    /// </summary>
    public int Index { get; set; }

    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Media.ImageSource? Thumbnail { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public string FileName => Entry.FileName;
    public string DateDisplay => Entry.DateTaken?.ToString("MMM d, yyyy") ?? "";
    public int? Rating => Entry.Rating;
    public bool IsFlagged => Entry.IsFlagged;
    public Microsoft.UI.Xaml.Visibility FlagVisibility => IsFlagged ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public bool IsVideo => Entry.MediaType == MediaType.Video;
    public Microsoft.UI.Xaml.Visibility VideoIconVisibility => IsVideo ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public ImageThumbnailViewModel(ImageEntry entry, CancellationToken cancellationToken = default)
    {
        Entry = entry;
        _cancellationToken = cancellationToken;

        IsLoading = true; // This initial set is OK (happens before binding)
    }

    /// <summary>
    /// Raises change notifications for the flag-derived properties after <see cref="Entry"/>'s
    /// flag has been updated, so the thumbnail badge repaints.
    /// </summary>
    public void NotifyFlagChanged()
    {
        OnPropertyChanged(nameof(IsFlagged));
        OnPropertyChanged(nameof(FlagVisibility));
    }

    /// <summary>
    /// Resets the thumbnail-loaded flag so the next call to <see cref="LoadThumbnailAsync"/>
    /// or <see cref="LoadThumbnailFromStreamAsync"/> re-fetches the bitmap from disk. Used
    /// after in-place edits (crop, rotate) so the grid reflects the new image content.
    /// </summary>
    public void InvalidateThumbnail()
    {
        _thumbnailLoaded = false;
        Thumbnail = null;
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
