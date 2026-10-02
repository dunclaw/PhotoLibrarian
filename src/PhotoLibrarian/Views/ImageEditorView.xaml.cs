using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using System.Numerics;
using Windows.Foundation;

namespace PhotoLibrarian.Views;

/// <summary>
/// Image editor view with Win2D GPU-accelerated effect pipeline. The preview and the save path
/// share the same effect graph (see <see cref="EditEffectGraph"/>), and saving bakes the result
/// into the file on disk via <see cref="ImageEditRenderer"/>.
/// </summary>
public sealed partial class ImageEditorView : UserControl
{
    private CanvasBitmap? _sourceBitmap;
    private byte[]? _sourcePixels;
    private ImageEditorViewModel? ViewModel => App.ViewModel?.ImageEditor;
    private bool _suppressSliderEvents;
    private int _draggedHistogramAnchor;
    private readonly EditorViewport _viewport = new();
    private string? _loadedPath;
    private uint? _panPointerId;
    private Vector2 _lastPanPoint;
    private readonly Dictionary<Slider, (double Neutral, double Threshold)> _sliderSnaps = new();
    private Slider? _draggedSlider;

    public ImageEditorView()
    {
        _suppressSliderEvents = true;
        this.InitializeComponent();
        _suppressSliderEvents = false;
        RegisterCenterSnaps();
        this.Loaded += OnLoaded;
        this.Unloaded += OnUnloaded;
        AddZoomAccelerator(Windows.System.VirtualKey.Add, () => ZoomBy(zoomIn: true));
        AddZoomAccelerator((Windows.System.VirtualKey)187, () => ZoomBy(zoomIn: true)); // OEM '=' / '+'
        AddZoomAccelerator(Windows.System.VirtualKey.Subtract, () => ZoomBy(zoomIn: false));
        AddZoomAccelerator((Windows.System.VirtualKey)189, () => ZoomBy(zoomIn: false)); // OEM '-'
        AddZoomAccelerator(Windows.System.VirtualKey.Number0, ZoomFit);
        AddZoomAccelerator(Windows.System.VirtualKey.Number1, ZoomActual);
    }

    private void AddZoomAccelerator(Windows.System.VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = Windows.System.VirtualKeyModifiers.Control
        };
        accelerator.Invoked += (_, args) =>
        {
            action();
            args.Handled = true;
        };
        KeyboardAccelerators.Add(accelerator);
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
        _sourcePixels = null;
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
        DispatcherQueue.TryEnqueue(() => HistogramCanvas.Invalidate());
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

        // Frame the *output* extent — rotation makes it larger than the source — so the preview
        // shows exactly what a save would write.
        var imageSize = _sourceBitmap.Size;
        var (originOffset, outputWidth, outputHeight) =
            EditEffectGraph.ComputeOutputExtent(imageSize.Width, imageSize.Height, p.RotationAngle);

        _viewport.SetCanvasSize(new Vector2((float)sender.Size.Width, (float)sender.Size.Height));
        _viewport.SetContentSize(new Vector2(outputWidth, outputHeight), resetView: false);
        var origin = _viewport.ContentOrigin;

        args.DrawingSession.Transform =
            Matrix3x2.CreateTranslation(originOffset) *
            Matrix3x2.CreateScale(_viewport.Scale) *
            Matrix3x2.CreateTranslation(origin);

