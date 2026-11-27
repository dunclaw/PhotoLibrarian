// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoLibrarian.Core.Models;
using System;
using System.Globalization;
using System.IO;
using CommunityToolkit.WinUI.Controls;

namespace PhotoLibrarian.Views;

public sealed partial class TopRibbon : UserControl
{
    private bool _photoFuseActive;
    private bool _canUndo;
    private bool _canRedo;
    private bool _hasPhoto;
    private bool _editorActive;
    private bool _isVideo;
    private bool _videoEditActive;
    private bool _syncingVideoEditOptions;
    private bool _videoEditBusy;
    private bool _videoEditPreviewReady;
    private string _videoEditStatus = "";
    private int _videoRotationQuarterTurns;
    private string? _videoSoundtrackPath;
    private readonly string _addSoundtrackLabel;
    private readonly object _addSoundtrackToolTip;
    private VideoTrimInfo? _videoTrimInfo;
    private bool _quickEditsAvailable;
    private Button? _historyFocus;
    public event EventHandler? CropClicked;
    public event EventHandler? MakeCopyClicked;
    public event EventHandler? StraightenClicked;
    public event EventHandler? AdjustClicked;
    public event EventHandler? EffectsClicked;
    internal event EventHandler<ViewerQuickEdit>? QuickEditClicked;
    public event EventHandler? UndoClicked;
    public event EventHandler? RedoClicked;
    public FrameworkElement EffectsAnchor => EffectsButton;

    public event EventHandler? RetouchClicked;
    public event EventHandler? RedEyeClicked;
    public event EventHandler? SlideshowClicked;
    public event EventHandler<SlideshowTheme>? SlideshowThemeChanged;
    public event EventHandler? PhotoFuseClicked;
    public event EventHandler? VideoEditOptionsChanged;
    public event EventHandler? VideoSoundtrackPickerClicked;
    public event EventHandler? VideoEditPrepareClicked;
    public event EventHandler? VideoEditExportClicked;
    public event EventHandler? VideoEditCancelClicked;
    public event EventHandler<string>? VideoEditStatusChanged;
    public event EventHandler? ApplyCropClicked;
    public event EventHandler? CancelCropClicked;
    public event EventHandler? ApplyStraightenClicked;
    public event EventHandler? CancelStraightenClicked;
    public event EventHandler<CropAspectRatio>? CropAspectChanged;

    public TopRibbon()
    {
        this.InitializeComponent();
        _addSoundtrackLabel = VideoSoundtrackButton.Label;
        _addSoundtrackToolTip = ToolTipService.GetToolTip(VideoSoundtrackButton);
        SetPhotoContext(hasPhoto: false, editorActive: false);
    }

    public void SetHistoryAvailability(bool canUndo, bool canRedo, bool busy = false)
    {
        if (busy && IsEnabled && XamlRoot is not null)
        {
            var focused = FocusManager.GetFocusedElement(XamlRoot);
            _historyFocus = ReferenceEquals(focused, UndoButton) ? UndoButton
                : ReferenceEquals(focused, RedoButton) ? RedoButton : null;
        }
        IsEnabled = !busy;
        _canUndo = canUndo;
        _canRedo = canRedo;
        UndoButton.IsEnabled = canUndo && !_photoFuseActive;
        RedoButton.IsEnabled = canRedo && !_photoFuseActive;
        if (!busy && _historyFocus is { } previous)
        {
            _historyFocus = null;
            var target = previous.IsEnabled ? previous : UndoButton.IsEnabled ? UndoButton
                : RedoButton.IsEnabled ? RedoButton : null;
            target?.Focus(FocusState.Keyboard);
        }
    }

    public void SetHistoryLabels(string undo, string redo)
    {
        AutomationProperties.SetName(UndoButton, undo);
        AutomationProperties.SetName(RedoButton, redo);
        ToolTipService.SetToolTip(UndoButton, $"{undo} (Ctrl+Z)\n{AutomationProperties.GetHelpText(UndoButton)}");
        ToolTipService.SetToolTip(RedoButton, $"{redo} (Ctrl+Y or Ctrl+Shift+Z)\n{AutomationProperties.GetHelpText(RedoButton)}");
    }

    public void SetPhotoContext(bool hasPhoto, bool editorActive)
    {
        _hasPhoto = hasPhoto;
        _editorActive = editorActive;
        Visibility = hasPhoto && !editorActive ? Visibility.Visible : Visibility.Collapsed;
        SetVideoMode(_isVideo);
    }

