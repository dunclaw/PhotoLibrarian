using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ViewModels;
using System.Numerics;

namespace PhotoLibrarian.Views;

/// <summary>
/// Image editor view with Win2D GPU-accelerated effect pipeline.
/// Effect chain: Source → Exposure → Brightness/Contrast → Saturation →
/// Temperature/Tint → Highlights/Shadows → Levels → Clarity → Sharpness → Rotation → Output
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
        // Resources are created when image is loaded
    }

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_sourceBitmap is null || ViewModel is null) return;

        var p = ViewModel.GetCurrentParameters();
        var effect = BuildEffectGraph(_sourceBitmap, p);

        // Center the image in the canvas
        var imageSize = _sourceBitmap.Size;
        var canvasSize = sender.Size;
        var scale = Math.Min(
            canvasSize.Width / imageSize.Width,
            canvasSize.Height / imageSize.Height);
        scale = Math.Min(scale, 1.0); // Don't upscale

        var scaledW = imageSize.Width * scale;
        var scaledH = imageSize.Height * scale;
        var offsetX = (canvasSize.Width - scaledW) / 2;
        var offsetY = (canvasSize.Height - scaledH) / 2;

        args.DrawingSession.Transform = Matrix3x2.CreateScale((float)scale) *
            Matrix3x2.CreateTranslation((float)offsetX, (float)offsetY);

        args.DrawingSession.DrawImage(effect);
    }

    /// <summary>
    /// Builds the Win2D GPU effect pipeline from EditParameters.
    /// </summary>
    private static ICanvasImage BuildEffectGraph(CanvasBitmap source, EditParameters p)
    {
        ICanvasImage current = source;

        // 1. Exposure (simulated via brightness/gamma)
        if (p.Exposure != 0)
        {
            current = new ExposureEffect
            {
                Source = current,
                Exposure = (float)p.Exposure * 2.0f // Scale to useful range
            };
        }

        // 2. Brightness & Contrast
        if (p.Brightness != 0 || p.Contrast != 0)
        {
            current = new BrightnessEffect
            {
                Source = current,
                WhitePoint = new Vector2(
                    1.0f + (float)p.Brightness * 0.5f,
                    1.0f + (float)p.Brightness * 0.5f)
            };

            if (p.Contrast != 0)
            {
                current = new ContrastEffect
                {
                    Source = current,
                    Contrast = (float)p.Contrast
                };
            }
        }

        // 3. Saturation
        if (p.Saturation != 0)
        {
            current = new SaturationEffect
            {
                Source = current,
                Saturation = 1.0f + (float)p.Saturation // 0=grayscale, 1=normal, 2=double
            };
        }

        // 4. Temperature & Tint (simulated via color matrix)
        if (p.Temperature != 0 || p.Tint != 0)
        {
            var temp = (float)p.Temperature * 0.3f;
            var tint = (float)p.Tint * 0.3f;
            current = new ColorMatrixEffect
            {
                Source = current,
                ColorMatrix = new Matrix5x4
                {
                    M11 = 1 + temp, M12 = 0, M13 = 0, M14 = 0,
                    M21 = 0, M22 = 1 + tint, M23 = 0, M24 = 0,
                    M31 = 0, M32 = 0, M33 = 1 - temp, M34 = 0,
                    M41 = 0, M42 = 0, M43 = 0, M44 = 1,
                    M51 = 0, M52 = 0, M53 = 0, M54 = 0
                }
            };
        }

        // 5. Highlights & Shadows (via gamma curves)
        if (p.Highlights != 0 || p.Shadows != 0)
        {
            current = new HighlightsAndShadowsEffect
            {
                Source = current,
                Highlights = (float)p.Highlights,
                Shadows = (float)p.Shadows,
                Clarity = (float)p.Clarity
            };
        }

        // 6. Levels (black point, white point, midtones via transfer table)
        if (p.BlackPoint != 0 || p.WhitePoint != 1.0 || p.Midtones != 0.5)
        {
            var gamma = p.Midtones > 0 ? Math.Log(0.5) / Math.Log(p.Midtones) : 1.0;
            var bp = (float)p.BlackPoint;
            var wp = (float)p.WhitePoint;
            var g = (float)gamma;

            // Generate transfer table
            var table = new float[256];
            for (int i = 0; i < 256; i++)
            {
                float v = i / 255f;
                v = Math.Clamp((v - bp) / (wp - bp), 0, 1);
                v = MathF.Pow(v, 1f / g);
                table[i] = v;
            }

            current = new TableTransferEffect
            {
                Source = current,
                RedTable = table,
                GreenTable = table,
                BlueTable = table
            };
        }

        // 7. Sharpness (via unsharp mask)
        if (p.Sharpness > 0)
        {
            current = new SharpenEffect
            {
                Source = current,
                Amount = (float)p.Sharpness * 5.0f, // 0-5 range
                Threshold = 0.05f
            };
        }

        // 8. Rotation
        if (p.RotationAngle != 0)
        {
            current = new Transform2DEffect
            {
                Source = current,
                TransformMatrix = Matrix3x2.CreateRotation(
                    (float)(p.RotationAngle * Math.PI / 180),
                    new Vector2((float)source.Size.Width / 2, (float)source.Size.Height / 2))
            };
        }

        return current;
    }

    private async Task LoadImageAsync(string? path)
    {
        _sourceBitmap?.Dispose();
        _sourceBitmap = null;

        if (path is null) return;

        try
        {
            _sourceBitmap = await CanvasBitmap.LoadAsync(EditCanvas, path);
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
            await ViewModel.RevertToOriginalCommand.ExecuteAsync(null);
            SyncSlidersFromViewModel();
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // Save will be implemented in p2-save-edits
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
