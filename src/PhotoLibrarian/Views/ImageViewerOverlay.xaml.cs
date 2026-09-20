using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Diagnostics;
using PhotoLibrarian.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;

namespace PhotoLibrarian.Views;

public sealed partial class ImageViewerOverlay : UserControl
{
    private ImageViewerViewModel? ViewModel => App.ViewModel?.ImageViewer;
    private ImageZoomPanController? _zoomPan;
    private uint _currentImagePixelWidth;
    private uint _currentImagePixelHeight;
    private bool _isStraightenGuideDragging;
    private Point _straightenGuideStart;
    private double _straightenAngleAtGuideStart;
    private bool _isManualFaceTagging;
    private bool _pendingManualFaceTagging;
    private bool _isDrawingManualFace;
    private Point _manualFaceStart;
    public bool IsCropping { get; private set; }
    public bool IsStraightening { get; private set; }
    public event EventHandler? CropExited;
    public event EventHandler? CropApplyRequested;
    public event EventHandler? CropCancelRequested;
    public event EventHandler? StraightenApplyRequested;
    public event EventHandler? StraightenCancelRequested;

    public CropOverlay CropOverlay => CropOverlayView;
    public uint CurrentImagePixelWidth => _currentImagePixelWidth;
    public uint CurrentImagePixelHeight => _currentImagePixelHeight;
    public double StraightenAngle => StraightenAngleSlider.Value;

    // Gap left around the image while cropping so the handles — which overhang the crop rect
    // by half their hit size — are never clipped by the viewport edge.
    private const double CropInset = 20;