    /// <summary>Set the centered context label (e.g. file name being viewed).</summary>
    public void SetContextLabel(string text) => ContextLabel.Text = text ?? "";

    /// <summary>Set the smaller secondary line under the context label. Pass null to hide it.</summary>
    public void SetContextSubLabel(string? text)
    {
        ContextSubLabel.Text = text ?? "";
        ContextSubLabel.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetVideoMode(bool isVideo)
    {
        _isVideo = isVideo;
        if (isVideo && _hasPhoto && !_editorActive && !_videoEditActive)
        {
            EnterVideoEditMode();
            return;
        }
        if (!isVideo)
            _videoEditActive = false;
        ViewerTools.Visibility = _hasPhoto && !_editorActive && !isVideo ? Visibility.Visible : Visibility.Collapsed;
        HistoryTools.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;
        ContextTools.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;
        VideoEditTools.Visibility = _hasPhoto && !_editorActive && isVideo
            ? Visibility.Visible : Visibility.Collapsed;
        CropTools.Visibility = Visibility.Collapsed;
        StraightenTools.Visibility = Visibility.Collapsed;
        SetContextSubLabel(null);
    }

    public void EnterVideoEditMode()
    {
        _videoEditActive = true;
        _syncingVideoEditOptions = true;
        _videoRotationQuarterTurns = 0;
        BrightnessSlider.Value = 0;
        ContrastSlider.Value = 100;
        VideoEditSpeedBox.SelectedIndex = 2;
        RemoveAudioBox.IsChecked = false;
        _videoSoundtrackPath = null;
        StabilizeVideoButton.IsChecked = false;
        TrimVideoButton.IsChecked = false;
        VideoTrimControls.Visibility = Visibility.Collapsed;
        if (_videoTrimInfo is { } info)
        {
            ResetVideoTrimRange(info);
        }
        _syncingVideoEditOptions = false;
        UpdateVideoEditLabels();
        SetVideoMode(_isVideo);
        SetVideoEditState(false, preparing: false, hasPreview: false, progress: 0,
            status: VideoEditStrings.Get("VideoEditPreparePrompt"));
    }

    public void ExitVideoEditMode()
    {
        _videoEditActive = false;
        _videoEditBusy = false;
        _videoEditPreviewReady = false;
        SetVideoMode(_isVideo);
    }

    public void SetVideoTrimInfo(VideoTrimInfo? info)
    {
        _syncingVideoEditOptions = true;
        _videoTrimInfo = info;
        TrimVideoButton.IsEnabled = info is not null;
        if (info is { } video)
        {
            ResetVideoTrimRange(video);
        }
        _syncingVideoEditOptions = false;
    }

    public bool TryGetVideoEditOptions(out VideoEditOptions options)
    {
        if (VideoEditSpeedBox.SelectedItem is not ComboBoxItem { Tag: string speedTag } ||
            !int.TryParse(speedTag, NumberStyles.None, CultureInfo.InvariantCulture, out int playbackSpeedPercent) ||
            playbackSpeedPercent is not (50 or 75 or 100))
        {
            options = new();
            return false;
        }
        StabilizationOptions? stabilization = StabilizeVideoButton.IsChecked == true
            ? new(VideoAutoCropBox.IsChecked == true,
                (StabilizationSmoothness)Math.Clamp(VideoSmoothnessBox.SelectedIndex, 0, 2),
                VideoCorrectRotationBox.IsChecked == true, VideoLockFrameBox.IsChecked == true)
            : null;
        options = new(_videoRotationQuarterTurns, RemoveAudioBox.IsChecked == true,
            (int)BrightnessSlider.Value, (int)ContrastSlider.Value, stabilization,
            PlaybackSpeedPercent: playbackSpeedPercent, SoundtrackPath: _videoSoundtrackPath);
        if (TrimVideoButton.IsChecked != true) return true;
        if (_videoTrimInfo is not { } info) return false;
        double start = VideoTrimRange.RangeStart / info.FramesPerSecond;
        double end = VideoTrimRange.RangeEnd / info.FramesPerSecond;
        if (!double.IsFinite(start) || !double.IsFinite(end) ||
            start < 0 || end > info.Duration.TotalSeconds ||
            end - start < 1 / info.FramesPerSecond - 0.0000001)
            return false;
        options = options with { TrimStart = TimeSpan.FromSeconds(start), TrimEnd = TimeSpan.FromSeconds(end) };
        return true;
    }

    public void SetVideoEditState(
        bool busy, bool preparing, bool hasPreview, double progress, string status, string? error = null)
    {
        if (!_videoEditActive) return;
        _videoEditBusy = busy;
        _videoEditPreviewReady = hasPreview;
        bool editable = !busy || preparing;
        StabilizeVideoButton.IsEnabled = editable;
        StabilizationSettingsButton.IsEnabled = editable;
        VideoSmoothnessBox.IsEnabled = editable && VideoLockFrameBox.IsChecked != true;
        VideoAutoCropBox.IsEnabled = editable;
        VideoCorrectRotationBox.IsEnabled = editable;
        VideoLockFrameBox.IsEnabled = editable;
        TrimVideoButton.IsEnabled = editable && _videoTrimInfo is not null;
        VideoTrimRange.IsEnabled = editable;
        BrightnessSlider.IsEnabled = editable;
        ContrastSlider.IsEnabled = editable;
        VideoEditSpeedBox.IsEnabled = editable;
        RotateVideoLeftButton.IsEnabled = editable;
        ResetRotationButton.IsEnabled = editable;
        RotateVideoRightButton.IsEnabled = editable;
        UpdateVideoSoundtrackControls(editable);
        PrepareVideoEditButton.IsEnabled = !busy && HasVideoEditChanges && TryGetVideoEditOptions(out _);
        ExportVideoEditButton.IsEnabled = !busy && hasPreview;
        CancelVideoEditButton.IsEnabled = true;
        string exitLabel = VideoEditStrings.Get("VideoEditReset");
        CancelVideoEditButton.Label = busy ? VideoEditStrings.Get("VideoEditCancel") : exitLabel;
        AutomationProperties.SetName(CancelVideoEditButton, CancelVideoEditButton.Label);
        ToolTipService.SetToolTip(CancelVideoEditButton,
            VideoEditStrings.Get(busy ? "VideoEditCancelHelp" : "VideoEditResetHelp"));
        VideoEditProgressRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        VideoEditProgressRing.IsActive = busy;
        _videoEditStatus = error is null
            ? status : VideoEditStrings.Format("VideoEditErrorStatus", error, status);
        if (busy && preparing)
            SetVideoEditProgress(progress);
        else
            VideoEditStatusChanged?.Invoke(this, _videoEditStatus);
    }

    public bool HasVideoEditPreview => _videoEditPreviewReady;

    public void SetVideoEditProgress(double progress)
    {
        string value = (Math.Clamp(progress, 0, 100) / 100).ToString("P0", CultureInfo.CurrentCulture);
        VideoEditStatusChanged?.Invoke(this,
            VideoEditStrings.Format("VideoEditProgressStatus", _videoEditStatus, value));
    }

    public bool HasVideoEditChanges =>
        _videoRotationQuarterTurns != 0 || RemoveAudioBox.IsChecked == true ||
        _videoSoundtrackPath is not null ||
        BrightnessSlider.Value != 0 || ContrastSlider.Value != 100 ||
        VideoEditSpeedBox.SelectedIndex != 2 ||
        StabilizeVideoButton.IsChecked == true || TrimVideoButton.IsChecked == true;

    private void OnVideoTrimChanged(object sender, RoutedEventArgs args)
    {
        if (!_videoEditActive || _syncingVideoEditOptions) return;
        VideoTrimControls.Visibility = TrimVideoButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        NotifyVideoEditOptionsChanged();
    }

    private void OnVideoTrimRangeChanged(object sender, RangeChangedEventArgs args) =>
        NotifyVideoEditOptionsChanged();

    private void ResetVideoTrimRange(VideoTrimInfo info)
    {
        // Integer frame positions keep both handles aligned to the export timebase.
        VideoTrimRange.RangeStart = 0;
        VideoTrimRange.RangeEnd = 0;
        VideoTrimRange.Maximum = Math.Max(1, Math.Floor(info.Duration.TotalSeconds * info.FramesPerSecond + 1e-9));
        VideoTrimRange.RangeEnd = VideoTrimRange.Maximum;
        UpdateVideoTrimLabels();
    }

    private void UpdateVideoTrimLabels()
    {
        if (_videoTrimInfo is not { } info) return;
        VideoTrimStartTime.Text = VideoEditStrings.Format("VideoTrimInTime",
            FormatVideoTrimTime(VideoTrimRange.RangeStart / info.FramesPerSecond));
        VideoTrimEndTime.Text = VideoEditStrings.Format("VideoTrimOutTime",
            FormatVideoTrimTime(VideoTrimRange.RangeEnd / info.FramesPerSecond));
    }

    private static string FormatVideoTrimTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return string.Format(CultureInfo.CurrentCulture, "{0:00}:{1:00}:{2:00}.{3:000}",
            (long)time.TotalHours, time.Minutes, time.Seconds, time.Milliseconds);
    }

