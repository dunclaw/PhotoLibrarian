using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Collections.ObjectModel;

namespace PhotoLibrarian.ViewModels;

/// <summary>
/// Represents one tag in the panel, with an indicator of whether it's present on all selected images.
/// </summary>
public partial class TagDisplayItem : ObservableObject
{
    [ObservableProperty]
    public partial string Tag { get; set; }

    [ObservableProperty]
    public partial int PresentCount { get; set; }

    [ObservableProperty]
    public partial int TotalCount { get; set; }

    public bool IsOnAll => PresentCount >= TotalCount;
    public string CountDisplay => IsOnAll ? "" : $"({PresentCount} of {TotalCount})";

    public TagDisplayItem(string tag, int presentCount, int totalCount)
    {
        Tag = tag;
        PresentCount = presentCount;
        TotalCount = totalCount;
    }
}

public partial class MetadataPanelViewModel : ObservableObject
{
    // Currently-selected entries (1 or more)
    private List<ImageEntry> _entries = new();
    private ImageRepository? _imageRepo;
    private TagRepository? _tagRepo;
    private MainViewModel? _main;

    [ObservableProperty]
    public partial bool HasImage { get; set; }

    [ObservableProperty]
    public partial bool IsMultiSelect { get; set; }

    [ObservableProperty]
    public partial int SelectionCount { get; set; }

    [ObservableProperty]
    public partial string SelectionSummary { get; set; } = "";

    // --- Editable fields ---

    /// <summary>Rating common to all selected entries; 0 if mixed or unrated.</summary>
    [ObservableProperty]
    public partial int Rating { get; set; }

    [ObservableProperty]
    public partial bool IsRatingMixed { get; set; }

    /// <summary>Caption common to all selected entries; "" if mixed or none.</summary>
    [ObservableProperty]
    public partial string Caption { get; set; } = "";

    [ObservableProperty]
    public partial bool IsCaptionMixed { get; set; }

    public ObservableCollection<TagDisplayItem> Tags { get; } = [];

    /// <summary>Date taken common to all selected entries; null if mixed or unset.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? CommonDateTaken { get; set; }

    [ObservableProperty]
    public partial bool IsDateMixed { get; set; }

    // --- Read-only / display fields (common-value or "Multiple values") ---

    [ObservableProperty]
    public partial string FileName { get; set; } = "";

    [ObservableProperty]
    public partial string FilePath { get; set; } = "";

    [ObservableProperty]
    public partial string FileSize { get; set; } = "";

    [ObservableProperty]
    public partial string Dimensions { get; set; } = "";

    [ObservableProperty]
    public partial string DateTaken { get; set; } = "";

    [ObservableProperty]
    public partial string Camera { get; set; } = "";

    [ObservableProperty]
    public partial string Lens { get; set; } = "";

    [ObservableProperty]
    public partial string Exposure { get; set; } = "";

    [ObservableProperty]
    public partial string FocalLength { get; set; } = "";

    [ObservableProperty]
    public partial string Aperture { get; set; } = "";

    [ObservableProperty]
    public partial string Iso { get; set; } = "";

    [ObservableProperty]
    public partial string GpsLatitude { get; set; } = "";

    [ObservableProperty]
    public partial string GpsLongitude { get; set; } = "";

    [ObservableProperty]
    public partial string MediaType { get; set; } = "";

    [ObservableProperty]
    public partial string Author { get; set; } = "";

    private const string MixedMarker = "Multiple values";

    public MetadataPanelViewModel() { }

    public void Initialize(ImageRepository imageRepo, TagRepository tagRepo, MainViewModel main)
    {
        _imageRepo = imageRepo;
        _tagRepo = tagRepo;
        _main = main;
    }

    /// <summary>
    /// Display metadata for a single image (legacy API kept for callers that already pass one).
    /// </summary>
    public void ShowMetadata(ImageEntry entry) => ShowMetadata(new[] { entry });

