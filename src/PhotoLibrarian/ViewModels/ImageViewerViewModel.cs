// Copyright (c) Duncan Lawler. All Rights Reserved.
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Diagnostics;

namespace PhotoLibrarian.ViewModels;

public partial class ImageViewerViewModel : ObservableObject
{
    private List<ImageEntry> _allImages = [];
    private int _currentIndex;
    private int _loadVersion;
    private string? _pendingExportPath;

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
    /// The currently-viewed photo or video entry, or null if the viewer is closed.
    /// Used by the ribbon/crop overlay to know which file to act on.
    /// </summary>
    public ImageEntry? CurrentEntry =>
        IsOpen && _currentIndex >= 0 && _currentIndex < _allImages.Count
            ? _allImages[_currentIndex]
            : null;

    /// <summary>The photos the viewer is stepping through (the grid's filtered set when opened).</summary>
    public IReadOnlyList<ImageEntry> Images => _allImages;

    /// <summary>
    /// Raised whenever the viewer moves to a different image (or closes, with null). Lets the
    /// metadata panel follow the image on screen instead of staying on the grid selection.
    /// </summary>
    public event Action<ImageEntry?>? CurrentEntryChanged;

    private void RaiseCurrentEntryChanged()
    {
        OnPropertyChanged(nameof(CurrentEntry));
        CurrentEntryChanged?.Invoke(CurrentEntry);
    }

    public ImageViewerViewModel()
    {
        Title = "";
        ImageInfo = "";
        ZoomFactor = 1.0;
    }

    public void OpenImage(ImageEntry entry, List<ImageEntry> allImages)
    {
        _allImages = allImages.ToList();
        _currentIndex = _allImages.FindIndex(image =>
            string.Equals(image.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase));
        if (_currentIndex < 0)
        {
            _currentIndex = _allImages.Count;
            _allImages.Add(entry);
        }
        IsOpen = true;
        CurrentImage = null;
        _ = LoadCurrentImageAsync();
    }

    public void OpenExportedVideo(ImageEntry exportedEntry)
    {
        ArgumentNullException.ThrowIfNull(exportedEntry);
        if (exportedEntry.MediaType != MediaType.Video)
            throw new ArgumentException("The exported copy must be a video.", nameof(exportedEntry));
        var path = exportedEntry.FilePath;
        var entries = _allImages.ToList();
        var index = entries.FindIndex(image =>
            string.Equals(image.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            entries[index] = exportedEntry;
        else
            entries.Insert(Math.Min(_currentIndex + 1, entries.Count), exportedEntry);
        _pendingExportPath = path;
        OpenImage(exportedEntry, entries);
    }

    public void UpdateLibraryImages(List<ImageEntry> images)
    {
        var current = CurrentEntry;
        if (current is null) return;

        _allImages = images.ToList();
        _currentIndex = images.FindIndex(image =>
            string.Equals(image.FilePath, current.FilePath, StringComparison.OrdinalIgnoreCase));
        bool isPendingExport = string.Equals(current.FilePath, _pendingExportPath, StringComparison.OrdinalIgnoreCase);
        if (_currentIndex >= 0 && isPendingExport)
            _pendingExportPath = null;

        if (_currentIndex < 0 && isPendingExport)
        {
            // A just-exported copy stays open until the library index catches up with it.
            _currentIndex = _allImages.Count;
            _allImages.Add(current);
        }
        else if (_allImages.Count == 0)
        {
            Close();
            return;
        }
        else if (_currentIndex < 0)
        {
            _currentIndex = 0;
            CurrentImage = null;
            _ = LoadCurrentImageAsync();
            return;
        }
        ImageInfo = $"{_currentIndex + 1} / {_allImages.Count}";
        RaiseCurrentEntryChanged();
    }

    [RelayCommand]
    private void Close()
    {
        _loadVersion++;
        _pendingExportPath = null;
        IsOpen = false;
        CurrentImage = null;
        VideoPath = null;
        IsVideo = false;
        RaiseCurrentEntryChanged();
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
        if (!IsOpen || _currentIndex < 0 || _currentIndex >= _allImages.Count) return;

        var loadVersion = ++_loadVersion;
        var entry = _allImages[_currentIndex];
        Title = entry.FileName;
        ImageInfo = $"{_currentIndex + 1} / {_allImages.Count}";
        RaiseCurrentEntryChanged();

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
            if (loadVersion != _loadVersion) return;
            using var stream = await file.OpenReadAsync();
            if (loadVersion != _loadVersion) return;
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            if (loadVersion != _loadVersion) return;
            CurrentImage = bmp;
            ZoomFactor = 1.0;
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"Failed to load viewer image '{entry.FilePath}': {ex}");
            if (loadVersion == _loadVersion)
                CurrentImage = null;
        }
    }
}