    private void OnVideoSmoothnessChanged(object sender, SelectionChangedEventArgs args) =>
        NotifyVideoEditOptionsChanged();

    private void OnVideoEditSliderChanged(object sender, RangeBaseValueChangedEventArgs args) =>
        NotifyVideoEditOptionsChanged();

    private void OnVideoEditSpeedChanged(object sender, SelectionChangedEventArgs args) =>
        NotifyVideoEditOptionsChanged();

    private void OnVideoEditAudioChanged(object sender, RoutedEventArgs args) =>
        NotifyVideoEditOptionsChanged();

    private void OnChooseVideoSoundtrackClick(object sender, RoutedEventArgs args) =>
        VideoSoundtrackPickerClicked?.Invoke(this, EventArgs.Empty);

    private void OnClearVideoSoundtrackClick(object sender, RoutedEventArgs args) =>
        SetVideoSoundtrackPath(null);

    public void SetVideoSoundtrackPath(string? path)
    {
        if (!_videoEditActive) return;
        _videoSoundtrackPath = path;
        UpdateVideoSoundtrackControls(editable: true);
        NotifyVideoEditOptionsChanged();
    }

    private void UpdateVideoSoundtrackControls(bool editable)
    {
        bool hasMusic = _videoSoundtrackPath is not null;
        RemoveAudioBox.IsEnabled = editable && !hasMusic;
        VideoSoundtrackButton.IsEnabled = editable;
        ClearVideoSoundtrackItem.IsEnabled = editable && hasMusic;
        string label = hasMusic ? VideoEditStrings.Get("VideoEditSoundtrackSelected") : _addSoundtrackLabel;
        VideoSoundtrackButton.Label = label;
        string name = hasMusic
            ? VideoEditStrings.Format("VideoEditSoundtrackFile", Path.GetFileName(_videoSoundtrackPath))
            : label;
        AutomationProperties.SetName(VideoSoundtrackButton, name);
        ToolTipService.SetToolTip(VideoSoundtrackButton, hasMusic ? name : _addSoundtrackToolTip);
    }