        args.DrawingSession.DrawImage(effect);
    }

    private void OnEditCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _viewport.SetCanvasSize(new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height));
        UpdateZoomText();
    }

    private void OnEditCanvasPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(EditCanvas);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0 || _sourceBitmap is null) return;
        var anchor = new Vector2((float)point.Position.X, (float)point.Position.Y);
        UpdateActualSizeScale();
        if (delta > 0) _viewport.ZoomIn(anchor);
        else _viewport.ZoomOut(anchor);
        OnViewportChanged();
        e.Handled = true;
    }

    private void OnEditCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(EditCanvas);
        if (_viewport.IsFit || !(point.Properties.IsLeftButtonPressed || point.Properties.IsMiddleButtonPressed || point.IsInContact))
            return;
        if (!EditCanvas.CapturePointer(e.Pointer)) return;
        _panPointerId = e.Pointer.PointerId;
        _lastPanPoint = new Vector2((float)point.Position.X, (float)point.Position.Y);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
        e.Handled = true;
    }

    private void OnEditCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_panPointerId != e.Pointer.PointerId) return;
        var position = e.GetCurrentPoint(EditCanvas).Position;
        var current = new Vector2((float)position.X, (float)position.Y);
        _viewport.PanBy(current - _lastPanPoint);
        _lastPanPoint = current;
        EditCanvas.Invalidate();
        e.Handled = true;
    }

    private void OnEditCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_panPointerId != e.Pointer.PointerId) return;
        EditCanvas.ReleasePointerCapture(e.Pointer);
        EndPan();
    }

    private void OnEditCanvasPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_panPointerId == e.Pointer.PointerId) EndPan();
    }

    private void EndPan()
    {
        _panPointerId = null;
        ProtectedCursor = null;
    }

    private void OnEditCanvasDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_sourceBitmap is null) return;
        if (_viewport.IsFit)
        {
            var position = e.GetPosition(EditCanvas);
            UpdateActualSizeScale();
            _viewport.ZoomTo(_viewport.ActualSizeScale, new Vector2((float)position.X, (float)position.Y));
        }
        else
        {
            _viewport.Fit();
        }
        OnViewportChanged();
        e.Handled = true;
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomBy(zoomIn: true);
    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomBy(zoomIn: false);
    private void OnZoomFit(object sender, RoutedEventArgs e) => ZoomFit();
    private void OnZoomActual(object sender, RoutedEventArgs e) => ZoomActual();

    private void ZoomBy(bool zoomIn)
    {
        if (_sourceBitmap is null) return;
        UpdateActualSizeScale();
        if (zoomIn) _viewport.ZoomIn();
        else _viewport.ZoomOut();
        OnViewportChanged();
    }

    private void ZoomFit()
    {
        _viewport.Fit();
        OnViewportChanged();
    }

    private void ZoomActual()
    {
        if (_sourceBitmap is null) return;
        UpdateActualSizeScale();
        _viewport.ZoomTo(_viewport.ActualSizeScale);
        OnViewportChanged();
    }

    private void UpdateActualSizeScale()
    {
        // The bitmap is 96 DPI, so one image pixel is one DIP; one physical pixel is 1/scale DIPs.
        var rasterScale = XamlRoot?.RasterizationScale ?? 1.0;
        _viewport.ActualSizeScale = (float)(1.0 / Math.Max(rasterScale, 0.1));
    }

    private void OnViewportChanged()
    {
        UpdateZoomText();
        EditCanvas.Invalidate();
    }

    private void UpdateZoomText()
    {
        if (_viewport.IsFit)
        {
            ZoomText.Text = "Fit";
            return;
        }
        UpdateActualSizeScale();
        var percent = _viewport.Scale / _viewport.ActualSizeScale * 100;
        ZoomText.Text = $"{percent:0}%";
    }

    private async Task LoadImageAsync(string? path)
    {
        _sourceBitmap?.Dispose();
        _sourceBitmap = null;
        _sourcePixels = null;

        if (path is null)
        {
            _loadedPath = null;
            _viewport.Fit();
            UpdateZoomText();
            return;
        }

        try
        {
            _sourceBitmap = await ImageEditRenderer.LoadOrientedAsync(EditCanvas, path);
            _sourcePixels = _sourceBitmap.GetPixelBytes();

            // A different photo starts at fit; reloading the same file after save/revert keeps
            // the user's zoom so they can keep inspecting the same detail.
            if (!string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _viewport.Fit();
                _loadedPath = path;
            }
            var size = _sourceBitmap.Size;
            var (_, outputWidth, outputHeight) = EditEffectGraph.ComputeOutputExtent(
                size.Width, size.Height, ViewModel?.RotationAngle ?? 0);
            _viewport.SetContentSize(new Vector2(outputWidth, outputHeight), resetView: false);
            UpdateZoomText();
            EditCanvas.Invalidate();
            HistogramCanvas.Invalidate();
        }
        catch { /* Failed to load */ }
    }

    private void OnHistogramDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_sourcePixels is null || ViewModel is null) return;

        var histogram = ImageHistogram.Calculate(_sourcePixels, ViewModel.GetCurrentParameters());
        var width = sender.Size.Width;
        var height = sender.Size.Height;
        var foreground = GetResourceColor("TextFillColorSecondaryBrush", Microsoft.UI.Colors.Gray);
        var accent = GetResourceColor("AccentFillColorDefaultBrush", Microsoft.UI.Colors.CornflowerBlue);

        for (var i = 0; i < histogram.Length; i++)
        {
            var x = (i + 0.5f) / histogram.Length * (float)width;
            var top = (float)height - histogram[i] * (float)(height - 8);
            args.DrawingSession.DrawLine(x, (float)height, x, top, foreground, 2);
        }

        DrawHistogramAnchor(args.DrawingSession, width, ViewModel.BlackPoint, accent, height);
        DrawHistogramAnchor(args.DrawingSession, width, ViewModel.Midtones, accent, height);
        DrawHistogramAnchor(args.DrawingSession, width, ViewModel.WhitePoint, accent, height);
    }

    private static void DrawHistogramAnchor(
        CanvasDrawingSession session, double width, double value, Windows.UI.Color color, double height)
    {
        var x = (float)Math.Clamp(value, 0, 1) * (float)width;
        session.DrawLine(x, 0, x, (float)height, color, 2);
        session.FillCircle(new Vector2(x, 5), 5, color);
    }

    private static Windows.UI.Color GetResourceColor(string key, Windows.UI.Color fallback)
    {
        return Application.Current.Resources.TryGetValue(key, out var value) &&
               value is Microsoft.UI.Xaml.Media.SolidColorBrush brush
            ? brush.Color
            : fallback;
    }

    private void OnHistogramPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var position = e.GetCurrentPoint(HistogramCanvas).Position;
        _draggedHistogramAnchor = FindHistogramAnchor(position.X / HistogramCanvas.ActualWidth);
        if (_draggedHistogramAnchor != 0)
        {
            HistogramCanvas.CapturePointer(e.Pointer);
            UpdateHistogramAnchor(position.X);
        }
        e.Handled = true;
    }

    private void OnHistogramPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_draggedHistogramAnchor == 0) return;
        UpdateHistogramAnchor(e.GetCurrentPoint(HistogramCanvas).Position.X);
        e.Handled = true;
    }

    private void OnHistogramPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndHistogramDrag(e.Pointer);
    }

    private void OnHistogramPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        EndHistogramDrag(e.Pointer);
    }

    private void EndHistogramDrag(Pointer pointer)
    {
        if (_draggedHistogramAnchor == 0) return;
        HistogramCanvas.ReleasePointerCapture(pointer);
        _draggedHistogramAnchor = 0;
    }

    private int FindHistogramAnchor(double position)
    {
        var anchors = new[] { ViewModel!.BlackPoint, ViewModel.Midtones, ViewModel.WhitePoint };
        var closest = 0;
        var distance = double.MaxValue;
        for (var i = 0; i < anchors.Length; i++)
        {
            var candidateDistance = Math.Abs(position - anchors[i]);
            if (candidateDistance < distance)
            {
                distance = candidateDistance;
                closest = i + 1;
            }
        }
        return distance <= 0.08 ? closest : 0;
    }

    private void UpdateHistogramAnchor(double x)
    {
        if (ViewModel is null || HistogramCanvas.ActualWidth <= 0) return;
        var value = Math.Clamp(x / HistogramCanvas.ActualWidth, 0, 1);
        switch (_draggedHistogramAnchor)
        {
            case 1:
                BlackPointSlider.Value = Math.Min(value, WhitePointSlider.Value - 0.01);
                break;
            case 2:
                MidtonesSlider.Value = Math.Clamp(value, 0.1, 0.9);
                break;
            case 3:
                WhitePointSlider.Value = Math.Max(value, BlackPointSlider.Value + 0.01);
                break;
        }
    }

    private void RegisterCenterSnaps()
    {
        Slider[] centered =
        [
            ExposureSlider, BrightnessSlider, ContrastSlider, HighlightsSlider, ShadowsSlider,
            SaturationSlider, TemperatureSlider, TintSlider, ClaritySlider, MidtonesSlider
        ];
        foreach (var slider in centered)
        {
            var neutral = (slider.Minimum + slider.Maximum) / 2;
            AddCenterSnap(slider, neutral, SliderSnap.ThresholdFor(slider.Minimum, slider.Maximum));
        }

        // A percentage of ±180° would swallow small straightening angles, so rotation snaps within 1°.
        AddCenterSnap(RotationSlider, 0, 1);
    }

    private void AddCenterSnap(Slider slider, double neutral, double threshold)
    {
        _sliderSnaps[slider] = (neutral, threshold);
        // Slider marks pointer events handled internally, so listen for handled events too.
        slider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _draggedSlider = slider), true);
        slider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndSliderDrag(slider)), true);
        slider.AddHandler(PointerCanceledEvent, new PointerEventHandler((_, _) => EndSliderDrag(slider)), true);
        slider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndSliderDrag(slider)), true);
    }

    private void EndSliderDrag(Slider slider)
    {
        if (_draggedSlider == slider) _draggedSlider = null;
    }

    private void OnSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSliderEvents || ViewModel is null) return;

        // Snap only while dragging so arrow-key fine tuning can still reach values near neutral.
        if (sender is Slider slider && slider == _draggedSlider &&
            _sliderSnaps.TryGetValue(slider, out var snap))
        {
            var snapped = SliderSnap.Apply(e.NewValue, snap.Neutral, snap.Threshold);
            if (snapped != e.NewValue)
            {
                slider.Value = snapped; // Re-raises ValueChanged with the neutral value.
                return;
            }
        }

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
        HistogramCanvas.Invalidate();
    }

    private void OnAutoEnhance(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && _sourcePixels is not null)
            ViewModel.ApplyAutoEnhance(AutoEnhanceAnalyzer.Analyze(_sourcePixels));
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
        HistogramCanvas.Invalidate();
        _suppressSliderEvents = false;
    }
}
