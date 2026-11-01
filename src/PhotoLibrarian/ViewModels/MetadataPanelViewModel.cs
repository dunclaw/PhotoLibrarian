// Copyright (c) Duncan Lawler. All Rights Reserved.
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.ApplicationModel.Resources;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PhotoLibrarian.ViewModels;

public partial class PersonTagDisplayItem : ObservableObject
{
    private readonly string _imagePath;
    private readonly FaceRegion _face;
    private Task? _thumbnailLoadTask;

    public string Name => _face.PersonName ?? "Unnamed person";
    public long FaceId => _face.Id;
    public FaceRegion Face => _face;
    public ImageEntry Image { get; }

    [ObservableProperty]
    public partial ImageSource? Thumbnail { get; set; }

    public PersonTagDisplayItem(ImageEntry image, FaceRegion face)
    {
        Image = image;
        _imagePath = image.FilePath;
        _face = face;
    }

    public Task LoadThumbnailAsync()
    {
        return _thumbnailLoadTask ??= LoadThumbnailCoreAsync();
    }

    private async Task LoadThumbnailCoreAsync()
    {
        var bytes = await FaceThumbnailCropper.CreateAsync(_imagePath, _face, 48);
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(bytes);
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        Thumbnail = bitmap;
    }
}

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
    private FaceRepository? _faceRepo;
    private MainViewModel? _main;
    private CancellationTokenSource? _videoMetadataCancellation;
    private static readonly ResourceLoader ResourceLoader = new(
        Path.Combine(AppContext.BaseDirectory, "PhotoLibrarian.pri"),
        "Resources");

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

    /// <summary>True when every selected entry is flagged.</summary>
    [ObservableProperty]
    public partial bool IsFlagged { get; set; }

    /// <summary>True when the selection contains both flagged and unflagged entries.</summary>
    [ObservableProperty]
    public partial bool IsFlagMixed { get; set; }

    /// <summary>Caption common to all selected entries; "" if mixed or none.</summary>
    [ObservableProperty]
    public partial string Caption { get; set; } = "";

    [ObservableProperty]
    public partial bool IsCaptionMixed { get; set; }

    public ObservableCollection<TagDisplayItem> Tags { get; } = [];
    public ObservableCollection<PersonTagDisplayItem> PeopleTags { get; } = [];

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

    /// <summary>Directory the image lives in — shown under Filename, trimmed with a tooltip.</summary>
    [ObservableProperty]
    public partial string FolderPath { get; set; } = "";

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

    [ObservableProperty]
    public partial string VideoTitle { get; set; } = "";

    [ObservableProperty]
    public partial string VideoDuration { get; set; } = "";

    [ObservableProperty]
    public partial string VideoBitrate { get; set; } = "";

    [ObservableProperty]
    public partial string VideoFrameRate { get; set; } = "";

    [ObservableProperty]
    public partial string VideoCodec { get; set; } = "";

    [ObservableProperty]
    public partial string VideoAlbum { get; set; } = "";

    [ObservableProperty]
    public partial string VideoYear { get; set; } = "";

    [ObservableProperty]
    public partial string VideoDateEncoded { get; set; } = "";

    [ObservableProperty]
    public partial string VideoMetadataError { get; set; } = "";

    private const string MixedMarker = "Multiple values";

    public MetadataPanelViewModel() { }

    public void Initialize(
        ImageRepository imageRepo,
        TagRepository tagRepo,
        FaceRepository faceRepo,
        MainViewModel main)
    {
        _imageRepo = imageRepo;
        _tagRepo = tagRepo;
        _faceRepo = faceRepo;
        _main = main;
        _main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsManualFaceTaggingActive))
            {
                IsManualFaceTaggingActive = _main.IsManualFaceTaggingActive;
            }
        };
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

        CancelVideoMetadataRead();
        ResetVideoMetadata();
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
            FolderPath = System.IO.Path.GetDirectoryName(single.FilePath) ?? "";
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
            IsFlagged = single.IsFlagged;
            IsFlagMixed = false;
            Caption = MetadataWriterService.ReadCaptionFromSidecar(single.FilePath) ?? "";
            IsCaptionMixed = false;
        }
        else
        {
            // Multi-select: compute common-or-mixed for each field
            SelectionSummary = $"{_entries.Count} items selected";
            FileName = $"{_entries.Count} items";
            FilePath = "";
            FolderPath = CommonStringOrMixed(_entries, e => System.IO.Path.GetDirectoryName(e.FilePath) ?? "");

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

            // Flag — flagged only when all are flagged; mixed when they differ
            var flags = _entries.Select(e => e.IsFlagged).Distinct().ToList();
            IsFlagged = flags.Count == 1 && flags[0];
            IsFlagMixed = flags.Count > 1;

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

        Task videoMetadataTask = Task.CompletedTask;
        if (_entries.Count == 1 && _entries[0].MediaType == Core.Models.MediaType.Video)
        {
            var cancellation = new CancellationTokenSource();
            _videoMetadataCancellation = cancellation;
            videoMetadataTask = LoadVideoMetadataAsync(_entries[0], cancellation);
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

        await ReloadPeopleTagsAsync();
        await videoMetadataTask;
    }

    private async Task LoadVideoMetadataAsync(ImageEntry entry, CancellationTokenSource cancellation)
    {
        try
        {
            var metadata = await VideoMetadataReaderService.ReadAsync(entry.FilePath, cancellation.Token);
            if (!IsCurrentVideoMetadataRequest(cancellation)) return;

            if (metadata.Width.HasValue && metadata.Height.HasValue)
                Dimensions = $"{metadata.Width} x {metadata.Height}";
            VideoTitle = string.IsNullOrWhiteSpace(entry.Title) ? metadata.Title ?? "" : entry.Title;
            Author = metadata.Artist ?? "";
            VideoAlbum = metadata.Album ?? "";
            VideoYear = metadata.Year?.ToString(CultureInfo.CurrentCulture) ?? "";
            VideoDateEncoded = metadata.DateEncoded.HasValue
                ? metadata.DateEncoded.Value.LocalDateTime.ToString("M/d/yyyy h:mm tt", CultureInfo.CurrentCulture)
                : "";
            VideoDuration = metadata.Duration.HasValue ? FormatVideoDuration(metadata.Duration.Value) : "";
            VideoBitrate = metadata.Bitrate.HasValue ? FormatVideoBitrate(metadata.Bitrate.Value) : "";
            VideoFrameRate = metadata.FrameRate.HasValue
                ? FormatVideoValue("MetadataVideoFrameRateFormat",
                    metadata.FrameRate.Value.ToString("0.##", CultureInfo.CurrentCulture))
                : "";
            VideoCodec = metadata.Codec ?? "";
            if (!entry.DateTaken.HasValue && metadata.DateTaken.HasValue)
            {
                CommonDateTaken = metadata.DateTaken.Value.DateTime;
                DateTaken = metadata.DateTaken.Value.DateTime.ToString("M/d/yyyy h:mm tt", CultureInfo.CurrentCulture);
            }
            if (string.IsNullOrWhiteSpace(Camera))
                Camera = FormatCamera(metadata.CameraMake, metadata.CameraModel);
            if (!entry.GpsLatitude.HasValue && metadata.Latitude.HasValue)
                GpsLatitude = metadata.Latitude.Value.ToString("F4", CultureInfo.CurrentCulture);
            if (!entry.GpsLongitude.HasValue && metadata.Longitude.HasValue)
                GpsLongitude = metadata.Longitude.Value.ToString("F4", CultureInfo.CurrentCulture);
            if (metadata.ExtractionError is not null)
                VideoMetadataError = FormatVideoMetadataError(entry.FileName, metadata.ExtractionError);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                           UnauthorizedAccessException or NotSupportedException or
                                           ArgumentException or COMException or InvalidOperationException)
        {
            if (!IsCurrentVideoMetadataRequest(cancellation)) return;

            Diagnostics.DebugLog.WriteLine(
                $"Could not read video details for {entry.FilePath}: {exception}");
            VideoMetadataError = FormatVideoMetadataError(entry.FileName, exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_videoMetadataCancellation, cancellation))
                _videoMetadataCancellation = null;
            cancellation.Dispose();
        }
    }

    private bool IsCurrentVideoMetadataRequest(CancellationTokenSource cancellation) =>
        ReferenceEquals(_videoMetadataCancellation, cancellation) && !cancellation.IsCancellationRequested;

    private void CancelVideoMetadataRead()
    {
        var cancellation = _videoMetadataCancellation;
        _videoMetadataCancellation = null;
        if (cancellation is null) return;
        cancellation.Cancel();
    }

    private void ResetVideoMetadata()
    {
        VideoTitle = "";
        VideoDuration = "";
        VideoBitrate = "";
        VideoFrameRate = "";
        VideoCodec = "";
        VideoAlbum = "";
        VideoYear = "";
        VideoDateEncoded = "";
        VideoMetadataError = "";
    }

    private static string FormatVideoDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1)
            return ResourceLoader.GetString("MetadataVideoDurationUnderSecond");
        if (duration.TotalHours >= 1)
            return $"{(long)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
        return $"{(long)duration.TotalMinutes}:{duration.Seconds:00}";
    }

    private static string FormatVideoBitrate(uint bitrate) =>
        bitrate >= 1_000_000
            ? FormatVideoValue("MetadataVideoMbpsFormat",
                (bitrate / 1_000_000d).ToString("0.#", CultureInfo.CurrentCulture))
            : FormatVideoValue("MetadataVideoKbpsFormat",
                (bitrate / 1_000d).ToString("N0", CultureInfo.CurrentCulture));

    private static string FormatVideoValue(string key, string value) =>
        string.Format(CultureInfo.CurrentCulture, ResourceLoader.GetString(key), value);

    private static string FormatVideoMetadataError(string fileName, string message) =>
        string.Format(
            CultureInfo.CurrentCulture,
            ResourceLoader.GetString("MetadataVideoErrorMessage"),
            fileName,
            message);

    [ObservableProperty]
    public partial bool IsManualFaceTaggingActive { get; set; }

    public async Task ReloadPeopleTagsAsync()
    {
        PeopleTags.Clear();
        if (_faceRepo == null) return;

        foreach (var entry in _entries.Where(entry => entry.Id > 0))
        {
            var faces = await _faceRepo.GetFacesForImageAsync(entry.Id);
            foreach (var face in faces.Where(face => !string.IsNullOrWhiteSpace(face.PersonName)))
            {
                var item = new PersonTagDisplayItem(entry, face);
                PeopleTags.Add(item);
                _ = LoadThumbnailSafelyAsync(item);
            }
        }

        OnPropertyChanged(nameof(PeopleTags));
    }

    public void StartManualFaceTagging()
    {
        if (_main is null) return;
        if (_main.IsManualFaceTaggingActive)
        {
            _main.CancelManualFaceTagging();
            return;
        }
        if (_entries.Count != 1) return;
        _main.StartManualFaceTagging(_entries[0]);
    }

    public async Task RemoveFaceTagAsync(PersonTagDisplayItem item)
    {
        if (_main is null) return;
        await _main.RemoveFaceTagAsync(item.FaceId);
    }

    /// <summary>
    /// Called when the pointer enters/leaves a people-tags row so the image (full viewer
    /// or grid thumbnail) can highlight where that face is. Pass null to clear the highlight.
    /// </summary>
    public void SetHoveredFace(PersonTagDisplayItem? item)
    {
        _main?.SetHoveredFace(item?.Image, item?.Face);
    }

    private static async Task LoadThumbnailSafelyAsync(PersonTagDisplayItem item)
    {
        try
        {
            await item.LoadThumbnailAsync();
        }
        catch (IOException)
        {
        }
        catch (InvalidDataException)
        {
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
        CancelVideoMetadataRead();
        ResetVideoMetadata();
        _entries = new();
        HasImage = false;
        IsMultiSelect = false;
        SelectionCount = 0;
        SelectionSummary = "";
        FileName = "";
        FolderPath = "";
        Tags.Clear();
        PeopleTags.Clear();
        CommonDateTaken = null;
        IsDateMixed = false;
        IsRatingMixed = false;
        IsCaptionMixed = false;
        IsFlagged = false;
        IsFlagMixed = false;
    }

    /// <summary>
    /// Recomputes the flag state from the currently-loaded entries. Called after a flag change
    /// made elsewhere (grid shortcut, context menu, viewer) so the panel stays in sync.
    /// </summary>
    public void RefreshFlagState()
    {
        if (_entries.Count == 0)
        {
            IsFlagged = false;
            IsFlagMixed = false;
            return;
        }

        var flags = _entries.Select(e => e.IsFlagged).Distinct().ToList();
        IsFlagged = flags.Count == 1 && flags[0];
        IsFlagMixed = flags.Count > 1;
    }

    /// <summary>
    /// Toggles the flag on every selected image. A mixed selection is flagged first.
    /// </summary>
    [RelayCommand]
    public async Task ToggleFlagAsync()
    {
        if (_entries.Count == 0 || _main is null) return;

        bool newValue = IsFlagMixed || !IsFlagged;
        await _main.SetFlagAsync(_entries.ToList(), newValue);
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
        {
            await MetadataWriterService.WriteCaptionAsync(entry.FilePath, Caption);
            if (_imageRepo is not null)
                await _imageRepo.UpdateCaptionAsync(entry.Id, Caption);
            entry.Caption = Caption;
        }
    }

    /// <summary>
    /// Refreshes just the Tags collection from the repo for the currently-loaded entries.
    /// Used by the drag-to-tag drop target so the right-panel tag list updates without
    /// re-running the full ShowMetadata pass.
    /// </summary>
    public async Task ReloadTagsAsync()
    {
        if (_tagRepo == null || _entries.Count == 0) return;
        Tags.Clear();
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
