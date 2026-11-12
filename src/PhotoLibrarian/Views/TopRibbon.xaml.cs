// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Services;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoLibrarian.Core.Models;
using System;

namespace PhotoLibrarian.Views;

public sealed partial class TopRibbon : UserControl
{
    private bool _photoFuseActive;
    private bool _canUndo;
    private bool _canRedo;
    private bool _hasPhoto;
    private bool _editorActive;
    private bool _isVideo;
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
    public event EventHandler? StabilizeVideoClicked;
    public event EventHandler? PhotoFuseClicked;
    public event EventHandler? EditVideoClicked;
    public event EventHandler? TrimVideoClicked;
    public event EventHandler? ApplyCropClicked;
    public event EventHandler? CancelCropClicked;
    public event EventHandler? ApplyStraightenClicked;
    public event EventHandler? CancelStraightenClicked;
    public event EventHandler<CropAspectRatio>? CropAspectChanged;

    public TopRibbon()
    {
        this.InitializeComponent();
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
        ViewerTools.Visibility = _hasPhoto && !_editorActive && !isVideo ? Visibility.Visible : Visibility.Collapsed;
        VideoTools.Visibility = _hasPhoto && !_editorActive && isVideo ? Visibility.Visible : Visibility.Collapsed;
        CropTools.Visibility = Visibility.Collapsed;
        StraightenTools.Visibility = Visibility.Collapsed;
        SetContextSubLabel(null);
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
    private void OnStabilizeVideoClick(object sender, RoutedEventArgs e) =>
        StabilizeVideoClicked?.Invoke(this, EventArgs.Empty);
    private void OnEditVideoClick(object sender, RoutedEventArgs e) =>
        EditVideoClicked?.Invoke(this, EventArgs.Empty);
    private void OnTrimVideoClick(object sender, RoutedEventArgs e) =>
        TrimVideoClicked?.Invoke(this, EventArgs.Empty);
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