    public void EnterCropMode()
    {
        if (_currentImagePixelWidth == 0 || _currentImagePixelHeight == 0) return;
        if (IsStraightening) ExitStraightenMode();

        IsCropping = true;

        // The prev/next buttons sit exactly where the E/W handles land and would swallow
        // the pointer presses meant for them, so they go away for the duration of the crop.
        PreviousButton.Visibility = Visibility.Collapsed;
        NextButton.Visibility = Visibility.Collapsed;

        // Re-fit with an inset: a zoomed-in image would push the crop rect's edges — and the
        // handles that straddle them — outside the viewport.
        _zoomPan?.ApplyBestFit(CropInset);

        CropOverlayView.Visibility = Visibility.Visible;

        // The overlay has no layout size until it has been measured, so initialise after the
        // pending layout pass; CropOverlay also defers its own reset until it has a real size.
        CropOverlayView.InitializeForImage(_currentImagePixelWidth, _currentImagePixelHeight);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (IsCropping && !CropOverlayView.IsCropEstablished)
                CropOverlayView.InitializeForImage(_currentImagePixelWidth, _currentImagePixelHeight);
        });
    }

    public void ExitCropMode()
    {
        var wasCropping = IsCropping;

        CropOverlayView.Visibility = Visibility.Collapsed;
        IsCropping = false;

        PreviousButton.Visibility = Visibility.Visible;
        NextButton.Visibility = Visibility.Visible;

        // Drop the crop inset so the image goes back to filling the viewport.
        if (wasCropping) _zoomPan?.ApplyBestFit();

        CropExited?.Invoke(this, EventArgs.Empty);
    }

    public void EnterStraightenMode()
    {
        if (_currentImagePixelWidth == 0 || _currentImagePixelHeight == 0) return;
        if (IsCropping) ExitCropMode();

        IsStraightening = true;
        StraightenAngleSlider.Value = 0;
        AutoStraightenStatus.Text = "Auto detects a dominant horizon or vertical line.";
        StraightenGrid.Visibility = Visibility.Visible;
        StraightenGuideOverlay.Visibility = Visibility.Visible;
        StraightenPanel.Visibility = Visibility.Visible;
        PreviousButton.Visibility = Visibility.Collapsed;
        NextButton.Visibility = Visibility.Collapsed;
        SetZoomControlsEnabled(false);

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!IsStraightening) return;
            _zoomPan?.ApplyBestFit();
            UpdateStraightenClip();
            UpdateStraightenPreview(StraightenAngleSlider.Value);
            _ = RunAutoStraightenAsync();
        });
    }

    public void EnterManualFaceTagging()
    {
        if (ViewModel?.CurrentEntry?.MediaType != MediaType.Image) return;
        if (ViewModel.CurrentImage is null ||
            _currentImagePixelWidth == 0 ||
            _currentImagePixelHeight == 0)
        {
            _pendingManualFaceTagging = true;
            return;
        }

        if (IsCropping) ExitCropMode();
        if (IsStraightening) ExitStraightenMode();

        _pendingManualFaceTagging = false;
        _isManualFaceTagging = true;
        ManualFaceOverlay.Visibility = Visibility.Visible;
        ManualFaceTagHelpText.Visibility = Visibility.Visible;
        CancelManualFaceTaggingButton.Visibility = Visibility.Visible;
        PreviousButton.Visibility = Visibility.Collapsed;
        NextButton.Visibility = Visibility.Collapsed;
        SetZoomControlsEnabled(false);
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Cross);
        _zoomPan?.ApplyBestFit();
    }

    /// <summary>Raised whenever manual face-tagging mode ends, however it was triggered
    /// (Save, Cancel, Escape) so other UI (the panel's toggle button) can stay in sync.</summary>
    public event EventHandler? ManualFaceTaggingExited;

    public void ExitManualFaceTagging()
    {
        var wasActive = _isManualFaceTagging || _pendingManualFaceTagging;
        _pendingManualFaceTagging = false;
        _isManualFaceTagging = false;
        _isDrawingManualFace = false;
        ManualFaceOverlay.Visibility = Visibility.Collapsed;
        ManualFaceRectangle.Visibility = Visibility.Collapsed;
        ManualFaceTagHelpText.Visibility = Visibility.Collapsed;
        CancelManualFaceTaggingButton.Visibility = Visibility.Collapsed;
        PreviousButton.Visibility = Visibility.Visible;
        NextButton.Visibility = Visibility.Visible;
        SetZoomControlsEnabled(true);
        ProtectedCursor = null;
        ManualFaceTagFlyout.Hide();
        if (wasActive) ManualFaceTaggingExited?.Invoke(this, EventArgs.Empty);
    }

    public void ExitStraightenMode()
    {
        var wasStraightening = IsStraightening;
        IsStraightening = false;
        StraightenGrid.Visibility = Visibility.Collapsed;
        StraightenGuideOverlay.Visibility = Visibility.Collapsed;
        StraightenGuideLine.Visibility = Visibility.Collapsed;
        _isStraightenGuideDragging = false;
        StraightenPanel.Visibility = Visibility.Collapsed;
        StraightenTransform.Rotation = 0;
        StraightenTransform.ScaleX = 1;
        StraightenTransform.ScaleY = 1;
        ImageHost.Clip = null;
        PreviousButton.Visibility = Visibility.Visible;
        NextButton.Visibility = Visibility.Visible;
        SetZoomControlsEnabled(true);
        AutoStraightenButton.IsEnabled = true;

        if (wasStraightening)
            _zoomPan?.ApplyBestFit();
    }

    public ImageViewerOverlay()
    {
        this.InitializeComponent();
        this.Loaded += OnLoaded;
        ImageHost.SizeChanged += (_, _) =>
        {
            if (IsStraightening)
                UpdateStraightenClip();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DebugLog.WriteLine("ImageViewerOverlay: OnLoaded called");
        
        if (ViewModel is null)
        {
            DebugLog.WriteLine("ImageViewerOverlay: ViewModel is null");
            return;
        }
        
        _zoomPan = new ImageZoomPanController(ImageScrollViewer, ScrollContent, ImageHost, FullImage);
        DebugLog.WriteLine("ImageViewerOverlay: ZoomPanController created");
        
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        
        // Wire up mouse wheel to root grid for capture (handles all wheel events including over ScrollViewer)
        RootGrid.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPointerWheelChanged), true);
        DebugLog.WriteLine("ImageViewerOverlay: Wheel handler attached to RootGrid");
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel is null) return;

            switch (e.PropertyName)
            {
                case nameof(ImageViewerViewModel.IsOpen):
                    Visibility = ViewModel.IsOpen ? Visibility.Visible : Visibility.Collapsed;
                    if (ViewModel.IsOpen) Focus(FocusState.Programmatic);
                    if (!ViewModel.IsOpen) StopVideo();
                    break;
                case nameof(ImageViewerViewModel.CurrentImage):
                    // Cancel any in-progress edit mode when navigating to a different image.
                    if (IsCropping) ExitCropMode();
                    if (IsStraightening) ExitStraightenMode();
                    if (_isManualFaceTagging) ExitManualFaceTagging();
                    // Attach handler BEFORE setting source (in case image is cached and fires immediately)
                    FullImage.ImageOpened += OnImageOpened;
                    FullImage.Source = ViewModel.CurrentImage;
                    
                    // If image is already loaded (cached), manually trigger setup
                    if (ViewModel.CurrentImage is BitmapImage bmp && bmp.PixelWidth > 0)
                    {
                        DebugLog.WriteLine($"ImageViewerOverlay: Image already loaded, setting up manually");
                        _currentImagePixelWidth = (uint)bmp.PixelWidth;
                        _currentImagePixelHeight = (uint)bmp.PixelHeight;
                        _zoomPan?.SetImageSize(bmp.PixelWidth, bmp.PixelHeight);
                        _zoomPan?.ApplyBestFit();
                        if (_pendingManualFaceTagging) EnterManualFaceTagging();
                    }
                    break;
                case nameof(ImageViewerViewModel.IsVideo):
                    ImageScrollViewer.Visibility = ViewModel.IsVideo ? Visibility.Collapsed : Visibility.Visible;
                    VideoPlayer.Visibility = ViewModel.IsVideo ? Visibility.Visible : Visibility.Collapsed;
                    if (!ViewModel.IsVideo) StopVideo();
                    break;
                case nameof(ImageViewerViewModel.VideoPath):
                    if (ViewModel.VideoPath is not null)
                        PlayVideo(ViewModel.VideoPath);
                    break;
                case nameof(ImageViewerViewModel.Title):
                    TitleText.Text = ViewModel.Title;
                    break;
                case nameof(ImageViewerViewModel.ImageInfo):
                    IndexText.Text = ViewModel.ImageInfo;
                    break;
            }
        });
    }

    private async void PlayVideo(string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var source = Windows.Media.Core.MediaSource.CreateFromStorageFile(file);
            VideoPlayer.Source = source;
        }
        catch { }
    }

    private void StopVideo()
    {
        if (VideoPlayer.Source is not null)
        {
            VideoPlayer.Source = null;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => ViewModel?.CloseCommand.Execute(null);
    private async void OnNext(object sender, RoutedEventArgs e) =>
        await (ViewModel?.NextImageCommand.ExecuteAsync(null) ?? Task.CompletedTask);
    private async void OnPrevious(object sender, RoutedEventArgs e) =>
        await (ViewModel?.PreviousImageCommand.ExecuteAsync(null) ?? Task.CompletedTask);
    private void OnZoomIn(object sender, RoutedEventArgs e)
    {
        if (IsStraightening) return;
        _zoomPan?.ZoomIn();
    }
    
    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        if (IsStraightening) return;
        _zoomPan?.ZoomOut();
    }
    
    private void OnZoomFit(object sender, RoutedEventArgs e)
    {
        if (IsStraightening) return;
        _zoomPan?.ApplyBestFit();
    }

    private void OnImageOpened(object sender, RoutedEventArgs e)
    {
        // Unhook to avoid multiple firings
        FullImage.ImageOpened -= OnImageOpened;

        DebugLog.WriteLine("ImageViewerOverlay: OnImageOpened called");

        if (FullImage.Source is not BitmapImage bitmap)
        {
            DebugLog.WriteLine("ImageViewerOverlay: Source is not BitmapImage");
            return;
        }

        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;

        if (width == 0 || height == 0)
        {
            DebugLog.WriteLine($"ImageViewer: Invalid image dimensions: {width}x{height}");
            return;
        }

        DebugLog.WriteLine($"ImageViewer: Image opened: {width}x{height}, ScrollViewer size: {ImageScrollViewer.ActualWidth}x{ImageScrollViewer.ActualHeight}");
        
        if (_zoomPan is null)
        {
            DebugLog.WriteLine("ImageViewerOverlay: _zoomPan is null!");
            return;
        }
        
        _zoomPan.SetImageSize(width, height);
        _zoomPan.ApplyBestFit();
        _currentImagePixelWidth = (uint)width;
        _currentImagePixelHeight = (uint)height;
        DebugLog.WriteLine("ImageViewerOverlay: Applied best fit");
        if (_pendingManualFaceTagging) EnterManualFaceTagging();
    }

    private void ImageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsCropping)
        {
            // Keep the inset fit so the crop handles stay inside the viewport after a resize.
            _zoomPan?.ApplyBestFit(CropInset);
            return;
        }
        if (IsStraightening)
        {
            _zoomPan?.ApplyBestFit();
            UpdateStraightenClip();
            return;
        }
        _zoomPan?.HandleSizeChanged(e.PreviousSize);
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (IsCropping || IsStraightening) return;
        DebugLog.WriteLine($"ImageViewerOverlay: Wheel changed, delta={e.GetCurrentPoint(RootGrid).Properties.MouseWheelDelta}");
        _zoomPan?.HandlePointerWheelChanged(e);
    }

    private void ImageScrollViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsCropping || IsStraightening || _isManualFaceTagging) return;
        _zoomPan?.HandlePointerPressed(ImageScrollViewer, e);
    }

    private void ImageScrollViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (IsCropping || IsStraightening || _isManualFaceTagging) return;
        _zoomPan?.HandlePointerMoved(ImageScrollViewer, e);
    }

    private void ImageScrollViewer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (IsCropping || IsStraightening || _isManualFaceTagging) return;
        _zoomPan?.HandlePointerReleased(ImageScrollViewer, e);
    }

    private void ImageScrollViewer_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _zoomPan?.HandlePointerCanceled(ImageScrollViewer, e);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Kept for backwards compatibility, but not used with new zoom controller
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // Kept for backwards compatibility, but not used with new zoom controller
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // Kept for backwards compatibility, but not used with new zoom controller
    }

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel is null) return;

        if (IsCropping)
        {
            // Swallow viewer navigation while cropping so arrow keys don't silently
            // discard the in-progress crop by moving to another photo.
            if (e.Key == Windows.System.VirtualKey.Escape)
                CropCancelRequested?.Invoke(this, EventArgs.Empty);
            else if (e.Key == Windows.System.VirtualKey.Enter)
                CropApplyRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (IsStraightening)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
                StraightenCancelRequested?.Invoke(this, EventArgs.Empty);
            else if (e.Key == Windows.System.VirtualKey.Enter)
                StraightenApplyRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (_isManualFaceTagging)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
                ExitManualFaceTagging();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                ViewModel.CloseCommand.Execute(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Right:
                await ViewModel.NextImageCommand.ExecuteAsync(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Left:
                await ViewModel.PreviousImageCommand.ExecuteAsync(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Add:
                ViewModel.ZoomInCommand.Execute(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Subtract:
                ViewModel.ZoomOutCommand.Execute(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.F:
                await ToggleFlagAsync();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Toggles the flag on the image currently shown in the viewer (F shortcut).</summary>
    private async Task ToggleFlagAsync()
    {
        var entry = ViewModel?.CurrentEntry;
        if (entry is null || App.ViewModel is null) return;

        await App.ViewModel.SetFlagAsync(new[] { entry }, !entry.IsFlagged);
    }

    private void OnStraightenAngleChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!IsStraightening) return;
        var angle = Math.Round(e.NewValue, 1, MidpointRounding.AwayFromZero);
        StraightenAngleText.Text = $"{angle:+0.0;-0.0;0.0}°";
        UpdateStraightenPreview(angle);
    }

    private async void OnAutoStraightenClick(object sender, RoutedEventArgs e) =>
        await RunAutoStraightenAsync();

    private async Task RunAutoStraightenAsync()
    {
        var entry = ViewModel?.CurrentEntry;
        if (!IsStraightening || entry is null || !AutoStraightenButton.IsEnabled)
            return;

        var analyzedPath = entry.FilePath;
        AutoStraightenButton.IsEnabled = false;
        AutoStraightenStatus.Text = "Detecting edges and lines…";
        try
        {
            var result = await AutoStraightenService.AnalyzeAsync(analyzedPath);
            if (!IsStraightening || ViewModel?.CurrentEntry?.FilePath != analyzedPath)
                return;

            if (!result.HasResult)
            {
                AutoStraightenStatus.Text = "No reliable horizon or vertical line was found.";
                return;
            }

            var correction = Math.Clamp(result.CorrectionDegrees, -45, 45);
            StraightenAngleSlider.Value = Math.Round(correction, 1);
            AutoStraightenStatus.Text =
                $"Proposed {correction:+0.0;-0.0;0.0}° correction ({result.Confidence:P0} confidence).";
        }
        catch (Exception ex)
        {
            if (IsStraightening)
                AutoStraightenStatus.Text = $"Auto detection failed: {ex.Message}";
        }
        finally
        {
            if (IsStraightening)
                AutoStraightenButton.IsEnabled = true;
        }
    }

    private void OnStraightenGuidePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsStraightening)
            return;

        var point = e.GetCurrentPoint(StraightenGuideOverlay);
        if (!point.Properties.IsLeftButtonPressed && !point.IsInContact)
            return;

        _isStraightenGuideDragging = true;
        _straightenGuideStart = point.Position;
        _straightenAngleAtGuideStart = StraightenAngleSlider.Value;
        SetStraightenGuideLine(_straightenGuideStart, _straightenGuideStart);
        StraightenGuideLine.Visibility = Visibility.Visible;
        StraightenGuideOverlay.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnStraightenGuidePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isStraightenGuideDragging)
            return;

        SetStraightenGuideLine(
            _straightenGuideStart,
            e.GetCurrentPoint(StraightenGuideOverlay).Position);
        e.Handled = true;
    }

    private void OnStraightenGuidePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isStraightenGuideDragging)
            return;

        var end = e.GetCurrentPoint(StraightenGuideOverlay).Position;
        StraightenGuideOverlay.ReleasePointerCapture(e.Pointer);
        _isStraightenGuideDragging = false;
        StraightenGuideLine.Visibility = Visibility.Collapsed;

        var deltaX = end.X - _straightenGuideStart.X;
        var deltaY = end.Y - _straightenGuideStart.Y;
        if (Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY)) < 24)
        {
            AutoStraightenStatus.Text = "Drag a longer guide along the horizon.";
            e.Handled = true;
            return;
        }

        var adjustment = StraightenGeometry.GetGuideCorrection(deltaX, deltaY);
        var correction = Math.Clamp(_straightenAngleAtGuideStart + adjustment, -45, 45);
        StraightenAngleSlider.Value = Math.Round(correction, 1, MidpointRounding.AwayFromZero);
        AutoStraightenStatus.Text =
            $"Guide set a {correction:+0.0;-0.0;0.0}° correction.";
        e.Handled = true;
    }

    private void OnStraightenGuidePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (!_isStraightenGuideDragging)
            return;

        StraightenGuideOverlay.ReleasePointerCapture(e.Pointer);
        _isStraightenGuideDragging = false;
        StraightenGuideLine.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void SetStraightenGuideLine(Point start, Point end)
    {
        StraightenGuideLine.X1 = start.X;
        StraightenGuideLine.Y1 = start.Y;
        StraightenGuideLine.X2 = end.X;
        StraightenGuideLine.Y2 = end.Y;
    }

    private void UpdateStraightenPreview(double angle)
    {
        if (_currentImagePixelWidth == 0 || _currentImagePixelHeight == 0)
            return;

        var scale = AutoCropGeometry.GetCoverScale(
            _currentImagePixelWidth,
            _currentImagePixelHeight,
            angle);
        StraightenTransform.Rotation = angle;
        StraightenTransform.ScaleX = scale;
        StraightenTransform.ScaleY = scale;
    }

    private void UpdateStraightenClip()
    {
        ImageHost.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, ImageHost.ActualWidth, ImageHost.ActualHeight)
        };
    }

    private void SetZoomControlsEnabled(bool isEnabled)
    {
        ZoomOutButton.IsEnabled = isEnabled;
        ZoomFitButton.IsEnabled = isEnabled;
        ZoomInButton.IsEnabled = isEnabled;
    }

    private void OnCancelManualFaceTagging(object sender, RoutedEventArgs e) =>
        ExitManualFaceTagging();

    private void OnManualFacePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_isManualFaceTagging || !e.GetCurrentPoint(ManualFaceOverlay).Properties.IsLeftButtonPressed)
            return;

        _isDrawingManualFace = true;
        _manualFaceStart = e.GetCurrentPoint(ManualFaceOverlay).Position;
        SetManualFaceRectangle(_manualFaceStart, _manualFaceStart);
        ManualFaceRectangle.Visibility = Visibility.Visible;
        ManualFaceOverlay.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnManualFacePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDrawingManualFace) return;
        SetManualFaceRectangle(
            _manualFaceStart,
            e.GetCurrentPoint(ManualFaceOverlay).Position);
        e.Handled = true;
    }

    private async void OnManualFacePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDrawingManualFace) return;

        _isDrawingManualFace = false;
        ManualFaceOverlay.ReleasePointerCapture(e.Pointer);
        SetManualFaceRectangle(
            _manualFaceStart,
            e.GetCurrentPoint(ManualFaceOverlay).Position);
        if (ManualFaceRectangle.Width < 8 || ManualFaceRectangle.Height < 8)
        {
            ManualFaceRectangle.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        var people = await App.ViewModel.PeopleReview.GetPeopleAsync();
        ManualFacePersonCombo.ItemsSource = people;
        ManualFacePersonCombo.SelectedItem = null;
        ManualFaceNewPersonBox.Text = "";
        PopulateManualFaceRecentPeople(people);
        ManualFaceTagFlyout.ShowAt(ManualFaceRectangle);
        e.Handled = true;
    }

    private void PopulateManualFaceRecentPeople(IReadOnlyList<Person> people)
    {
        ManualFaceRecentPeoplePanel.Children.Clear();
        var recentPeople = App.ViewModel.PeopleReview.RecentPeople
            .Select(recent => people.FirstOrDefault(person => person.Id == recent.Id))
            .OfType<Person>()
            .ToList();
        var hasRecent = recentPeople.Count > 0;
        ManualFaceRecentPeopleLabel.Visibility = hasRecent ? Visibility.Visible : Visibility.Collapsed;
        ManualFaceRecentPeoplePanel.Visibility = hasRecent ? Visibility.Visible : Visibility.Collapsed;
        foreach (var person in recentPeople)
        {
            var button = new Button { Content = person.Name, Tag = person };
            AutomationProperties.SetAutomationId(button, $"RecentPerson{person.Id}");
            AutomationProperties.SetName(button, $"Select recent person {person.Name}");
            button.Click += (_, _) =>
            {
                ManualFacePersonCombo.SelectedItem = person;
                ManualFaceNewPersonBox.Text = "";
            };
            ManualFaceRecentPeoplePanel.Children.Add(button);
        }
    }

    private void OnManualFacePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDrawingManualFace) return;
        ManualFaceOverlay.ReleasePointerCapture(e.Pointer);
        _isDrawingManualFace = false;
        ManualFaceRectangle.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    /// <summary>Highlights <paramref name="region"/> over the currently displayed image
    /// (only if this viewer is the surface currently on screen for that image — the caller
    /// is responsible for that check). Pass null to clear.</summary>
    public void SetFaceHighlight(FaceRegion? region)
    {
        if (region is null ||
            FaceHighlightOverlay.ActualWidth <= 0 ||
            FaceHighlightOverlay.ActualHeight <= 0)
        {
            FaceHighlightRectangle.Visibility = Visibility.Collapsed;
            return;
        }

        var width = FaceHighlightOverlay.ActualWidth;
        var height = FaceHighlightOverlay.ActualHeight;
        Canvas.SetLeft(FaceHighlightRectangle, region.X * width);
        Canvas.SetTop(FaceHighlightRectangle, region.Y * height);
        FaceHighlightRectangle.Width = region.Width * width;
        FaceHighlightRectangle.Height = region.Height * height;
        FaceHighlightRectangle.Visibility = Visibility.Visible;
    }

    private void SetManualFaceRectangle(Point first, Point second)
    {
        var left = Math.Clamp(Math.Min(first.X, second.X), 0, ManualFaceOverlay.ActualWidth);
        var top = Math.Clamp(Math.Min(first.Y, second.Y), 0, ManualFaceOverlay.ActualHeight);
        var right = Math.Clamp(Math.Max(first.X, second.X), 0, ManualFaceOverlay.ActualWidth);
        var bottom = Math.Clamp(Math.Max(first.Y, second.Y), 0, ManualFaceOverlay.ActualHeight);
        Canvas.SetLeft(ManualFaceRectangle, left);
        Canvas.SetTop(ManualFaceRectangle, top);
        ManualFaceRectangle.Width = right - left;
        ManualFaceRectangle.Height = bottom - top;
    }

    private async void OnSaveManualFaceTagClick(object sender, RoutedEventArgs e)
    {
        var entry = ViewModel?.CurrentEntry;
        if (entry is null || ManualFaceOverlay.ActualWidth <= 0 || ManualFaceOverlay.ActualHeight <= 0)
            return;

        var existingPerson = ManualFacePersonCombo.SelectedItem as Person;
        var name = ManualFaceNewPersonBox.Text.Trim();
        if (existingPerson is null && name.Length == 0)
        {
            ManualFaceNewPersonBox.Focus(FocusState.Programmatic);
            return;
        }

        SaveManualFaceTagButton.IsEnabled = false;
        try
        {
            var region = new FaceRegion
            {
                X = Canvas.GetLeft(ManualFaceRectangle) / ManualFaceOverlay.ActualWidth,
                Y = Canvas.GetTop(ManualFaceRectangle) / ManualFaceOverlay.ActualHeight,
                Width = ManualFaceRectangle.Width / ManualFaceOverlay.ActualWidth,
                Height = ManualFaceRectangle.Height / ManualFaceOverlay.ActualHeight
            };
            var personName = existingPerson?.Name ?? name;
            await App.ViewModel.AddManualFaceAsync(
                entry,
                region,
                existingPerson?.Id,
                personName);
            ExitManualFaceTagging();
        }
        catch (Exception exception)
        {
            App.ViewModel.StatusText = $"Could not add people tag: {exception.Message}";
        }
        finally
        {
            SaveManualFaceTagButton.IsEnabled = true;
        }
    }

    private void OnCancelManualFaceTagClick(object sender, RoutedEventArgs e) =>
        ExitManualFaceTagging();

    private void OnManualFaceTagFlyoutClosed(object sender, object e)
    {
        if (_isManualFaceTagging && !_isDrawingManualFace)
            ManualFaceRectangle.Visibility = Visibility.Collapsed;
    }
}
