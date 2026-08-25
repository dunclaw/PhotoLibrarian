using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace PhotoLibrarian.Views;

public sealed partial class TopRibbon : UserControl
{
    public event EventHandler? CropClicked;
    public event EventHandler? StraightenClicked;
    public event EventHandler? AdjustClicked;
    public event EventHandler? CloseViewerClicked;
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

    /// <summary>Switch the ribbon into crop sub-mode (replaces standard viewer tools with Apply/Cancel/Aspect).</summary>
    public void EnterCropMode()
    {
        ViewerTools.Visibility = Visibility.Collapsed;
        CropTools.Visibility = Visibility.Visible;
        CloseViewerButton.Visibility = Visibility.Collapsed;
        SetContextSubLabel("Drag the handles to set the crop (hold Ctrl to keep proportions), or drag on the photo to draw a new one");
    }

    public void ExitCropMode()
    {
        ViewerTools.Visibility = Visibility.Visible;
        CropTools.Visibility = Visibility.Collapsed;
        CloseViewerButton.Visibility = Visibility.Visible;
        SetContextSubLabel(null);
    }

    public void EnterStraightenMode()
    {
        ViewerTools.Visibility = Visibility.Collapsed;
        CropTools.Visibility = Visibility.Collapsed;
        StraightenTools.Visibility = Visibility.Visible;
        CloseViewerButton.Visibility = Visibility.Collapsed;
        SetContextSubLabel("Drag along the horizon, fine-tune the angle, or use Auto");
    }

    public void ExitStraightenMode()
    {
        ViewerTools.Visibility = Visibility.Visible;
        StraightenTools.Visibility = Visibility.Collapsed;
        CloseViewerButton.Visibility = Visibility.Visible;
        SetContextSubLabel(null);
    }

    private void OnCropClick(object sender, RoutedEventArgs e) => CropClicked?.Invoke(this, EventArgs.Empty);
    private void OnStraightenClick(object sender, RoutedEventArgs e) =>
        StraightenClicked?.Invoke(this, EventArgs.Empty);
    private void OnAdjustClick(object sender, RoutedEventArgs e) => AdjustClicked?.Invoke(this, EventArgs.Empty);
    private void OnCloseViewerClick(object sender, RoutedEventArgs e) => CloseViewerClicked?.Invoke(this, EventArgs.Empty);
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
