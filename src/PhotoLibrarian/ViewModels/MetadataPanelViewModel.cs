using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Collections.ObjectModel;

namespace PhotoLibrarian.ViewModels;

public partial class MetadataPanelViewModel : ObservableObject
{
    private ImageEntry? _currentEntry;
    private ImageRepository? _imageRepo;
    private TagRepository? _tagRepo;

    [ObservableProperty]
    public partial bool HasImage { get; set; }

    // --- Editable fields ---

    [ObservableProperty]
    public partial int Rating { get; set; }

    [ObservableProperty]
    public partial string Caption { get; set; }

    public ObservableCollection<string> Tags { get; } = [];

    // --- Read-only display fields ---

    [ObservableProperty]
    public partial string FileName { get; set; }

    [ObservableProperty]
    public partial string FilePath { get; set; }

    [ObservableProperty]
    public partial string FileSize { get; set; }

    [ObservableProperty]
    public partial string Dimensions { get; set; }

    [ObservableProperty]
    public partial string DateTaken { get; set; }

    [ObservableProperty]
    public partial string Camera { get; set; }

    [ObservableProperty]
    public partial string Lens { get; set; }

    [ObservableProperty]
    public partial string Exposure { get; set; }

    [ObservableProperty]
    public partial string FocalLength { get; set; }

    [ObservableProperty]
    public partial string Aperture { get; set; }

    [ObservableProperty]
    public partial string Iso { get; set; }

    [ObservableProperty]
    public partial string GpsLatitude { get; set; }

    [ObservableProperty]
    public partial string GpsLongitude { get; set; }

    [ObservableProperty]
    public partial string MediaType { get; set; }

    [ObservableProperty]
    public partial string Author { get; set; }

    public MetadataPanelViewModel()
    {
        FileName = "";
        FilePath = "";
        FileSize = "";
        Dimensions = "";
        DateTaken = "";
        Camera = "";
        Lens = "";
        Exposure = "";
        FocalLength = "";
        Aperture = "";
        Iso = "";
        GpsLatitude = "";
        GpsLongitude = "";
        MediaType = "";
        Caption = "";
        Author = "";
    }

    /// <summary>
    /// Injects repository dependencies (called from MainViewModel constructor).
    /// </summary>
    public void Initialize(ImageRepository imageRepo, TagRepository tagRepo)
    {
        _imageRepo = imageRepo;
        _tagRepo = tagRepo;
    }

    public async void ShowMetadata(ImageEntry entry)
    {
        _currentEntry = entry;
        HasImage = true;
        FileName = entry.FileName;
        FilePath = entry.FilePath;
        FileSize = FormatFileSize(entry.FileSize);
        Dimensions = entry.Width > 0 ? $"{entry.Width} x {entry.Height}" : "";
        DateTaken = entry.DateTaken?.ToString("M/d/yyyy h:mm tt") ?? "";
        Camera = FormatCamera(entry.CameraMake, entry.CameraModel);
        Lens = entry.LensModel ?? "";
        Exposure = entry.ExposureTime != null ? $"{entry.ExposureTime} sec" : "";
        FocalLength = entry.FocalLength.HasValue ? $"{entry.FocalLength:F1} mm" : "";
        Aperture = entry.Aperture.HasValue ? $"f/{entry.Aperture:F1}" : "";
        Iso = entry.Iso?.ToString() ?? "";
        GpsLatitude = entry.GpsLatitude.HasValue ? $"{entry.GpsLatitude:F4}" : "";
        GpsLongitude = entry.GpsLongitude.HasValue ? $"{entry.GpsLongitude:F4}" : "";
        Rating = entry.Rating ?? 0;
        MediaType = entry.MediaType == Core.Models.MediaType.Video ? "Video" : "Photo";
        Author = ""; // Not currently stored in ImageEntry

        // Load caption from XMP sidecar
        Caption = MetadataWriterService.ReadCaptionFromSidecar(entry.FilePath) ?? "";

        // Load tags from database
        Tags.Clear();
        if (_tagRepo != null && entry.Id > 0)
        {
            var tags = await _tagRepo.GetTagsAsync(entry.Id);
            foreach (var tag in tags)
            {
                Tags.Add(tag.Tag);
            }
        }
    }

    public void Clear()
    {
        _currentEntry = null;
        HasImage = false;
        FileName = "";
        Tags.Clear();
    }

    /// <summary>
    /// Sets the rating (1-5). If the same value is already set, clears it (toggle behavior).
    /// </summary>
    [RelayCommand]
    public async Task SetRatingAsync(int star)
    {
        if (_currentEntry == null) return;

        // Toggle: clicking the current rating clears it
        int newRating = (Rating == star) ? 0 : star;
        Rating = newRating;
        _currentEntry.Rating = newRating > 0 ? newRating : null;

        // Persist to XMP sidecar
        await MetadataWriterService.WriteRatingAsync(_currentEntry.FilePath, _currentEntry.Rating);

        // Update database cache
        if (_imageRepo != null && _currentEntry.Id > 0)
        {
            await _imageRepo.UpdateRatingAsync(_currentEntry.Id, _currentEntry.Rating);
        }
    }

    /// <summary>
    /// Saves the current caption to the XMP sidecar.
    /// Called when user finishes editing the caption (LostFocus).
    /// </summary>
    [RelayCommand]
    public async Task SaveCaptionAsync()
    {
        if (_currentEntry == null) return;
        await MetadataWriterService.WriteCaptionAsync(_currentEntry.FilePath, Caption);
    }

    /// <summary>
    /// Adds a new tag to the current image.
    /// </summary>
    [RelayCommand]
    public async Task AddTagAsync(string tag)
    {
        if (_currentEntry == null || string.IsNullOrWhiteSpace(tag)) return;
        
        var trimmed = tag.Trim();
        if (Tags.Contains(trimmed)) return;

        Tags.Add(trimmed);

        // Persist to database
        if (_tagRepo != null && _currentEntry.Id > 0)
        {
            await _tagRepo.AddTagAsync(new ImageTag
            {
                ImageId = _currentEntry.Id,
                Tag = trimmed,
                Source = TagSource.Manual,
                Confidence = 1.0f
            });
        }

        // Write to XMP sidecar
        await TagWriterService.WriteTagsToSidecarAsync(_currentEntry.FilePath, Tags);
    }

    /// <summary>
    /// Removes a tag from the current image.
    /// </summary>
    [RelayCommand]
    public async Task RemoveTagAsync(string tag)
    {
        if (_currentEntry == null || string.IsNullOrWhiteSpace(tag)) return;

        Tags.Remove(tag);

        // Remove from database
        if (_tagRepo != null && _currentEntry.Id > 0)
        {
            await _tagRepo.RemoveTagAsync(_currentEntry.Id, tag);
        }

        // Update XMP sidecar
        await TagWriterService.WriteTagsToSidecarAsync(_currentEntry.FilePath, Tags);
    }

    private static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F2} MB",
            _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
        };
    }

    private static string FormatCamera(string? make, string? model)
    {
        if (string.IsNullOrEmpty(make) && string.IsNullOrEmpty(model))
            return "";
        if (string.IsNullOrEmpty(make))
            return model!;
        if (model?.StartsWith(make, StringComparison.OrdinalIgnoreCase) == true)
            return model;
        return $"{make} {model}";
    }
}