    private void OnVideoRotateLeftClick(object sender, RoutedEventArgs args) =>
        ChangeVideoRotation(-1);

    private void OnVideoRotateRightClick(object sender, RoutedEventArgs args) =>
        ChangeVideoRotation(1);

    private void OnVideoResetRotationClick(object sender, RoutedEventArgs args)
    {
        if (_videoRotationQuarterTurns == 0) return;
        _videoRotationQuarterTurns = 0;
        UpdateVideoEditLabels();
        NotifyVideoEditOptionsChanged();
    }

    private void ChangeVideoRotation(int turns)
    {
        _videoRotationQuarterTurns = (_videoRotationQuarterTurns + turns + 4) % 4;
        UpdateVideoEditLabels();
        NotifyVideoEditOptionsChanged();
    }

    private void UpdateVideoEditLabels()
    {
        var culture = CultureInfo.CurrentCulture;
        BrightnessValue.Text = ((int)BrightnessSlider.Value).ToString("+0;-0;0", culture);
        ContrastValue.Text = ((int)ContrastSlider.Value).ToString("0", culture) +
            culture.NumberFormat.PercentSymbol;
        int rotationDegrees = _videoRotationQuarterTurns * 90;
        UpdateVideoTrimLabels();
        ResetRotationButton.Label = rotationDegrees.ToString(culture) + "°";
        AutomationProperties.SetName(ResetRotationButton,
            VideoEditStrings.Format(
                "VideoEditRotationStatus",
                rotationDegrees));
    }

