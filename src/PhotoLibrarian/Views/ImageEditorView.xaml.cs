// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using System.Numerics;
using Windows.Foundation;
using Windows.Graphics.DirectX;

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
    private readonly ImageEditorViewModel? _providedViewModel;
    private ImageEditorViewModel? ViewModel => _providedViewModel ?? App.ViewModel?.ImageEditor;
    private bool _suppressSliderEvents;
    private int _draggedHistogramAnchor;
    private readonly EditorViewport _viewport = new();
    private string? _loadedPath;
    private uint? _panPointerId;
    private Vector2 _lastPanPoint;
    private readonly Dictionary<Slider, (double Neutral, double Threshold)> _sliderSnaps = new();
    private Slider? _draggedSlider;
    private readonly RawPreviewSession _rawPreview = new();
    private CancellationTokenSource? _previewRequest;
    private bool _exportingRaw;
    private CanvasBitmap? _retouchedBitmap;
    private List<RetouchStroke> _previewStrokes = [];
    private CancellationTokenSource? _retouchRequest;
    private readonly RetouchGesture _retouchGesture = new();
    private bool _retouchPreviewFailed;
    private bool _pendingRetouch;
    private bool _retouchWorkspace;
    private Vector2? _brushPosition;

    public ImageEditorView()
    {
        _suppressSliderEvents = true;
        this.InitializeComponent();
        _suppressSliderEvents = false;
        RegisterComparisonControls();
        RegisterCenterSnaps();
        RegisterEditGestures();
        this.Loaded += OnLoaded;
        this.Unloaded += OnUnloaded;
        AddZoomAccelerator(Windows.System.VirtualKey.Add, () => ZoomBy(zoomIn: true));
        AddZoomAccelerator((Windows.System.VirtualKey)187, () => ZoomBy(zoomIn: true)); // OEM '=' / '+'
        AddZoomAccelerator(Windows.System.VirtualKey.Subtract, () => ZoomBy(zoomIn: false));
        AddZoomAccelerator((Windows.System.VirtualKey)189, () => ZoomBy(zoomIn: false)); // OEM '-'
        AddZoomAccelerator(Windows.System.VirtualKey.Number0, ZoomFit);
        AddZoomAccelerator(Windows.System.VirtualKey.Number1, ZoomActual);
        AddNavigationAccelerator(Windows.System.VirtualKey.PageUp);
        AddNavigationAccelerator(Windows.System.VirtualKey.PageDown);
        var cancelRetouch = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        cancelRetouch.Invoked += OnCancelRetouchInvoked;
        KeyboardAccelerators.Add(cancelRetouch);
    }

    public ImageEditorView(ImageEditorViewModel viewModel) : this() =>
        _providedViewModel = viewModel;

    public void SetRetouchWorkspace(bool enabled)
    {
        _retouchWorkspace = enabled;
        RetouchToggle.IsChecked = enabled;
        CancelRetouchGesture();
        UpdateHistoryControls();
    }

    private void AddNavigationAccelerator(Windows.System.VirtualKey key)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = Windows.System.VirtualKeyModifiers.Control
        };
        accelerator.Invoked += OnNavigationAcceleratorInvoked;
        KeyboardAccelerators.Add(accelerator);
    }

    private async void OnNavigationAcceleratorInvoked(KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Visibility != Visibility.Visible || ViewModel?.IsOpen != true) return;
        args.Handled = true;
        await NavigateAsync(sender.Key == Windows.System.VirtualKey.PageUp ? -1 : 1);
    }

    public void FocusEditor()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ViewModel?.IsOpen == true && !ViewModel.IsBusy)
                CloseBtn.Focus(FocusState.Programmatic);
        });
    }

    public static int GetArrowNavigationDirection(Windows.System.VirtualKey key,
        Windows.System.VirtualKeyModifiers modifiers, bool isEditingControl)
    {
        if (isEditingControl || modifiers != Windows.System.VirtualKeyModifiers.None) return 0;
        return key switch
        {
            Windows.System.VirtualKey.Left => -1,
            Windows.System.VirtualKey.Right => 1,
            _ => 0
        };
    }

    public async Task HandleEditorKeyAsync(KeyRoutedEventArgs args, DependencyObject? focused)
    {
        if (Visibility != Visibility.Visible || ViewModel?.IsOpen != true) return;
        if (args.Key == Windows.System.VirtualKey.Escape && RetouchToggle.IsChecked == true)
        {
            ExitRetouchMode();
            args.Handled = true;
            return;
        }
        if (args.Handled) return;
        var isEditingControl = false;
        for (var current = focused; current is not null; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
        {
            if (current == CurveCanvas ||
                current is Slider or TextBox or RichEditBox or PasswordBox or AutoSuggestBox or ComboBox or
                    NumberBox or ContentDialog or GridView or MenuFlyoutItemBase or MenuFlyoutPresenter)
            {
                isEditingControl = true;
                break;
            }
        }
        var modifiers = Windows.System.VirtualKeyModifiers.None;
        foreach (var (modifierKey, modifier) in new[]
        {
            (Windows.System.VirtualKey.Control, Windows.System.VirtualKeyModifiers.Control),
            (Windows.System.VirtualKey.Shift, Windows.System.VirtualKeyModifiers.Shift),
            (Windows.System.VirtualKey.Menu, Windows.System.VirtualKeyModifiers.Menu),
            (Windows.System.VirtualKey.LeftWindows, Windows.System.VirtualKeyModifiers.Windows),
            (Windows.System.VirtualKey.RightWindows, Windows.System.VirtualKeyModifiers.Windows)
        })
        {
            if ((InputKeyboardSource.GetKeyStateForCurrentThread(modifierKey) &
                Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
                modifiers |= modifier;
        }
        if (EditorComparison.TryGetShortcut(args.Key, modifiers, isEditingControl, out var mode))
        {
            if (ComparisonButton.IsEnabled)
            {
                CancelRetouchGesture();
                ViewModel?.ToggleComparison(mode);
                args.Handled = true;
            }
            return;
        }
        var direction = GetArrowNavigationDirection(args.Key, modifiers, isEditingControl);
        if (direction == 0) return;
        args.Handled = true;
        await NavigateAsync(direction);
    }

    private void OnCancelRetouchInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Visibility != Visibility.Visible || ViewModel?.IsOpen != true || RetouchToggle.IsChecked != true) return;
        ExitRetouchMode();
        args.Handled = true;
    }

    private void ExitRetouchMode()
    {
        CancelRetouchGesture();
        if (_pendingRetouch) _retouchRequest?.Cancel();
        RetouchToggle.IsChecked = false;
        UpdateBrushCursor();
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
        SyncSlidersFromViewModel();
        SyncCurveControls();
        TitleText.Text = ViewModel.Title;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ClearRetouchPreview();
        ViewModel?.CancelRawExport();
        _previewRequest?.Cancel();
        _rawPreview.Invalidate();
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.ParametersChanged -= OnParametersChanged;
        }
        ReleaseSourceBitmap();
        ClearComparisonSource();
        _loadedPath = null;
        EditCanvas.RemoveFromVisualTree();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImageEditorViewModel.ImagePath))
        {
            var path = ViewModel?.ImagePath;
            _previewRequest?.Cancel();
            _rawPreview.Invalidate();
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ViewModel?.ImagePath == path) _ = LoadImageAsync(path);
            });
            return;
        }
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel is null) return;
            switch (e.PropertyName)
            {
                case nameof(ImageEditorViewModel.CurrentEntry):
                    _ = LoadImageAsync(ViewModel.ImagePath);
                    break;
                case nameof(ImageEditorViewModel.ComparisonMode):
                    CancelRetouchGesture();
                    UpdateHistoryControls();
                    break;
                case nameof(ImageEditorViewModel.IsOpen):
                    Visibility = ViewModel.IsOpen ? Visibility.Visible : Visibility.Collapsed;
                    if (ViewModel.IsOpen) SyncSlidersFromViewModel();
                    break;
                case nameof(ImageEditorViewModel.IsRawPreview):
                    UpdateHistoryControls();
                    break;
                case nameof(ImageEditorViewModel.Title):
                    TitleText.Text = ViewModel.Title;
                    break;
                case nameof(ImageEditorViewModel.HasBackup):
                    UpdateHistoryControls();
                    break;
                case nameof(ImageEditorViewModel.CanUndo):
                case nameof(ImageEditorViewModel.CanRedo):
                case nameof(ImageEditorViewModel.IsSaving):
                case nameof(ImageEditorViewModel.IsBusy):
                case nameof(ImageEditorViewModel.CanNavigate):
                    UpdateHistoryControls();
                    break;
            }
        });
    }

    private void OnParametersChanged(object? sender, EventArgs e)
    {
        CancelRetouchGesture();
        SyncSlidersFromViewModel();
        SyncCurveControls();
        if (ViewModel?.IsRawPreview == true)
        {
            _ = LoadImageAsync(ViewModel.ImagePath);
            return;
        }
        DispatcherQueue.TryEnqueue(() => EditCanvas.Invalidate());
        DispatcherQueue.TryEnqueue(() => HistogramCanvas.Invalidate());
        _ = RefreshRetouchPreviewAsync();
    }

    private void UpdateHistoryControls()
    {
        var isSaving = ViewModel?.IsBusy == true;
        PreviousBtn.IsEnabled = ViewModel?.CanNavigate == true;
        NextBtn.IsEnabled = ViewModel?.CanNavigate == true;
        AdjustmentsPanel.IsEnabled = !isSaving;
        CloseBtn.IsEnabled = !isSaving;
        var isRaw = ViewModel?.IsRawPreview == true;
        var fullWidthRetouch = _retouchWorkspace && !isRaw;
        AdjustmentsColumn.Width = new GridLength(fullWidthRetouch ? 0 : 300);
        AdjustmentsPanel.Visibility = fullWidthRetouch ? Visibility.Collapsed : Visibility.Visible;
        SaveBtn.IsEnabled = ViewModel?.CanSavePixels == true;
        ToolTipService.SetToolTip(SaveBtn, isRaw ? ViewModel!.SaveDisabledReason : "Save edits");
        RawPreviewPanel.Visibility = isRaw ? Visibility.Visible : Visibility.Collapsed;
        RasterAdjustmentsPanel.Visibility = isRaw ? Visibility.Collapsed : Visibility.Visible;
        AutoBtn.IsEnabled = !isRaw && !isSaving;
        ResetBtn.IsEnabled = !isSaving;
        RevertBtn.Visibility = !isRaw && ViewModel?.HasBackup == true ? Visibility.Visible : Visibility.Collapsed;
        KeepRecipeBtn.IsEnabled = isRaw && !isSaving;
        ExportRawBtn.IsEnabled = ViewModel?.CanExportRawDerivative == true;
        CancelRawExportBtn.Visibility = _exportingRaw ? Visibility.Visible : Visibility.Collapsed;
        // Cancellation stays available while all recipe controls are frozen.
        AdjustmentsPanel.IsEnabled = !isSaving || _exportingRaw;
        RawExposureSlider.IsEnabled = !isSaving;
        RawWhitePointSlider.IsEnabled = !isSaving;
        RetouchToggle.IsEnabled = !isRaw && !isSaving && _sourceBitmap is not null &&
            ViewModel?.ImagePath is { } path && ImageEditRenderer.IsSupported(path);
        RetouchToolbar.Visibility = isRaw ? Visibility.Collapsed : Visibility.Visible;
        RetouchRadius.IsEnabled = RetouchToggle.IsEnabled;
        if (_retouchPreviewFailed) SaveBtn.IsEnabled = false;
        UpdateComparisonControls();
        UpdateBrushCursor();
        if (_brushPosition is not null) EditCanvas.Invalidate();
    }

    private void OnCanvasCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        if (args.Reason == CanvasCreateResourcesReason.NewDevice)
            ReleaseComparisonDevice();
        // The editor is opened while collapsed, so the image path can be set before the canvas
        // has a device. Reload here so the preview appears as soon as resources exist.
        args.TrackAsyncAction(LoadImageAsync(ViewModel?.ImagePath).AsAsyncAction());
    }

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_sourceBitmap is null || ViewModel is null || _loadedSourceVersion != ViewModel.SourceVersion) return;

        var p = ViewModel.GetCurrentParameters();
        if (TryDrawComparisonPreview(args.DrawingSession,
            new Vector2((float)sender.Size.Width, (float)sender.Size.Height)))
        {
            if (ComparisonMode == EditorComparisonMode.After)
                DrawRetouchStroke(args.DrawingSession,
                    new Vector2((float)_sourceBitmap.Size.Width, (float)_sourceBitmap.Size.Height));
            UpdateZoomText();
            return;
        }
        if (ViewModel.IsRawPreview) p = new EditParameters();
        // Frame the *output* extent — rotation makes it larger than the source — so the preview
        // shows exactly what a save would write.
        var imageSize = _sourceBitmap.Size;
        var (originOffset, outputWidth, outputHeight) =
            EditEffectGraph.ComputeOutputExtent(imageSize.Width, imageSize.Height, p.RotationAngle);

        _viewport.SetCanvasSize(new Vector2((float)sender.Size.Width, (float)sender.Size.Height));
        _viewport.SetContentSize(new Vector2(outputWidth, outputHeight), resetView: false);
        var origin = _viewport.ContentOrigin;

        var transform =
            Matrix3x2.CreateTranslation(originOffset) *
            Matrix3x2.CreateScale(_viewport.Scale) *
            Matrix3x2.CreateTranslation(origin);

        if (ViewModel.IsRawPreview)
        {
            // The SDR transfer has already been applied. Do not run the byte raster effects again.
            ImageEditRenderPass.DrawRawPreview(args.DrawingSession, _sourceBitmap, transform);
            return;
        }
        ImageEditRenderPass.Draw(
            args.DrawingSession,
            _retouchedBitmap ?? _sourceBitmap,
            p,
            new Vector2((float)imageSize.Width, (float)imageSize.Height),
            transform);
        DrawRetouchStroke(args.DrawingSession, new Vector2((float)imageSize.Width, (float)imageSize.Height));
    }

    private void OnEditCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        CancelRetouchGesture();
        _viewport.SetCanvasSize(PreviewSize);
        UpdateComparisonDivider();
        UpdateBrushCursor();
        UpdateZoomText();
    }

    private void OnEditCanvasPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_retouchGesture.PointerId is not null) { e.Handled = true; return; }
        var point = e.GetCurrentPoint(EditCanvas);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0 || _sourceBitmap is null) return;
        var anchor = new Vector2((float)point.Position.X, (float)point.Position.Y);
        if (ComparisonMode == EditorComparisonMode.SideBySide && anchor.X >= PreviewSize.X)
            anchor.X -= PreviewSize.X;
        UpdateActualSizeScale();
        if (delta > 0) _viewport.ZoomIn(anchor);
        else _viewport.ZoomOut(anchor);
        OnViewportChanged();
        e.Handled = true;
    }

    private void OnEditCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_retouchGesture.PointerId is not null) { e.Handled = true; return; }
        var point = e.GetCurrentPoint(EditCanvas);
        if (RetouchToggle.IsChecked == true && point.Properties.IsLeftButtonPressed)
        {
            if (ViewModel?.IsBusy == true || !RetouchToggle.IsEnabled || !TryRetouchPoint(point.Position, out var sourcePoint))
                return;
            FocusEditor();
            if (!EditCanvas.CapturePointer(e.Pointer)) return;
            _retouchGesture.Begin(e.Pointer.PointerId, (int)_sourceBitmap!.SizeInPixels.Width,
                (int)_sourceBitmap.SizeInPixels.Height, (int)RetouchRadius.Value, sourcePoint);
            RetouchStatus.IsOpen = false;
            UpdateBrushPosition(point.Position);
            EditCanvas.Invalidate();
            e.Handled = true;
            return;
        }
        if (RetouchToggle.IsChecked == true && !point.Properties.IsMiddleButtonPressed) return;
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
        UpdateBrushPosition(e.GetCurrentPoint(EditCanvas).Position);
        if (_retouchGesture.PointerId == e.Pointer.PointerId)
        {
            AddRetouchPoint(e.GetCurrentPoint(EditCanvas).Position);
            EditCanvas.Invalidate();
            e.Handled = true;
            return;
        }
        if (_panPointerId != e.Pointer.PointerId) return;
        var position = e.GetCurrentPoint(EditCanvas).Position;
        var current = new Vector2((float)position.X, (float)position.Y);
        _viewport.PanBy(current - _lastPanPoint);
        _lastPanPoint = current;
        EditCanvas.Invalidate();
        e.Handled = true;
    }

    private async void OnEditCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_retouchGesture.PointerId == e.Pointer.PointerId)
        {
            AddRetouchPoint(e.GetCurrentPoint(EditCanvas).Position);
            var stroke = _retouchGesture.Complete(e.Pointer.PointerId);
            CancelRetouchGesture();
            if (stroke is null) return;
            await RefreshRetouchPreviewAsync(stroke);
            e.Handled = true;
            return;
        }
        if (_panPointerId != e.Pointer.PointerId) return;
        EditCanvas.ReleasePointerCapture(e.Pointer);
        EndPan();
    }

    private void OnEditCanvasPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_retouchGesture.PointerId == e.Pointer.PointerId) CancelRetouchGesture();
        if (_panPointerId == e.Pointer.PointerId) EndPan();
    }

    private void OnEditCanvasPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        CancelRetouchGesture();
        EditCanvas.ReleasePointerCapture(e.Pointer);
        EndPan();
    }

    private void OnRetouchToggled(object sender, RoutedEventArgs e)
    {
        CancelRetouchGesture();
        UpdateBrushCursor();
    }

    private void OnRetouchRadiusChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (RetouchRadiusText is null) return;
        RetouchRadiusText.Text = $"{e.NewValue:0} px";
        EditCanvas?.Invalidate();
    }

    private void OnEditCanvasPointerEntered(object sender, PointerRoutedEventArgs e) =>
        UpdateBrushPosition(e.GetCurrentPoint(EditCanvas).Position);

    private void OnEditCanvasPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _brushPosition = null;
        UpdateBrushCursor();
        EditCanvas.Invalidate();
    }

    private void UpdateBrushPosition(Point position)
    {
        _brushPosition = new Vector2((float)position.X, (float)position.Y);
        UpdateBrushCursor();
        if (RetouchToggle.IsChecked == true) EditCanvas.Invalidate();
    }

    private void UpdateBrushCursor()
    {
        if (_panPointerId is not null) return;
        // The canvas draws a DPI/zoom-correct brush outline; hide the unrelated arrow while over the photo.
        ProtectedCursor = RetouchToggle.IsChecked == true && ViewModel?.IsBusy == false &&
            _brushPosition is { } position &&
            TryRetouchPoint(new Point(position.X, position.Y), out _)
            ? null : InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    private bool TryRetouchPoint(Point position, out RetouchPoint point)
    {
        point = default;
        if (_sourceBitmap is null || ViewModel is null || ComparisonMode != EditorComparisonMode.After) return false;
        return EditorImageCoordinates.TryGetSourcePoint(new Vector2((float)position.X, (float)position.Y),
            new Vector2((float)_sourceBitmap.Size.Width, (float)_sourceBitmap.Size.Height),
            ViewModel.RotationAngle, _viewport.Scale, _viewport.ContentOrigin, out point);
    }

    private void AddRetouchPoint(Point position)
    {
        if (!TryRetouchPoint(position, out var point))
        {
            CancelRetouchGesture();
            ShowRetouchError("Stroke cancelled outside the image. Keep the entire stroke inside the photo.");
            return;
        }
        if (!_retouchGesture.Add(point))
        {
            CancelRetouchGesture();
            ShowRetouchError("Stroke cancelled outside the image. Keep the entire stroke inside the photo.");
        }
    }

    private void CancelRetouchGesture()
    {
        _retouchGesture.Cancel();
        EditCanvas.ReleasePointerCaptures();
        UpdateBrushCursor();
        EditCanvas.Invalidate();
    }

    private void ShowRetouchError(string message)
    {
        RetouchStatus.Message = message;
        RetouchStatus.IsOpen = true;
        SetStatus(message);
    }

    private void ClearRetouchPreview()
    {
        _brushPosition = null;
        CancelRetouchGesture();
        _retouchRequest?.Cancel();
        _retouchRequest = null;
        _pendingRetouch = false;
        _retouchedBitmap?.Dispose();
        _retouchedBitmap = null;
        _previewStrokes = [];
        _retouchPreviewFailed = false;
        if (ViewModel is { } vm) vm.IsRetouching = false;
    }

    private async Task RefreshRetouchPreviewAsync(RetouchStroke? pending = null)
    {
        var vm = ViewModel;
        var source = _sourceBitmap;
        if (vm is null || source is null || vm.IsRawPreview) return;
        var path = vm.ImagePath;
        var strokes = vm.GetCurrentParameters().RetouchStrokes;
        if (pending is not null) strokes.Add(pending);
        if (RetouchStroke.SequenceEqual(strokes, _previewStrokes))
        {
            if (_retouchPreviewFailed) RetouchStatus.IsOpen = false;
            _retouchPreviewFailed = false;
            UpdateHistoryControls();
            return;
        }
        _retouchRequest?.Cancel();
        using var request = new CancellationTokenSource();
        _retouchRequest = request;
        _pendingRetouch = pending is not null;
        vm.IsRetouching = true;
        try
        {
            // New strokes build on the cached full-resolution result. Undo/reload replays the recipe.
            var incremental = pending is not null && _retouchedBitmap is not null &&
                RetouchStroke.SequenceEqual(strokes.Take(strokes.Count - 1).ToList(), _previewStrokes);
            var preview = await ImageEditRenderer.TryCreateRetouchedAsync(EditCanvas,
                incremental ? _retouchedBitmap! : source,
                incremental ? [pending!] : strokes, request.Token);
            var bitmap = preview.Bitmap;
            if (_retouchRequest != request || _sourceBitmap != source || vm.ImagePath != path)
            {
                bitmap?.Dispose();
                return;
            }
            if (!preview.Result.Succeeded)
            {
                _retouchPreviewFailed = pending is null;
                ShowRetouchError(preview.Result.Message! +
                    (_retouchPreviewFailed ? " Undo or reset the recipe before saving." : ""));
                return;
            }
            _retouchedBitmap?.Dispose();
            _retouchedBitmap = bitmap;
            _sourcePixels = (bitmap ?? source).GetPixelBytes();
            _previewStrokes = strokes;
            _retouchPreviewFailed = false;
            RetouchStatus.IsOpen = false;
            vm.IsRetouching = false;
            if (pending is not null) vm.AddRetouchStroke(pending);
            EditCanvas.Invalidate();
            HistogramCanvas.Invalidate();
            CurveCanvas.Invalidate();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_retouchRequest != request) return;
            System.Diagnostics.Debug.WriteLine($"[RETOUCH PREVIEW] {ex}");
            _retouchPreviewFailed = pending is null;
            ShowRetouchError($"Retouch not applied: {ex.Message}" +
                (_retouchPreviewFailed ? " Undo or reset the recipe before saving." : ""));
        }
        finally
        {
            if (_retouchRequest == request)
            {
                _retouchRequest = null;
                _pendingRetouch = false;
                vm.IsRetouching = false;
                UpdateHistoryControls();
            }
        }
    }

    private void DrawRetouchStroke(CanvasDrawingSession session, Vector2 sourceSize)
    {
        if (ViewModel is null) return;
        if (RetouchToggle.IsChecked == true && ViewModel.IsBusy == false && _brushPosition is { } position &&
            TryRetouchPoint(new Point(position.X, position.Y), out _))
        {
            session.Transform = Matrix3x2.Identity;
            var radius = (float)RetouchRadius.Value * _viewport.Scale;
            // A contrasting double outline stays legible on both light and dark photo content.
            session.DrawCircle(position, radius, Microsoft.UI.Colors.Black, 3);
            session.DrawCircle(position, radius, Microsoft.UI.Colors.White, 1);
        }
        if (_retouchGesture.Points.Count == 0) return;
        session.Transform = EditorImageCoordinates.SourceToCanvas(sourceSize, ViewModel.RotationAngle,
            _viewport.Scale, _viewport.ContentOrigin);
        using var selection = RetouchStrokeGeometry.Create(session, _retouchGesture.Points, _retouchGesture.Radius);
        session.FillGeometry(selection, Windows.UI.Color.FromArgb(72, 255, 255, 255));
    }

    private void EndPan()
    {
        _panPointerId = null;
        UpdateBrushCursor();
    }

    private void OnEditCanvasDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RetouchToggle.IsChecked == true) { e.Handled = true; return; }
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
        CancelRetouchGesture();
        if (_sourceBitmap is null) return;
        UpdateActualSizeScale();
        if (zoomIn) _viewport.ZoomIn();
        else _viewport.ZoomOut();
        OnViewportChanged();
    }

    private void ZoomFit()
    {
        CancelRetouchGesture();
        _viewport.Fit();
        OnViewportChanged();
    }

    private void ZoomActual()
    {
        CancelRetouchGesture();
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
        UpdateBrushCursor();
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
        ClearRetouchPreview();
        RetouchToggle.IsChecked = _retouchWorkspace && ComparisonMode == EditorComparisonMode.After;
        _brushPosition = null;
        RetouchStatus.IsOpen = false;
        _previewRequest?.Cancel();
        using var request = new CancellationTokenSource();
        _previewRequest = request;
        var token = request.Token;
        var parameters = ViewModel?.GetCurrentParameters() ?? new EditParameters();
        var isRaw = path is not null && RawToneMappingService.IsRaw(path);
        var differentPhoto = !string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase) ||
            _loadedSourceVersion != ViewModel?.SourceVersion;
        if (differentPhoto || path is null)
        {
            _rawPreview.Invalidate();
            ReleaseSourceBitmap();
            ClearComparisonSource();
            _loadedPath = path;
            _loadedSourceVersion = ViewModel?.SourceVersion ?? -1;
            _draggedSlider = null;
            _viewport.Fit();
            UpdateZoomText();
            EditCanvas.Invalidate();
            HistogramCanvas.Invalidate();
            CurveCanvas.Invalidate();
        }
        try
        {
            if (path is null) return;
            CanvasBitmap bitmap;
            byte[] pixels;
            if (isRaw)
            {
                RawPreviewStatus.Text = "Developing full-resolution RAW preview...";
                await Task.Delay(80, token);
                var result = await _rawPreview.RenderAsync(path, parameters, token);
                pixels = await Task.Run(() => result.ToPreviewBgra8(token), token);
                token.ThrowIfCancellationRequested();
                bitmap = CanvasBitmap.CreateFromBytes(EditCanvas, pixels, result.Width, result.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
                RawPreviewStatus.Text = $"{result.Width} x {result.Height}; sRGB SDR reference. Camera WB; global luminance shoulder.";
            }
            else
            {
                bitmap = await ImageEditRenderer.LoadOrientedAsync(EditCanvas, path);
                if (token.IsCancellationRequested)
                {
                    bitmap.Dispose();
                    token.ThrowIfCancellationRequested();
                }
                pixels = bitmap.GetPixelBytes();
            }
            ReleaseSourceBitmap();
            _sourceBitmap = bitmap;
            if (!isRaw) KeepComparisonSource(bitmap, pixels);
            _sourcePixels = isRaw ? null : pixels;
            var size = _sourceBitmap.Size;
            var (_, outputWidth, outputHeight) = EditEffectGraph.ComputeOutputExtent(
                size.Width, size.Height, isRaw ? 0 : ViewModel?.RotationAngle ?? 0);
            _viewport.SetContentSize(new Vector2(outputWidth, outputHeight), resetView: false);
            UpdateZoomText();
            EditCanvas.Invalidate();
            HistogramCanvas.Invalidate();
            CurveCanvas.Invalidate();
            await RefreshRetouchPreviewAsync();
            UpdateHistoryControls();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            ReleaseSourceBitmap();
            RawPreviewStatus.Text = $"Preview failed: {ex.Message}";
            ShowRetouchError($"Preview failed: {ex.Message}");
            UpdateHistoryControls();
            EditCanvas.Invalidate();
        }
        finally
        {
            if (_previewRequest == request) _previewRequest = null;
        }
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
            ViewModel.BeginEdit("Levels");
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
        _draggedHistogramAnchor = 0;
        HistogramCanvas.ReleasePointerCapture(pointer);
        ViewModel?.EndEdit();
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
    }

    private void RegisterEditGestures()
    {
        Slider[] sliders =
        [
            ExposureSlider, BrightnessSlider, ContrastSlider, HighlightsSlider, ShadowsSlider,
            SaturationSlider, TemperatureSlider, TintSlider, ClaritySlider, SharpnessSlider,
            NoiseReductionSlider, UnsharpMaskAmountSlider, UnsharpMaskRadiusSlider,
            UnsharpMaskThresholdSlider, BlackPointSlider, WhitePointSlider, MidtonesSlider,
            RotationSlider, RawExposureSlider, RawWhitePointSlider
        ];
        foreach (var slider in sliders)
        {
            // Slider marks pointer events handled internally, so listen for handled events too.
            slider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) =>
            {
                _draggedSlider = slider;
                ViewModel?.BeginEdit(slider.Name.Replace("Slider", "") switch
                {
                    "BlackPoint" => "Black point",
                    "WhitePoint" => "White point",
                    "NoiseReduction" => "Noise reduction",
                    "UnsharpMaskAmount" => "Unsharp-mask amount",
                    "UnsharpMaskRadius" => "Unsharp-mask radius",
                    "UnsharpMaskThreshold" => "Unsharp-mask threshold",
                    var label => label
                });
            }), true);
            slider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndSliderDrag(slider)), true);
            slider.AddHandler(PointerCanceledEvent, new PointerEventHandler((_, _) => EndSliderDrag(slider)), true);
            slider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndSliderDrag(slider)), true);
        }
    }

    private void EndSliderDrag(Slider slider)
    {
        if (_draggedSlider != slider) return;
        _draggedSlider = null;
        ViewModel?.EndEdit();
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
        ViewModel.NoiseReduction = NoiseReductionSlider.Value;
        ViewModel.UnsharpMaskAmount = UnsharpMaskAmountSlider.Value;
        ViewModel.UnsharpMaskRadius = UnsharpMaskRadiusSlider.Value;
        ViewModel.UnsharpMaskThreshold = UnsharpMaskThresholdSlider.Value;
        ViewModel.BlackPoint = BlackPointSlider.Value;
        ViewModel.WhitePoint = WhitePointSlider.Value;
        ViewModel.Midtones = MidtonesSlider.Value;
        ViewModel.RotationAngle = RotationSlider.Value;
        HistogramCanvas.Invalidate();
    }

    private void OnRawSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSliderEvents || ViewModel?.IsRawPreview != true) return;
        if (sender == RawExposureSlider) ViewModel.RawExposureEv = e.NewValue;
        else if (sender == RawWhitePointSlider) ViewModel.RawWhitePoint = e.NewValue;
    }

    private async void OnKeepRawRecipe(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        try
        {
            if (await ViewModel.KeepRawPreviewRecipeAsync())
                SetStatus("RAW preview recipe kept in XMP sidecar; original unchanged.");
        }
        catch (Exception ex) { SetStatus($"Unable to keep RAW recipe: {ex.Message}"); }
    }

    private void OnAdvancedSharpeningChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSliderEvents || ViewModel is null) return;

        var mode = AdvancedSharpeningCheckBox.IsChecked == true
            ? SharpeningMode.UnsharpMask
            : SharpeningMode.Legacy;
        if (ViewModel.SharpeningMode == mode) return;

        ViewModel.BeginEdit("Advanced sharpening");
        ViewModel.SharpeningMode = mode;
        ViewModel.EndEdit();
        UpdateSharpeningControls();
    }

    private void UpdateSharpeningControls()
    {
        var useUnsharpMask = ViewModel?.SharpeningMode == SharpeningMode.UnsharpMask;
        BasicSharpeningPanel.Visibility = useUnsharpMask ? Visibility.Collapsed : Visibility.Visible;
        UnsharpMaskPanel.Visibility = useUnsharpMask ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAutoEnhance(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && !ViewModel.IsRawPreview && _sourcePixels is not null)
            ViewModel.ApplyAutoEnhance(AutoEnhanceAnalyzer.Analyze(_sourcePixels));
        SyncSlidersFromViewModel();
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        CancelRetouchGesture();
        ViewModel?.ResetAllCommand.Execute(null);
        SyncSlidersFromViewModel();
    }

    private async void OnRevert(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.RevertToOriginalCommand.CanExecute(null) == true)
        {
            // Let go of the file before it is overwritten by the backup copy.
            ReleaseSourceBitmap();

            try
            {
                await ViewModel.RevertToOriginalCommand.ExecuteAsync(null);
                SyncSlidersFromViewModel();
            }
            catch (Exception ex)
            {
                SetStatus($"Revert failed: {ex.Message}");
            }
            finally
            {
                await LoadImageAsync(ViewModel.ImagePath);
            }
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        CancelRetouchGesture();
        if (ViewModel is null || ViewModel.IsBusy) return;
        if (ViewModel.IsRawPreview)
        {
            SetStatus(ViewModel.SaveDisabledReason);
            return;
        }

        if (!ViewModel.HasChanges)
        {
            SetStatus("No adjustments to save");
            return;
        }

        SaveBtn.IsEnabled = false;
        SetStatus("Saving adjustments…");
        try
        {
            var saved = await ViewModel.SaveAsync(RenderForSaveAsync);
            if (!saved) return;
            SyncSlidersFromViewModel();

            // Reload either the baked pixels or the source behind a saved recipe.
            await LoadImageAsync(ViewModel.ImagePath);

        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}");
            await LoadImageAsync(ViewModel.ImagePath);
        }
        finally
        {
            UpdateHistoryControls();
        }
    }

    private static void SetStatus(string text)
    {
        if (App.ViewModel is not null) App.ViewModel.StatusText = text;
    }

    private Task<(uint Width, uint Height)> RenderForSaveAsync(string path, EditParameters parameters)
    {
        ClearRetouchPreview();
        // Cancel old loads and release our handle before the renderer rewrites the file.
        _previewRequest?.Cancel();
        ReleaseSourceBitmap();
        return ImageEditRenderer.RenderToFileAsync(path, parameters, EditCanvas.Device);
    }

    private async Task<PendingEditDecision> ConfirmPendingEditsAsync()
    {
        var isRaw = ViewModel?.IsRawPreview == true;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = isRaw ? "Keep RAW preview changes?" : "Save changes?",
            Content = isRaw
                ? "Keep the preview recipe in the photo's XMP sidecar before leaving? The RAW original will not be changed."
                : "Save your adjustments before leaving this photo?",
            PrimaryButtonText = isRaw ? "Keep recipe" : "Save",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        return (await dialog.ShowAsync()) switch
        {
            ContentDialogResult.Primary => PendingEditDecision.Save,
            ContentDialogResult.Secondary => PendingEditDecision.Discard,
            _ => PendingEditDecision.Cancel
        };
    }

    private async void OnPrevious(object sender, RoutedEventArgs e) => await NavigateAsync(-1);
    private async void OnNext(object sender, RoutedEventArgs e) => await NavigateAsync(1);

    private async Task NavigateAsync(int direction)
    {
        CancelRetouchGesture();
        var vm = ViewModel;
        if (vm?.CanNavigate != true) return;
        var path = vm.ImagePath;
        try
        {
            await vm.NavigateAsync(direction, ConfirmPendingEditsAsync, RenderForSaveAsync);
        }
        catch (Exception ex)
        {
            SetStatus($"Unable to move to another photo: {ex.Message}");
        }
        finally
        {
            if (vm.ImagePath == path && _sourceBitmap is null)
                await LoadImageAsync(path);
            SyncSlidersFromViewModel();
            if (vm.IsOpen && vm.CanNavigate)
                (direction > 0 ? NextBtn : PreviousBtn).Focus(FocusState.Programmatic);
            else if (vm.IsOpen)
                FocusEditor();
        }
    }

    private async void OnClose(object sender, RoutedEventArgs e)
    {
        CancelRetouchGesture();
        var vm = ViewModel;
        if (vm is null || vm.IsBusy) return;
        try
        {
            await vm.TryCloseAsync(ConfirmPendingEditsAsync, RenderForSaveAsync);
        }
        catch (Exception ex)
        {
            SetStatus($"Unable to close editor: {ex.Message}");
        }
        finally
        {
            if (vm.IsOpen && _sourceBitmap is null) await LoadImageAsync(vm.ImagePath);
            UpdateHistoryControls();
            if (vm.IsOpen) CloseBtn.Focus(FocusState.Programmatic);
        }
    }

    private void OnCancelRawExport(object sender, RoutedEventArgs e)
    {
        ViewModel?.CancelRawExport();
        RawPreviewStatus.Text = "Cancelling export; private staging files will be removed.";
    }

    private async void OnExportRaw(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm?.CanExportRawDerivative != true || vm.ImagePath is null) return;
        var sourcePath = vm.ImagePath;
        var dialog = new RawDerivativeExportDialog(System.IO.Path.GetDirectoryName(sourcePath)!)
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme
        };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (vm.ImagePath != sourcePath || !vm.CanExportRawDerivative) return;
            _previewRequest?.Cancel();
            _exportingRaw = true;
            RawPreviewStatus.Text = "Exporting full-resolution SDR development and keeping the source recipe...";
            UpdateHistoryControls();
            var result = await vm.ExportRawDerivativeAsync(dialog.OutputFolder, dialog.Format, _rawPreview.RenderAsync);
            RawPreviewStatus.Text = $"Exported {result.FilePath} ({result.Width} x {result.Height}). RAW unchanged; recipe kept.";
            SetStatus(RawPreviewStatus.Text);
            if (App.ViewModel is { } main)
            {
                try
                {
                    await main.IndexRawDerivativeAsync(result.FilePath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RAW EXPORT INDEX] {ex}");
                    RawPreviewStatus.Text = $"Export saved at {result.FilePath}, but library refresh failed: {ex.Message}. Refresh the destination folder.";
                    SetStatus(RawPreviewStatus.Text);
                }
            }
        }
        catch (OperationCanceledException)
        {
            RawPreviewStatus.Text = "Export cancelled. No derivative was published; RAW original unchanged.";
            SetStatus(RawPreviewStatus.Text);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RAW EXPORT] {ex}");
            RawPreviewStatus.Text = $"Export failed: {ex.Message}. RAW unchanged. Check folder permissions/free space and the source XMP, then retry.";
            SetStatus(RawPreviewStatus.Text);
        }
        finally
        {
            _exportingRaw = false;
            UpdateHistoryControls();
        }
    }

    private void SyncSlidersFromViewModel()
    {
        if (ViewModel is null) return;
        _suppressSliderEvents = true;
        RawExposureSlider.Value = ViewModel.RawExposureEv;
        RawWhitePointSlider.Value = ViewModel.RawWhitePoint;
        RawExposureText.Text = $"{ViewModel.RawExposureEv:+0.0;-0.0;0.0} EV";
        RawWhitePointText.Text = ViewModel.RawWhitePoint.ToString("0.0");
        UpdateHistoryControls();
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
        NoiseReductionSlider.Value = ViewModel.NoiseReduction;
        AdvancedSharpeningCheckBox.IsChecked =
            ViewModel.SharpeningMode == SharpeningMode.UnsharpMask;
        UnsharpMaskAmountSlider.Value = ViewModel.UnsharpMaskAmount;
        UnsharpMaskAmountText.Text = ViewModel.UnsharpMaskAmount.ToString("0.00");
        UnsharpMaskRadiusSlider.Value = ViewModel.UnsharpMaskRadius;
        UnsharpMaskRadiusText.Text = $"{ViewModel.UnsharpMaskRadius:0.0} px";
        UnsharpMaskThresholdSlider.Value = ViewModel.UnsharpMaskThreshold;
        UnsharpMaskThresholdText.Text = ViewModel.UnsharpMaskThreshold.ToString("0.000");
        UpdateSharpeningControls();
        BlackPointSlider.Value = ViewModel.BlackPoint;
        WhitePointSlider.Value = ViewModel.WhitePoint;
        MidtonesSlider.Value = ViewModel.Midtones;
        RotationSlider.Value = ViewModel.RotationAngle;
        HistogramCanvas.Invalidate();
        _suppressSliderEvents = false;
    }
}
