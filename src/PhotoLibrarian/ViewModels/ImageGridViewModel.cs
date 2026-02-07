using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PhotoLibrarian.ViewModels;

public partial class ImageGridViewModel : ObservableObject
{
    private readonly ImageRepository _imageRepo;
    private readonly ThumbnailRepository _thumbRepo;
    private readonly ThumbnailService _thumbnailService;
    private readonly MainViewModel _main;
    private string? _currentFolderFilter;
    private string? _currentSortBy = "date_taken";
    private bool _sortDescending = true;

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
        MainViewModel main)
    {
        _imageRepo = imageRepo;
        _thumbRepo = thumbRepo;
        _thumbnailService = thumbnailService;
        _main = main;

        ThumbnailSize = 180;
        SortField = "Date Taken";
    }

    public async Task LoadImagesAsync()
    {
        var images = await _imageRepo.GetAllAsync(_currentSortBy, _sortDescending);
        Images.Clear();

        foreach (var img in images)
        {
            if (_currentFolderFilter is not null &&
                !img.FilePath.StartsWith(_currentFolderFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            Images.Add(new ImageThumbnailViewModel(img, _thumbRepo, _thumbnailService));
        }
    }

    public async Task FilterByFolderAsync(string? folderPath)
    {
        _currentFolderFilter = folderPath;
        await LoadImagesAsync();
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

    public ImageThumbnailViewModel(ImageEntry entry, ThumbnailRepository thumbRepo, ThumbnailService thumbnailService)
    {
        Entry = entry;
        _thumbRepo = thumbRepo;
        _thumbnailService = thumbnailService;

        IsLoading = true;
    }

    /// <summary>
    /// Lazy-loads the thumbnail when the item becomes visible in the grid.
    /// </summary>
    public async Task LoadThumbnailAsync()
    {
        if (_thumbnailLoaded) return;
        _thumbnailLoaded = true;

        try
        {
            var data = await _thumbnailService.GetOrCreateThumbnailAsync(
                Entry.Id, Entry.FilePath, ThumbnailSize.Small);

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
        catch { /* Thumbnail generation failed */ }
        finally
        {
            IsLoading = false;
        }
    }
}