    private void NotifyVideoEditOptionsChanged()
    {
        if (_syncingVideoEditOptions || !_videoEditActive) return;
        UpdateVideoEditLabels();
        _videoEditPreviewReady = false;
        ExportVideoEditButton.IsEnabled = false;
        bool valid = TryGetVideoEditOptions(out _);
        PrepareVideoEditButton.IsEnabled = !_videoEditBusy && HasVideoEditChanges && valid;
        _videoEditStatus = VideoEditStrings.Get(valid ? "VideoEditUpdating" : "VideoEditInvalidTrim");
        VideoEditStatusChanged?.Invoke(this, _videoEditStatus);
        VideoEditOptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetPhotoFuseMode(bool active)
    {
        _photoFuseActive = active;
        MakeCopyButton.IsEnabled = !active;
        CropButton.IsEnabled = !active;
        StraightenButton.IsEnabled = !active;
        AdjustButton.IsEnabled = !active;
        EffectsButton.IsEnabled = !active;
        SetQuickEditAvailability(_quickEditsAvailable);
        RetouchButton.IsEnabled = !active;
        SetHistoryAvailability(_canUndo, _canRedo);
        RedEyeButton.IsEnabled = !active;
        SlideshowButton.IsEnabled = !active;
        PhotoFuseCaption.Text = PhotoFuseStrings.Get(active ? "FuseSaveExit" : "FuseTitle");
        AutomationProperties.SetName(PhotoFuseButton, PhotoFuseCaption.Text);
        SetContextSubLabel(active ? PhotoFuseStrings.Get("FuseHint") : null);
    }

    public void PhotoFuseButtonEnabled(bool enabled) => PhotoFuseButton.IsEnabled = enabled;

    public void SetQuickEditAvailability(bool available)
    {
        _quickEditsAvailable = available;
        foreach (var button in QuickEditTools.Children.OfType<Button>())
            button.IsEnabled = available && !_photoFuseActive;
    }

    /// <summary>Switch the ribbon into crop sub-mode (replaces standard viewer tools with Apply/Cancel/Aspect).</summary>
    public void EnterCropMode()
    {
        var moveFocus = HasFocusWithin(ViewerTools);
        ViewerTools.Visibility = Visibility.Collapsed;
        CropTools.Visibility = Visibility.Visible;
        SetContextSubLabel("Drag the handles to set the crop (hold Ctrl to keep proportions), or drag on the photo to draw a new one");
        if (moveFocus) AspectCombo.Focus(FocusState.Keyboard);
    }

    public void ExitCropMode()
    {
        var moveFocus = HasFocusWithin(CropTools);
        ViewerTools.Visibility = Visibility.Visible;
        CropTools.Visibility = Visibility.Collapsed;
        SetContextSubLabel(null);
        if (moveFocus) CropButton.Focus(FocusState.Keyboard);
    }

    public void EnterStraightenMode()
    {
        var moveFocus = HasFocusWithin(ViewerTools) || HasFocusWithin(CropTools);
        ViewerTools.Visibility = Visibility.Collapsed;
        CropTools.Visibility = Visibility.Collapsed;
        StraightenTools.Visibility = Visibility.Visible;
        SetContextSubLabel("Drag along the horizon, fine-tune the angle, or use Auto");
        if (moveFocus) ApplyStraightenButton.Focus(FocusState.Keyboard);
    }

    public void ExitStraightenMode()
    {
        var moveFocus = HasFocusWithin(StraightenTools);
        ViewerTools.Visibility = Visibility.Visible;
        StraightenTools.Visibility = Visibility.Collapsed;
        SetContextSubLabel(null);
        if (moveFocus) StraightenButton.Focus(FocusState.Keyboard);
    }

    private bool HasFocusWithin(DependencyObject ancestor)
    {
        if (XamlRoot is null) return false;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        for (var current = focused; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    public void EnterRedEyeMode()
    {
        ViewerTools.Visibility = Visibility.Collapsed;
        SetContextSubLabel("Drag a rectangle around both eyes (Esc to cancel)");
    }

    public void ExitRedEyeMode()
    {
        ViewerTools.Visibility = Visibility.Visible;
        SetContextSubLabel(null);
    }

    private void OnCropClick(object sender, RoutedEventArgs e) => CropClicked?.Invoke(this, EventArgs.Empty);
    private void OnPhotoFuseClick(object sender, RoutedEventArgs e) => PhotoFuseClicked?.Invoke(this, EventArgs.Empty);
    private void OnMakeCopyClick(object sender, RoutedEventArgs e) =>
        MakeCopyClicked?.Invoke(this, EventArgs.Empty);
    private void OnStraightenClick(object sender, RoutedEventArgs e) =>
        StraightenClicked?.Invoke(this, EventArgs.Empty);
    private void OnAdjustClick(object sender, RoutedEventArgs e) => AdjustClicked?.Invoke(this, EventArgs.Empty);
    private void OnEffectsClick(object sender, RoutedEventArgs e) => EffectsClicked?.Invoke(this, EventArgs.Empty);
    private void OnQuickEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<ViewerQuickEdit>(tag, out var action))
            QuickEditClicked?.Invoke(this, action);
    }
    private void OnUndoClick(object sender, RoutedEventArgs e) => UndoClicked?.Invoke(this, EventArgs.Empty);
    private void OnRedoClick(object sender, RoutedEventArgs e) => RedoClicked?.Invoke(this, EventArgs.Empty);
    private void OnRetouchClick(object sender, RoutedEventArgs e) => RetouchClicked?.Invoke(this, EventArgs.Empty);
    private void OnRedEyeClick(object sender, RoutedEventArgs e) => RedEyeClicked?.Invoke(this, EventArgs.Empty);
    private void OnSlideshowClick(SplitButton sender, SplitButtonClickEventArgs e) =>
        SlideshowClicked?.Invoke(this, EventArgs.Empty);

    public void SetSlideshowTheme(SlideshowTheme theme)
    {
        var option = SlideshowThemeOptions.Children.OfType<RadioButton>().Single(
            button => (string)button.Tag == theme.ToString());
        option.IsChecked = true;
        UpdateSlideshowThemeName(theme);
    }

    private void OnSlideshowThemeChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radioButton ||
            !Enum.TryParse<SlideshowTheme>(radioButton.Tag as string, out var theme))
            return;

