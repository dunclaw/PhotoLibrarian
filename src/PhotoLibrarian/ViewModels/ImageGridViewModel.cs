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
    private readonly ThumbnailRepository _thumbRepo;
    private readonly ThumbnailService _thumbnailService;
    private readonly FolderScannerService _scanner;
    private readonly MetadataReaderService _metadataReader;
    private readonly MainViewModel _main;
    private string? _currentFolderFilter;
    private string? _currentSortBy = "date_taken";
    private bool _sortDescending = true;
    private CancellationTokenSource? _loadCts;

    public ObservableCollection<ImageThumbnailViewModel> Images { get; } = [];

    [ObservableProperty]
    public partial ImageThumbnailViewModel? SelectedImage { get; set; }

    [ObservableProperty]
    public partial double ThumbnailSize { get; set; }

    [ObservableProperty]
    public partial string SortField { get; set; }

    public ImageGridViewModel(
        ImageRepository imageRepo,
        ThumbnailRepository thumbRepo,
        ThumbnailService thumbnailService,
        FolderScannerService scanner,
        MetadataReaderService metadataReader,
        MainViewModel main)
    {
        _imageRepo = imageRepo;
        _thumbRepo = thumbRepo;
        _thumbnailService = thumbnailService;
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
        Images.Clear();

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

            Images.Add(new ImageThumbnailViewModel(img, _thumbRepo, _thumbnailService, ct));
        }
        
        if (skipCount > 2)
            DebugLog.WriteLine($"  ... and {skipCount - 2} more skipped");
        
        DebugLog.WriteLine($"LoadImagesAsync: Added {Images.Count} images to collection (matched {matchCount}, skipped {skipCount})");

        // If filtering by folder and no indexed images found, scan folder directly
        if (filter is not null && Images.Count == 0 && Directory.Exists(filter.TrimEnd(Path.DirectorySeparatorChar)))
        {
            DebugLog.WriteLine($"LoadImagesAsync: No indexed images found, scanning folder directly...");
            await ScanFolderDirectlyAsync(filter.TrimEnd(Path.DirectorySeparatorChar), ct);
        }
    }

    private async Task ScanFolderDirectlyAsync(string folderPath, CancellationToken ct)
    {
        try
        {
            int count = 0;
            await foreach (var filePath in _scanner.ScanFolderAsync(folderPath, includeSubfolders: true, ct))
            {
                if (ct.IsCancellationRequested) break;

                count++;
                if (count <= 3)
                    DebugLog.WriteLine($"  Found: '{filePath}'");
                
                // Create a minimal ImageEntry for display
                var entry = _metadataReader.ReadMetadata(filePath);
                Images.Add(new ImageThumbnailViewModel(entry, _thumbRepo, _thumbnailService, ct));

                // Limit to prevent UI freeze on very large folders
                if (count >= 100) break; // Reduced from 500 to 100
            }
            if (count > 3)
                DebugLog.WriteLine($"  ... and {count - 3} more files");
            DebugLog.WriteLine($"LoadImagesAsync: Added {count} images from direct scan");
        }
        catch (OperationCanceledException)
        {
            DebugLog.WriteLine($"LoadImagesAsync: Direct scan cancelled");
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"LoadImagesAsync: Error scanning folder - {ex.Message}");
        }
    }

    public async Task FilterByFolderAsync(string? folderPath)
    {
        DebugLog.WriteLine($"FilterByFolderAsync: folderPath='{folderPath ?? "null"}'");
        
        // Pause background indexing while user is browsing
        _main.PauseBackgroundIndexing();
        
        _currentFolderFilter = folderPath;
        await LoadImagesAsync();
        DebugLog.WriteLine($"FilterByFolderAsync: LoadImagesAsync complete, Images.Count={Images.Count}");
        
        // Resume background indexing after a delay
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000); // Wait 5 seconds
            _main.StartBackgroundIndexing();
        });
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
    private readonly ThumbnailRepository _thumbRepo;
    private readonly ThumbnailService _thumbnailService;
    private readonly CancellationToken _cancellationToken;
    private bool _thumbnailLoaded;

    public ImageEntry Entry { get; }

    [ObservableProperty]
    public partial BitmapImage? Thumbnail { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public string FileName => Entry.FileName;
    public string DateDisplay => Entry.DateTaken?.ToString("MMM d, yyyy") ?? "";
    public int? Rating => Entry.Rating;
    public bool IsVideo => Entry.MediaType == MediaType.Video;

    public ImageThumbnailViewModel(ImageEntry entry, ThumbnailRepository thumbRepo, ThumbnailService thumbnailService, CancellationToken cancellationToken = default)
    {
        Entry = entry;
        _thumbRepo = thumbRepo;
        _thumbnailService = thumbnailService;
        _cancellationToken = cancellationToken;

        IsLoading = true;
    }

    /// <summary>
    /// Lazy-loads the thumbnail when the item becomes visible in the grid.
    /// </summary>
    public async Task LoadThumbnailAsync()
    {
        if (_thumbnailLoaded)
        {
            return;
        }
        
        _thumbnailLoaded = true;

        try
        {
            // Check cancellation before starting
            if (_cancellationToken.IsCancellationRequested)
            {
                IsLoading = false;
                return;
            }

            byte[]? data;
            
            // If image is indexed (has valid ID), use cache
            if (Entry.Id > 0)
            {
                data = await _thumbnailService.GetOrCreateThumbnailAsync(
                    Entry.Id, Entry.FilePath, ThumbnailSize.Small);
            }
            else
            {
                // For unindexed images, generate thumbnail without caching
                data = await ThumbnailService.GenerateThumbnailAsync(Entry.FilePath, (int)ThumbnailSize.Small);
            }

            // Check cancellation before updating UI
            if (_cancellationToken.IsCancellationRequested)
            {
                IsLoading = false;
                return;
            }

            if (data is not null)
            {
                var bmp = new BitmapImage();
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await stream.WriteAsync(data.AsBuffer());
                stream.Seek(0);
                await bmp.SetSourceAsync(stream);
                Thumbnail = bmp;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when switching folders
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"LoadThumbnailAsync EXCEPTION for '{Entry.FilePath}': {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }
}
