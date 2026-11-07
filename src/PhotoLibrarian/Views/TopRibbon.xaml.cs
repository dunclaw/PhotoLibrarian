// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Core.Models;
using System;

namespace PhotoLibrarian.Views;

public sealed partial class TopRibbon : UserControl
{
    public event EventHandler? CropClicked;
    public event EventHandler? MakeCopyClicked;
    public event EventHandler? StraightenClicked;
    public event EventHandler? AdjustClicked;
    public event EventHandler? RedEyeClicked;
    public event EventHandler? SlideshowClicked;
    public event EventHandler<SlideshowTheme>? SlideshowThemeChanged;
    public event EventHandler? StabilizeVideoClicked;
    public event EventHandler? ApplyCropClicked;
    public event EventHandler? CancelCropClicked;
    public event EventHandler? ApplyStraightenClicked;
    public event EventHandler? CancelStraightenClicked;
    public event EventHandler<CropAspectRatio>? CropAspectChanged;

    public TopRibbon()
    {
        this.InitializeComponent();
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
        ViewerTools.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;
        VideoTools.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        CropTools.Visibility = Visibility.Collapsed;
        StraightenTools.Visibility = Visibility.Collapsed;
        SetContextSubLabel(null);
    }

    /// <summary>Switch the ribbon into crop sub-mode (replaces standard viewer tools with Apply/Cancel/Aspect).</summary>
    public void EnterCropMode()
    {
        ViewerTools.Visibility = Visibility.Collapsed;
        CropTools.Visibility = Visibility.Visible;
        SetContextSubLabel("Drag the handles to set the crop (hold Ctrl to keep proportions), or drag on the photo to draw a new one");
    }

    public void ExitCropMode()
    {
        ViewerTools.Visibility = Visibility.Visible;
        CropTools.Visibility = Visibility.Collapsed;
        SetContextSubLabel(null);
    }

    public void EnterStraightenMode()
    {
        ViewerTools.Visibility = Visibility.Collapsed;
        CropTools.Visibility = Visibility.Collapsed;
        StraightenTools.Visibility = Visibility.Visible;
        SetContextSubLabel("Drag along the horizon, fine-tune the angle, or use Auto");
    }

    public void ExitStraightenMode()
    {
        ViewerTools.Visibility = Visibility.Visible;
        StraightenTools.Visibility = Visibility.Collapsed;
        SetContextSubLabel(null);
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
    private void OnMakeCopyClick(object sender, RoutedEventArgs e) =>
        MakeCopyClicked?.Invoke(this, EventArgs.Empty);
    private void OnStraightenClick(object sender, RoutedEventArgs e) =>
        StraightenClicked?.Invoke(this, EventArgs.Empty);
    private void OnAdjustClick(object sender, RoutedEventArgs e) => AdjustClicked?.Invoke(this, EventArgs.Empty);
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
