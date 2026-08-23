using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using System.Numerics;

namespace PhotoLibrarian.Views;

/// <summary>
/// Image editor view with Win2D GPU-accelerated effect pipeline. The preview and the save path
/// share the same effect graph (see <see cref="EditEffectGraph"/>), and saving bakes the result
/// into the file on disk via <see cref="ImageEditRenderer"/>.
/// </summary>
public sealed partial class ImageEditorView : UserControl
{
    private CanvasBitmap? _sourceBitmap;
    private ImageEditorViewModel? ViewModel => App.ViewModel?.ImageEditor;
    private bool _suppressSliderEvents;

    public ImageEditorView()
    {
        _suppressSliderEvents = true;
        this.InitializeComponent();
        _suppressSliderEvents = false;
        this.Loaded += OnLoaded;
        this.Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.ParametersChanged += OnParametersChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.ParametersChanged -= OnParametersChanged;
        }
        _sourceBitmap?.Dispose();
        _sourceBitmap = null;
        EditCanvas.RemoveFromVisualTree();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel is null) return;
            switch (e.PropertyName)
            {
                case nameof(ImageEditorViewModel.IsOpen):
                    Visibility = ViewModel.IsOpen ? Visibility.Visible : Visibility.Collapsed;
                    if (ViewModel.IsOpen) SyncSlidersFromViewModel();
                    break;
                case nameof(ImageEditorViewModel.ImagePath):
                    _ = LoadImageAsync(ViewModel.ImagePath);
                    break;
                case nameof(ImageEditorViewModel.Title):
                    TitleText.Text = ViewModel.Title;
                    break;
                case nameof(ImageEditorViewModel.HasBackup):
                    RevertBtn.Visibility = ViewModel.HasBackup ? Visibility.Visible : Visibility.Collapsed;
                    break;
            }
        });
    }

    private void OnParametersChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => EditCanvas.Invalidate());
    }

    private void OnCanvasCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        // The editor is opened while collapsed, so the image path can be set before the canvas
        // has a device. Reload here so the preview appears as soon as resources exist.
        args.TrackAsyncAction(LoadImageAsync(ViewModel?.ImagePath).AsAsyncAction());
    }

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_sourceBitmap is null || ViewModel is null) return;

        var p = ViewModel.GetCurrentParameters();
        var effect = EditEffectGraph.Build(
            _sourceBitmap,
            new Vector2((float)_sourceBitmap.Size.Width, (float)_sourceBitmap.Size.Height),
            p);

        // Fit the *output* extent — rotation makes it larger than the source — so the preview
        // frames exactly what a save would write.
        var imageSize = _sourceBitmap.Size;
        var (originOffset, outputWidth, outputHeight) =
            EditEffectGraph.ComputeOutputExtent(imageSize.Width, imageSize.Height, p.RotationAngle);

        var canvasSize = sender.Size;
        var scale = Math.Min(
            canvasSize.Width / outputWidth,
            canvasSize.Height / outputHeight);
        scale = Math.Min(scale, 1.0); // Don't upscale

        var scaledW = outputWidth * scale;
        var scaledH = outputHeight * scale;
        var offsetX = (canvasSize.Width - scaledW) / 2;
        var offsetY = (canvasSize.Height - scaledH) / 2;

        args.DrawingSession.Transform =
            Matrix3x2.CreateTranslation(originOffset) *
            Matrix3x2.CreateScale((float)scale) *
            Matrix3x2.CreateTranslation((float)offsetX, (float)offsetY);

        args.DrawingSession.DrawImage(effect);
    }

    private async Task LoadImageAsync(string? path)
    {
        _sourceBitmap?.Dispose();
        _sourceBitmap = null;

        if (path is null) return;

        try
        {
            _sourceBitmap = await ImageEditRenderer.LoadOrientedAsync(EditCanvas, path);
            EditCanvas.Invalidate();
        }
        catch { /* Failed to load */ }
    }

    private void OnSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSliderEvents || ViewModel is null) return;

        ViewModel.Brightness = BrightnessSlider.Value;
        ViewModel.Contrast = ContrastSlider.Value;
        ViewModel.Exposure = ExposureSlider.Value;
        ViewModel.Highlights = HighlightsSlider.Value;
        ViewModel.Shadows = ShadowsSlider.Value;
        ViewModel.Saturation = SaturationSlider.Value;
        ViewModel.Temperature = TemperatureSlider.Value;
        ViewModel.Tint = TintSlider.Value;
        ViewModel.Clarity = ClaritySlider.Value;
        ViewModel.Sharpness = SharpnessSlider.Value;
        ViewModel.BlackPoint = BlackPointSlider.Value;
        ViewModel.WhitePoint = WhitePointSlider.Value;
        ViewModel.Midtones = MidtonesSlider.Value;
        ViewModel.RotationAngle = RotationSlider.Value;
    }

    private void OnAutoEnhance(object sender, RoutedEventArgs e)
    {
        ViewModel?.AutoEnhanceCommand.Execute(null);
        SyncSlidersFromViewModel();
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        ViewModel?.ResetAllCommand.Execute(null);
        SyncSlidersFromViewModel();
    }

    private async void OnRevert(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.RevertToOriginalCommand.CanExecute(null) == true)
        {
            // Let go of the file before it is overwritten by the backup copy.
            _sourceBitmap?.Dispose();
            _sourceBitmap = null;

            await ViewModel.RevertToOriginalCommand.ExecuteAsync(null);
            SyncSlidersFromViewModel();
            await LoadImageAsync(ViewModel.ImagePath);
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || ViewModel.IsSaving) return;

        if (!ViewModel.HasChanges)
        {
            SetStatus("No adjustments to save");
            return;
        }

        SaveBtn.IsEnabled = false;
        SetStatus("Applying adjustments…");
        try
        {
            // Release our handle on the file before the renderer rewrites it.
            _sourceBitmap?.Dispose();
            _sourceBitmap = null;

            var saved = await ViewModel.SaveAsync(ImageEditRenderer.RenderToFileAsync);
            SyncSlidersFromViewModel();

            // Reload from disk so the preview shows the baked pixels.
            await LoadImageAsync(ViewModel.ImagePath);

            if (!saved) SetStatus("No adjustments to save");
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}");
            await LoadImageAsync(ViewModel.ImagePath);
        }
        finally
        {
            SaveBtn.IsEnabled = true;
        }
    }

    private static void SetStatus(string text)
    {
        if (App.ViewModel is not null) App.ViewModel.StatusText = text;
    }

    private void OnClose(object sender, RoutedEventArgs e) => ViewModel?.CloseCommand.Execute(null);

    private void SyncSlidersFromViewModel()
    {
        if (ViewModel is null) return;
        _suppressSliderEvents = true;
        BrightnessSlider.Value = ViewModel.Brightness;
        ContrastSlider.Value = ViewModel.Contrast;
        ExposureSlider.Value = ViewModel.Exposure;
        HighlightsSlider.Value = ViewModel.Highlights;
        ShadowsSlider.Value = ViewModel.Shadows;
        SaturationSlider.Value = ViewModel.Saturation;
        TemperatureSlider.Value = ViewModel.Temperature;
        TintSlider.Value = ViewModel.Tint;
        ClaritySlider.Value = ViewModel.Clarity;
        SharpnessSlider.Value = ViewModel.Sharpness;
        BlackPointSlider.Value = ViewModel.BlackPoint;
        WhitePointSlider.Value = ViewModel.WhitePoint;
        MidtonesSlider.Value = ViewModel.Midtones;
        RotationSlider.Value = ViewModel.RotationAngle;
        _suppressSliderEvents = false;
    }
}