    /// <summary>
    /// Display metadata for one or more images. With multiple, fields show common value or
    /// "Multiple values" marker; tags become a union with per-tag presence counts.
    /// </summary>
    public async void ShowMetadata(IReadOnlyList<ImageEntry> entries)
    {
        if (entries == null || entries.Count == 0)
        {
            Clear();
            return;
        }

        _entries = entries.ToList();
        HasImage = true;
        SelectionCount = _entries.Count;
        IsMultiSelect = _entries.Count > 1;

        if (_entries.Count == 1)
        {
            var single = _entries[0];
            SelectionSummary = single.FileName;
            FileName = single.FileName;
            FilePath = single.FilePath;
            FileSize = FormatFileSize(single.FileSize);
            Dimensions = single.Width > 0 ? $"{single.Width} x {single.Height}" : "";
            DateTaken = single.DateTaken?.ToString("M/d/yyyy h:mm tt") ?? "";
            CommonDateTaken = single.DateTaken;
            IsDateMixed = false;
            Camera = FormatCamera(single.CameraMake, single.CameraModel);
            Lens = single.LensModel ?? "";
            Exposure = FormatExposureForDisplay(single.ExposureTime);
            FocalLength = single.FocalLength.HasValue ? $"{single.FocalLength:F1} mm" : "";
            Aperture = single.Aperture.HasValue ? $"f/{single.Aperture:F1}" : "";
            Iso = single.Iso?.ToString() ?? "";
            GpsLatitude = single.GpsLatitude.HasValue ? $"{single.GpsLatitude:F4}" : "";
            GpsLongitude = single.GpsLongitude.HasValue ? $"{single.GpsLongitude:F4}" : "";
            MediaType = single.MediaType == Core.Models.MediaType.Video ? "Video" : "Photo";
            Author = "";

            Rating = single.Rating ?? 0;
            IsRatingMixed = false;
            Caption = MetadataWriterService.ReadCaptionFromSidecar(single.FilePath) ?? "";
            IsCaptionMixed = false;
        }
        else
        {
            // Multi-select: compute common-or-mixed for each field
            SelectionSummary = $"{_entries.Count} items selected";
            FileName = $"{_entries.Count} items";
            FilePath = "";

            long totalSize = _entries.Sum(e => e.FileSize);
            FileSize = FormatFileSize(totalSize) + " total";

            Dimensions = CommonStringOrMixed(_entries, e => e.Width > 0 ? $"{e.Width} x {e.Height}" : "");

            // Date taken — common or mixed
            var dates = _entries.Select(e => e.DateTaken).Distinct().ToList();
            if (dates.Count == 1)
            {
                CommonDateTaken = dates[0];
                IsDateMixed = false;
                DateTaken = dates[0]?.ToString("M/d/yyyy h:mm tt") ?? "";
            }
            else
            {
                CommonDateTaken = null;
                IsDateMixed = true;
                DateTaken = MixedMarker;
            }

            Camera = CommonStringOrMixed(_entries, e => FormatCamera(e.CameraMake, e.CameraModel));
            Lens = CommonStringOrMixed(_entries, e => e.LensModel ?? "");
            Exposure = CommonStringOrMixed(_entries, e => FormatExposureForDisplay(e.ExposureTime));
            FocalLength = CommonStringOrMixed(_entries, e => e.FocalLength.HasValue ? $"{e.FocalLength:F1} mm" : "");
            Aperture = CommonStringOrMixed(_entries, e => e.Aperture.HasValue ? $"f/{e.Aperture:F1}" : "");
            Iso = CommonStringOrMixed(_entries, e => e.Iso?.ToString() ?? "");
            GpsLatitude = CommonStringOrMixed(_entries, e => e.GpsLatitude.HasValue ? $"{e.GpsLatitude:F4}" : "");
            GpsLongitude = CommonStringOrMixed(_entries, e => e.GpsLongitude.HasValue ? $"{e.GpsLongitude:F4}" : "");

            var types = _entries.Select(e => e.MediaType).Distinct().ToList();
            MediaType = types.Count == 1
                ? (types[0] == Core.Models.MediaType.Video ? "Video" : "Photo")
                : "Mixed";
            Author = "";

            // Rating — common or 0+mixed flag
            var ratings = _entries.Select(e => e.Rating ?? 0).Distinct().ToList();
            if (ratings.Count == 1)
            {
                Rating = ratings[0];
                IsRatingMixed = false;
            }
            else
            {
                Rating = 0;
                IsRatingMixed = true;
            }

            // Caption — common or empty+mixed flag
            var captions = _entries
                .Select(e => MetadataWriterService.ReadCaptionFromSidecar(e.FilePath) ?? "")
                .Distinct()
                .ToList();
            if (captions.Count == 1)
            {
                Caption = captions[0];
                IsCaptionMixed = false;
            }
            else
            {
                Caption = "";
                IsCaptionMixed = true;
            }
        }

        // Tags — load from DB and compute union
        Tags.Clear();
        if (_tagRepo != null)
        {
            var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int considered = 0;
            foreach (var e in _entries)
            {
                if (e.Id <= 0) continue;
                considered++;
                var list = await _tagRepo.GetTagsAsync(e.Id);
                foreach (var t in list)
                {
                    if (string.IsNullOrWhiteSpace(t.Tag)) continue;
                    tagCounts[t.Tag] = tagCounts.GetValueOrDefault(t.Tag, 0) + 1;
                }
            }
            if (considered == 0) considered = _entries.Count;
            foreach (var kvp in tagCounts.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                Tags.Add(new TagDisplayItem(kvp.Key, kvp.Value, considered));
            }
        }
    }