        UpdateSlideshowThemeName(theme);
        SlideshowThemeChanged?.Invoke(this, theme);
    }

    private void UpdateSlideshowThemeName(SlideshowTheme theme)
    {
        var themeName = GetThemeName(theme);
        AutomationProperties.SetName(SlideshowButton, $"Slideshow, {themeName} theme");
        ToolTipService.SetToolTip(
            SlideshowButton,
            $"Play a full-screen slideshow starting at this photo. Current theme: {themeName}. Open the arrow to change it.");
    }

    private static string GetThemeName(SlideshowTheme theme) => theme switch
    {
        SlideshowTheme.PanAndZoom => "Pan and Zoom",
        SlideshowTheme.Contemporary => "Contemporary",
        SlideshowTheme.Cinematic => "Cinematic",
        SlideshowTheme.Fade => "Fade",
        SlideshowTheme.BlackAndWhite => "Black and White",
        SlideshowTheme.Sepia => "Sepia",
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unknown slideshow theme.")
    };
    private void OnVideoEditPrepareClick(object sender, RoutedEventArgs e) =>
        VideoEditPrepareClicked?.Invoke(this, EventArgs.Empty);
    private void OnVideoEditExportClick(object sender, RoutedEventArgs e) =>
        VideoEditExportClicked?.Invoke(this, EventArgs.Empty);
    private void OnVideoEditCancelClick(object sender, RoutedEventArgs e) =>
        VideoEditCancelClicked?.Invoke(this, EventArgs.Empty);
    private void OnApplyCropClick(object sender, RoutedEventArgs e) => ApplyCropClicked?.Invoke(this, EventArgs.Empty);
    private void OnCancelCropClick(object sender, RoutedEventArgs e) => CancelCropClicked?.Invoke(this, EventArgs.Empty);
    private void OnApplyStraightenClick(object sender, RoutedEventArgs e) =>
        ApplyStraightenClicked?.Invoke(this, EventArgs.Empty);
    private void OnCancelStraightenClick(object sender, RoutedEventArgs e) =>
        CancelStraightenClicked?.Invoke(this, EventArgs.Empty);

    private void OnAspectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AspectCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag
            && Enum.TryParse<CropAspectRatio>(tag, out var ratio))
        {
            CropAspectChanged?.Invoke(this, ratio);
        }
    }
}
