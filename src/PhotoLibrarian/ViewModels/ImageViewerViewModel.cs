using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Collections.ObjectModel;

namespace PhotoLibrarian.ViewModels;

public partial class ImageViewerViewModel : ObservableObject
{
    private List<ImageEntry> _allImages = [];
    private int _currentIndex;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial BitmapImage? CurrentImage { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string ImageInfo { get; set; }

    [ObservableProperty]
    public partial double ZoomFactor { get; set; }

    [ObservableProperty]
    public partial bool IsVideo { get; set; }

    [ObservableProperty]
    public partial string? VideoPath { get; set; }

    /// <summary>
    /// The currently-viewed image entry, or null if the viewer is closed or showing a video.
    /// Used by the ribbon/crop overlay to know which file to act on.
    /// </summary>
    public ImageEntry? CurrentEntry =>
        IsOpen && _currentIndex >= 0 && _currentIndex < _allImages.Count
            ? _allImages[_currentIndex]
            : null;

    public ImageViewerViewModel()
    {
        Title = "";
        ImageInfo = "";
        ZoomFactor = 1.0;
    }

    public void OpenImage(ImageEntry entry, List<ImageEntry> allImages)
    {
        _allImages = allImages;
        _currentIndex = allImages.IndexOf(entry);
        if (_currentIndex < 0) _currentIndex = 0;
        IsOpen = true;
        OnPropertyChanged(nameof(CurrentEntry));
        _ = LoadCurrentImageAsync();
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        CurrentImage = null;
        VideoPath = null;
        IsVideo = false;
        OnPropertyChanged(nameof(CurrentEntry));
    }

    /// <summary>
    /// Reload the current image from disk (used after edits like crop that change the file content).
    /// </summary>
    public async Task ReloadCurrentImageAsync()
    {
        await LoadCurrentImageAsync();
    }

    [RelayCommand]
    private async Task NextImageAsync()
    {
        if (_allImages.Count == 0) return;
        _currentIndex = (_currentIndex + 1) % _allImages.Count;
        await LoadCurrentImageAsync();
    }

    [RelayCommand]
    private async Task PreviousImageAsync()
    {
        if (_allImages.Count == 0) return;
        _currentIndex = (_currentIndex - 1 + _allImages.Count) % _allImages.Count;
        await LoadCurrentImageAsync();
    }

    [RelayCommand]
    private void ZoomIn()
    {
        ZoomFactor = Math.Min(ZoomFactor * 1.25, 10.0);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        ZoomFactor = Math.Max(ZoomFactor / 1.25, 0.1);
    }

    [RelayCommand]
    private void ZoomFit()
    {
        ZoomFactor = 1.0;
    }

    private async Task LoadCurrentImageAsync()
    {
        if (_currentIndex < 0 || _currentIndex >= _allImages.Count) return;

        var entry = _allImages[_currentIndex];
        Title = entry.FileName;
        ImageInfo = $"{_currentIndex + 1} / {_allImages.Count}";
        OnPropertyChanged(nameof(CurrentEntry));

        if (entry.MediaType == MediaType.Video)
        {
            IsVideo = true;
            VideoPath = entry.FilePath;
            CurrentImage = null;
            return;
        }

        IsVideo = false;
        VideoPath = null;

        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(entry.FilePath);
            using var stream = await file.OpenReadAsync();
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            CurrentImage = bmp;
            ZoomFactor = 1.0;
        }
        catch
        {
            CurrentImage = null;
        }
    }
}