    private static string CommonStringOrMixed(List<ImageEntry> entries, Func<ImageEntry, string> selector)
    {
        var distinct = entries.Select(selector).Distinct().ToList();
        if (distinct.Count == 1) return distinct[0];
        // Treat "all empty" as not mixed
        if (distinct.All(string.IsNullOrEmpty)) return "";
        return MixedMarker;
    }

    public void Clear()
    {
        _entries = new();
        HasImage = false;
        IsMultiSelect = false;
        SelectionCount = 0;
        SelectionSummary = "";
        FileName = "";
        Tags.Clear();
        CommonDateTaken = null;
        IsDateMixed = false;
        IsRatingMixed = false;
        IsCaptionMixed = false;
    }

    /// <summary>
    /// Sets the rating (1-5) on every selected image. If a single image is selected and the clicked
    /// star matches its current rating, clears the rating (toggle behavior). For multi-select, click
    /// always sets to the chosen value (no toggle — too ambiguous when mixed).
    /// </summary>
    [RelayCommand]
    public async Task SetRatingAsync(int star)
    {
        if (_entries.Count == 0) return;

        int newRating;
        if (_entries.Count == 1 && !IsRatingMixed && Rating == star)
            newRating = 0; // single-select toggle
        else
            newRating = star;

        Rating = newRating;
        IsRatingMixed = false;

        int? persistedRating = newRating > 0 ? newRating : (int?)null;
        foreach (var entry in _entries)
        {
            entry.Rating = persistedRating;
            await MetadataWriterService.WriteRatingAsync(entry.FilePath, persistedRating);
            if (_imageRepo != null && entry.Id > 0)
                await _imageRepo.UpdateRatingAsync(entry.Id, persistedRating);
        }
    }

    /// <summary>
    /// Saves the current caption to every selected image's XMP sidecar. (LostFocus.)
    /// </summary>
    [RelayCommand]
    public async Task SaveCaptionAsync()
    {
        if (_entries.Count == 0) return;
        IsCaptionMixed = false;
        foreach (var entry in _entries)
            await MetadataWriterService.WriteCaptionAsync(entry.FilePath, Caption);
    }

