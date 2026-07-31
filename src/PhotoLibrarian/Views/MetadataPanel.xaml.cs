using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoLibrarian.ViewModels;
using System;
using System.Globalization;

namespace PhotoLibrarian.Views;

public sealed partial class MetadataPanel : UserControl
{
    private MetadataPanelViewModel? ViewModel => App.ViewModel?.MetadataPanel;

    // Star glyph codes
    private const string StarFilled = "\uE1CF";  // FavoriteStar filled
    private const string StarEmpty = "\uE1CE";    // FavoriteStar empty

    private bool _suppressCaptionSave;
    private string _lastLoadedCaption = "";
    private bool _suppressDateBoxLostFocus;
    private string _lastLoadedDateText = "";

    // Accepted date formats (in order). M/d/yyyy h:mm tt is the canonical one shown to user.
    private static readonly string[] AcceptedDateFormats = new[]
    {
        "M/d/yyyy h:mm tt",
        "M/d/yyyy h:mm:ss tt",
        "M/d/yyyy H:mm",
        "M/d/yyyy H:mm:ss",
        "M/d/yyyy",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ss"
    };

    public MetadataPanel()
    {
        this.InitializeComponent();
        this.Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        TagsList.ItemsSource = ViewModel.Tags;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MetadataPanelViewModel.HasImage):
            case nameof(MetadataPanelViewModel.FileName):
            case nameof(MetadataPanelViewModel.SelectionCount):
                DispatcherQueue.TryEnqueue(UpdateDisplay);
                break;
            case nameof(MetadataPanelViewModel.CommonDateTaken):
            case nameof(MetadataPanelViewModel.IsDateMixed):
                DispatcherQueue.TryEnqueue(RefreshDateDisplay);
                break;
            case nameof(MetadataPanelViewModel.Rating):
            case nameof(MetadataPanelViewModel.IsRatingMixed):
                DispatcherQueue.TryEnqueue(UpdateStars);
                break;
            case nameof(MetadataPanelViewModel.IsFlagged):
            case nameof(MetadataPanelViewModel.IsFlagMixed):
                DispatcherQueue.TryEnqueue(UpdateFlag);
                break;
        }
    }

    private void UpdateDisplay()
    {
        if (ViewModel is null || !ViewModel.HasImage)
        {
            EmptyContent.Visibility = Visibility.Visible;
            DetailContent.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyContent.Visibility = Visibility.Collapsed;
        DetailContent.Visibility = Visibility.Visible;

        // Multi-select banner
        bool multi = ViewModel.IsMultiSelect;
        if (multi)
        {
            MultiSelectBanner.Visibility = Visibility.Visible;
            MultiSelectText.Text = $"{ViewModel.SelectionCount} items selected — edits apply to all";
            DateMultiHint.Visibility = ViewModel.IsDateMixed ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            MultiSelectBanner.Visibility = Visibility.Collapsed;
            DateMultiHint.Visibility = Visibility.Collapsed;
            // Always start in absolute mode for single-select
            DateAbsoluteMode.Visibility = Visibility.Visible;
            DateShiftMode.Visibility = Visibility.Collapsed;
        }
        // Shift link is useful both for time-zone fixes (multi) and camera-clock fixes (single)
        SwitchToShiftBtn.Visibility = ViewModel.CommonDateTaken.HasValue || ViewModel.IsDateMixed
            ? Visibility.Visible : Visibility.Collapsed;

        // Caption
        _suppressCaptionSave = true;
        CaptionBox.Text = ViewModel.Caption ?? "";
        _lastLoadedCaption = CaptionBox.Text;
        CaptionBox.PlaceholderText = ViewModel.IsCaptionMixed
            ? "Multiple captions — type to replace all"
            : "Add caption";
        CaptionMixedHint.Visibility = ViewModel.IsCaptionMixed ? Visibility.Visible : Visibility.Collapsed;
        _suppressCaptionSave = false;

        // Date taken — compact text editor
        RefreshDateDisplay();

        // Geotag
        if (!string.IsNullOrEmpty(ViewModel.GpsLatitude) && ViewModel.GpsLatitude != "Multiple values")
            GeotagText.Text = $"{ViewModel.GpsLatitude}, {ViewModel.GpsLongitude}";
        else if (ViewModel.GpsLatitude == "Multiple values")
            GeotagText.Text = "Multiple geotags";
        else
            GeotagText.Text = "Add geotag";

        // Information section
        InfoFileName.Text = ViewModel.FileName;
        InfoFolder.Text = !string.IsNullOrEmpty(ViewModel.FolderPath) ? ViewModel.FolderPath : "—";
        InfoFileSize.Text = ViewModel.FileSize;
        InfoDimensions.Text = !string.IsNullOrEmpty(ViewModel.Dimensions) ? ViewModel.Dimensions : "—";
        InfoCamera.Text = !string.IsNullOrEmpty(ViewModel.Camera) ? ViewModel.Camera : "—";
        InfoAuthor.Text = !string.IsNullOrEmpty(ViewModel.Author) ? ViewModel.Author : "Add an author";
        InfoExposure.Text = ViewModel.Exposure;
        InfoAperture.Text = ViewModel.Aperture;
        InfoFocalLength.Text = ViewModel.FocalLength;
        InfoIso.Text = ViewModel.Iso;
        InfoLatitude.Text = ViewModel.GpsLatitude;
        InfoLongitude.Text = ViewModel.GpsLongitude;
        InfoFilePath.Text = ViewModel.FilePath;

        InfoCameraRow.Visibility = string.IsNullOrEmpty(ViewModel.Camera) ? Visibility.Collapsed : Visibility.Visible;
        InfoExposureRow.Visibility = string.IsNullOrEmpty(ViewModel.Exposure) ? Visibility.Collapsed : Visibility.Visible;
        InfoApertureRow.Visibility = string.IsNullOrEmpty(ViewModel.Aperture) ? Visibility.Collapsed : Visibility.Visible;
        InfoFocalLengthRow.Visibility = string.IsNullOrEmpty(ViewModel.FocalLength) ? Visibility.Collapsed : Visibility.Visible;
        InfoIsoRow.Visibility = string.IsNullOrEmpty(ViewModel.Iso) ? Visibility.Collapsed : Visibility.Visible;
        InfoLatRow.Visibility = string.IsNullOrEmpty(ViewModel.GpsLatitude) ? Visibility.Collapsed : Visibility.Visible;
        InfoLonRow.Visibility = string.IsNullOrEmpty(ViewModel.GpsLongitude) ? Visibility.Collapsed : Visibility.Visible;
        InfoDimensionsRow.Visibility = string.IsNullOrEmpty(ViewModel.Dimensions) ? Visibility.Collapsed : Visibility.Visible;

        UpdateStars();
        UpdateFlag();
    }

    // --- Flag ---

    private void UpdateFlag()
    {
        if (ViewModel is null) return;

        bool mixed = ViewModel.IsFlagMixed;
        bool flagged = ViewModel.IsFlagged;

        FlagText.Text = mixed ? "Mixed" : flagged ? "Flagged" : "Not flagged";
        FlagIcon.Foreground = flagged || mixed
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(mixed ? (byte)120 : (byte)255, 232, 17, 35))
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);

        ToolTipService.SetToolTip(FlagButton,
            mixed ? "Mixed flags — click to flag all (F)" : flagged ? "Click to unflag (F)" : "Click to flag (F)");
    }

    private void OnFlagClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.ToggleFlagCommand.Execute(null);
    }

    // --- Star Rating ---

    private void UpdateStars()
    {
        if (ViewModel is null) return;
        int rating = ViewModel.Rating;
        bool mixed = ViewModel.IsRatingMixed;

        Star1Icon.Glyph = rating >= 1 ? StarFilled : StarEmpty;
        Star2Icon.Glyph = rating >= 2 ? StarFilled : StarEmpty;
        Star3Icon.Glyph = rating >= 3 ? StarFilled : StarEmpty;
        Star4Icon.Glyph = rating >= 4 ? StarFilled : StarEmpty;
        Star5Icon.Glyph = rating >= 5 ? StarFilled : StarEmpty;

        var goldBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 185, 0));
        var fadedBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 255, 185, 0));
        var grayBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray);

        Brush emptyBrush = mixed ? fadedBrush : grayBrush;
        Star1Icon.Foreground = rating >= 1 ? goldBrush : emptyBrush;
        Star2Icon.Foreground = rating >= 2 ? goldBrush : emptyBrush;
        Star3Icon.Foreground = rating >= 3 ? goldBrush : emptyBrush;
        Star4Icon.Foreground = rating >= 4 ? goldBrush : emptyBrush;
        Star5Icon.Foreground = rating >= 5 ? goldBrush : emptyBrush;

        ToolTipService.SetToolTip(StarPanel, mixed ? "Mixed ratings — click to set all" : null);
    }

    private void OnStarClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not Button btn || btn.Tag is not string tagStr) return;
        if (int.TryParse(tagStr, out int star))
            ViewModel.SetRatingCommand.Execute(star);
    }

    // --- Caption ---

    private void OnCaptionLostFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || _suppressCaptionSave) return;
        if (CaptionBox.Text == _lastLoadedCaption) return;
        ViewModel.Caption = CaptionBox.Text;
        ViewModel.SaveCaptionCommand.Execute(null);
        _lastLoadedCaption = CaptionBox.Text;
        CaptionMixedHint.Visibility = Visibility.Collapsed;
    }

    // --- Tags ---

    private void OnAddTagClick(object sender, RoutedEventArgs e) => AddCurrentTag();

    private void OnNewTagKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            AddCurrentTag();
            e.Handled = true;
        }
    }

    private void AddCurrentTag()
    {
        if (ViewModel is null) return;
        var tag = NewTagBox.Text?.Trim();
        if (string.IsNullOrEmpty(tag)) return;
        ViewModel.AddTagCommand.Execute(tag);
        NewTagBox.Text = "";
    }

    private void OnRemoveTagClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not Button btn) return;
        var tag = btn.Tag as string;
        if (!string.IsNullOrEmpty(tag))
            ViewModel.RemoveTagCommand.Execute(tag);
    }

    // --- Date taken: compact text editor ---

    /// <summary>
    /// Re-renders the date editor from the current ViewModel state. Called whenever
    /// CommonDateTaken / IsDateMixed change (e.g. after Apply or Shift) so the box
    /// reflects the new value without forcing a full UpdateDisplay rebuild.
    /// </summary>
    private void RefreshDateDisplay()
    {
        if (ViewModel is null) return;

        _suppressDateBoxLostFocus = true;
        if (ViewModel.IsDateMixed)
        {
            DateTakenBox.Text = "";
            DateTakenBox.PlaceholderText = "Multiple dates — type to set all";
            DateMixedHint.Visibility = Visibility.Visible;
            DateMultiHint.Visibility = Visibility.Collapsed;
        }
        else if (ViewModel.CommonDateTaken.HasValue)
        {
            var d = ViewModel.CommonDateTaken.Value;
            DateTakenBox.Text = d.LocalDateTime.ToString("M/d/yyyy h:mm tt", CultureInfo.CurrentCulture);
            DateTakenBox.PlaceholderText = "M/d/yyyy h:mm tt";
            DateMixedHint.Visibility = Visibility.Collapsed;
            if (ViewModel.IsMultiSelect)
                DateMultiHint.Visibility = Visibility.Visible;
        }
        else
        {
            DateTakenBox.Text = "";
            DateTakenBox.PlaceholderText = "M/d/yyyy h:mm tt";
            DateMixedHint.Visibility = Visibility.Collapsed;
        }
        _lastLoadedDateText = DateTakenBox.Text;
        DateParseError.Visibility = Visibility.Collapsed;

        // Pre-fill flyout pickers from current date if any
        var seed = ViewModel.CommonDateTaken ?? DateTimeOffset.Now;
        DatePopupCal.Date = new DateTimeOffset(seed.Year, seed.Month, seed.Day, 0, 0, 0, seed.Offset);
        DatePopupTime.Time = new TimeSpan(seed.Hour, seed.Minute, seed.Second);
        _suppressDateBoxLostFocus = false;

        // If shift mode is currently open, refresh its preview against the new common date
        if (DateShiftMode.Visibility == Visibility.Visible)
            UpdateShiftPreview();
    }

    private async void OnDateTakenBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || _suppressDateBoxLostFocus) return;
        if (DateTakenBox.Text == _lastLoadedDateText) return;
        await CommitDateTextAsync();
    }

    private async void OnDateTakenBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            await CommitDateTextAsync();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            // Revert
            DateTakenBox.Text = _lastLoadedDateText;
            DateParseError.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private async System.Threading.Tasks.Task CommitDateTextAsync()
    {
        if (ViewModel is null) return;

        var txt = DateTakenBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(txt))
        {
            // Empty — silently ignore; user can clear via flyout if desired
            DateParseError.Visibility = Visibility.Collapsed;
            return;
        }

        if (!TryParseDate(txt, out var parsed))
        {
            DateParseError.Visibility = Visibility.Visible;
            return;
        }

        DateParseError.Visibility = Visibility.Collapsed;
        await ViewModel.SetDateTakenAsync(parsed);
        _lastLoadedDateText = parsed.ToString("M/d/yyyy h:mm tt", CultureInfo.CurrentCulture);
        // Re-format the box to canonical form
        _suppressDateBoxLostFocus = true;
        DateTakenBox.Text = _lastLoadedDateText;
        _suppressDateBoxLostFocus = false;
    }

    private static bool TryParseDate(string text, out DateTime result)
    {
        if (DateTime.TryParseExact(text, AcceptedDateFormats, CultureInfo.CurrentCulture,
                DateTimeStyles.AssumeLocal, out result))
            return true;
        // Last-ditch general parse
        return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out result);
    }

    private async void OnApplyDatePopupClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (DatePopupCal.Date is not DateTimeOffset date) return;
        var time = DatePopupTime.Time;
        var combined = new DateTime(date.Year, date.Month, date.Day,
                                    time.Hours, time.Minutes, time.Seconds,
                                    DateTimeKind.Local);
        await ViewModel.SetDateTakenAsync(combined);
        // Close the flyout
        DatePickerBtn.Flyout?.Hide();
    }

    private async void OnApplyShiftClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var offset = BuildShiftOffset();
        if (offset == TimeSpan.Zero) return;

        ApplyShiftButton.IsEnabled = false;
        try
        {
            await ViewModel.OffsetDateTakenAsync(offset);
        }
        finally
        {
            ApplyShiftButton.IsEnabled = true;
        }

        ResetShiftFields();
        // Switch back to absolute view; RefreshDateDisplay is triggered by VM property change.
        DateShiftMode.Visibility = Visibility.Collapsed;
        DateAbsoluteMode.Visibility = Visibility.Visible;
    }

    private void OnSwitchToShift(object sender, RoutedEventArgs e)
    {
        ResetShiftFields();
        UpdateShiftHeader();
        UpdateShiftPreview();
        DateAbsoluteMode.Visibility = Visibility.Collapsed;
        DateShiftMode.Visibility = Visibility.Visible;
    }

    private void OnSwitchToAbsolute(object sender, RoutedEventArgs e)
    {
        DateShiftMode.Visibility = Visibility.Collapsed;
        DateAbsoluteMode.Visibility = Visibility.Visible;
    }

    private void OnShiftValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        // NumberBox raises ValueChanged during XAML init (Value="0") before all siblings exist.
        if (ShiftDaysBox is null || ShiftHoursBox is null
            || ShiftMinutesBox is null || ShiftSecondsBox is null) return;
        UpdateShiftPreview();
    }

    private TimeSpan BuildShiftOffset()
    {
        if (ShiftDaysBox is null || ShiftHoursBox is null
            || ShiftMinutesBox is null || ShiftSecondsBox is null) return TimeSpan.Zero;
        int days = double.IsNaN(ShiftDaysBox.Value) ? 0 : (int)ShiftDaysBox.Value;
        int hours = double.IsNaN(ShiftHoursBox.Value) ? 0 : (int)ShiftHoursBox.Value;
        int minutes = double.IsNaN(ShiftMinutesBox.Value) ? 0 : (int)ShiftMinutesBox.Value;
        int seconds = double.IsNaN(ShiftSecondsBox.Value) ? 0 : (int)ShiftSecondsBox.Value;
        return new TimeSpan(days, hours, minutes, seconds);
    }

    private void ResetShiftFields()
    {
        ShiftDaysBox.Value = 0;
        ShiftHoursBox.Value = 0;
        ShiftMinutesBox.Value = 0;
        ShiftSecondsBox.Value = 0;
    }

    private void UpdateShiftHeader()
    {
        if (ViewModel is null) return;
        int n = ViewModel.SelectionCount;
        ShiftHeaderText.Text = n > 1
            ? $"Shift the capture date of {n} images by:"
            : "Shift the capture date by:";
    }

    private void UpdateShiftPreview()
    {
        if (ViewModel is null || ShiftPreviewText is null || ApplyShiftButton is null) return;

        var offset = BuildShiftOffset();
        if (offset == TimeSpan.Zero)
        {
            ShiftPreviewText.Text = "Enter a non-zero shift (negative values shift earlier).";
            ApplyShiftButton.IsEnabled = false;
            return;
        }

        ApplyShiftButton.IsEnabled = true;
        string sign = offset < TimeSpan.Zero ? "−" : "+";
        string magnitude = FormatOffset(offset.Duration());

        if (ViewModel.CommonDateTaken.HasValue && !ViewModel.IsDateMixed)
        {
            var newDate = ViewModel.CommonDateTaken.Value + offset;
            ShiftPreviewText.Text =
                $"{sign}{magnitude}  →  {newDate.LocalDateTime:M/d/yyyy h:mm:ss tt}";
        }
        else
        {
            ShiftPreviewText.Text = $"{sign}{magnitude} (each date shifts independently)";
        }
    }

    private static string FormatOffset(TimeSpan d)
    {
        var parts = new List<string>();
        if (d.Days > 0) parts.Add($"{d.Days}d");
        if (d.Hours > 0) parts.Add($"{d.Hours}h");
        if (d.Minutes > 0) parts.Add($"{d.Minutes}m");
        if (d.Seconds > 0) parts.Add($"{d.Seconds}s");
        return parts.Count > 0 ? string.Join(" ", parts) : "0";
    }

    // Keep DetailGrid MinHeight in sync with ScrollViewer viewport
    // so the * spacer row pushes the info block to the bottom.
    private void OnDetailScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        DetailGrid.MinHeight = e.NewSize.Height;
    }
}