    /// <summary>
    /// Adds a tag to every selected image (no-op for images that already have it).
    /// </summary>
    [RelayCommand]
    public async Task AddTagAsync(string tag)
    {
        if (_entries.Count == 0 || string.IsNullOrWhiteSpace(tag)) return;
        var trimmed = tag.Trim();

        // Update UI tag list
        var existing = Tags.FirstOrDefault(t => string.Equals(t.Tag, trimmed, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            Tags.Add(new TagDisplayItem(trimmed, _entries.Count, _entries.Count));
        }
        else
        {
            existing.PresentCount = _entries.Count;
            existing.TotalCount = _entries.Count;
        }

        // Apply to every entry
        foreach (var entry in _entries)
        {
            if (_tagRepo != null && entry.Id > 0)
            {
                await _tagRepo.AddTagAsync(new ImageTag
                {
                    ImageId = entry.Id,
                    Tag = trimmed,
                    Source = TagSource.Manual,
                    Confidence = 1.0f
                });

                // Rewrite sidecar with the full set for this image (DB is the source of truth)
                var allTags = await _tagRepo.GetTagsAsync(entry.Id);
                await TagWriterService.WriteTagsToSidecarAsync(entry.FilePath,
                    allTags.Select(t => t.Tag).Distinct());
            }
        }

        // Refresh the tag navigation tree so counts and new tags show up immediately
        if (_main != null) await _main.RefreshTagsTreeAsync();
    }

    /// <summary>
    /// Removes a tag from every selected image that has it.
    /// </summary>
    [RelayCommand]
    public async Task RemoveTagAsync(string tag)
    {
        if (_entries.Count == 0 || string.IsNullOrWhiteSpace(tag)) return;

        // Update UI
        var existing = Tags.FirstOrDefault(t => string.Equals(t.Tag, tag, StringComparison.OrdinalIgnoreCase));
        if (existing != null) Tags.Remove(existing);

        foreach (var entry in _entries)
        {
            if (_tagRepo != null && entry.Id > 0)
            {
                await _tagRepo.RemoveTagAsync(entry.Id, tag);
                var allTags = await _tagRepo.GetTagsAsync(entry.Id);
                await TagWriterService.WriteTagsToSidecarAsync(entry.FilePath,
                    allTags.Select(t => t.Tag).Distinct());
            }
        }

        // Refresh the tag navigation tree so counts update immediately
        if (_main != null) await _main.RefreshTagsTreeAsync();
    }

    /// <summary>
    /// Sets the same capture date on every selected image (XMP sidecar + DB cache).
    /// </summary>
    public async Task SetDateTakenAsync(DateTime dt)
    {
        if (_entries.Count == 0) return;
        CommonDateTaken = dt;
        IsDateMixed = false;
        DateTaken = dt.ToString("M/d/yyyy h:mm tt");

        foreach (var entry in _entries)
        {
            entry.DateTaken = dt;
            await MetadataWriterService.WriteDateTakenAsync(entry.FilePath, dt);
            if (_imageRepo != null && entry.Id > 0)
                await _imageRepo.UpdateDateTakenAsync(entry.Id, dt);
        }

        if (_main != null) await _main.RefreshAfterDateChangeAsync();
    }

    /// <summary>
    /// Shifts every selected image's capture date by the given offset (useful for time-zone fixes).
    /// Images without a current date are skipped.
    /// </summary>
    public async Task OffsetDateTakenAsync(TimeSpan offset)
    {
        if (_entries.Count == 0 || offset == TimeSpan.Zero) return;

        foreach (var entry in _entries)
        {
            if (!entry.DateTaken.HasValue) continue;
            var newDate = entry.DateTaken.Value + offset;
            entry.DateTaken = newDate;
            await MetadataWriterService.WriteDateTakenAsync(entry.FilePath, newDate);
            if (_imageRepo != null && entry.Id > 0)
                await _imageRepo.UpdateDateTakenAsync(entry.Id, newDate);
        }

        // Refresh derived display fields
        var distinct = _entries.Select(e => e.DateTaken).Distinct().ToList();
        if (distinct.Count == 1)
        {
            CommonDateTaken = distinct[0];
            IsDateMixed = false;
            DateTaken = distinct[0]?.ToString("M/d/yyyy h:mm tt") ?? "";
        }
        else
        {
            CommonDateTaken = null;
            IsDateMixed = true;
            DateTaken = MixedMarker;
        }

        if (_main != null) await _main.RefreshAfterDateChangeAsync();
    }

    private static string FormatExposureForDisplay(string? raw)
    {
        // Handles both new canonical values (e.g. "1/60") and legacy DB values that may already
        // include "sec" or be decimals like "0.4". Re-normalizes via MetadataReaderService helpers.
        var normalized = MetadataReaderService.StripSecondsUnit(raw);
        return string.IsNullOrEmpty(normalized) ? "" : $"{normalized} sec";
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
