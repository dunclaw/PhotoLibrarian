using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Diagnostics;
using PhotoLibrarian.Services;
using System;
using Windows.Foundation;

namespace PhotoLibrarian.Views;

public sealed partial class ImageViewerOverlay : UserControl
{
    private ImageViewerViewModel? ViewModel => App.ViewModel?.ImageViewer;
    private ImageZoomPanController? _zoomPan;
    private uint _currentImagePixelWidth;
    private uint _currentImagePixelHeight;
    public bool IsCropping { get; private set; }
    public event EventHandler? CropExited;
    public event EventHandler? CropApplyRequested;
    public event EventHandler? CropCancelRequested;

    public CropOverlay CropOverlay => CropOverlayView;
    public uint CurrentImagePixelWidth => _currentImagePixelWidth;
    public uint CurrentImagePixelHeight => _currentImagePixelHeight;

    // Gap left around the image while cropping so the handles — which overhang the crop rect
    // by half their hit size — are never clipped by the viewport edge.
    private const double CropInset = 20;

    public void EnterCropMode()
    {
        if (_currentImagePixelWidth == 0 || _currentImagePixelHeight == 0) return;

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

    public ImageViewerOverlay()
    {
        this.InitializeComponent();
        this.Loaded += OnLoaded;
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
                    // Cancel any in-progress crop when navigating to a different image.
                    if (IsCropping) ExitCropMode();
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
        _zoomPan?.ZoomIn();
    }
    
    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        _zoomPan?.ZoomOut();
    }
    
    private void OnZoomFit(object sender, RoutedEventArgs e)
    {
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
    }

    private void ImageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsCropping)
        {
            // Keep the inset fit so the crop handles stay inside the viewport after a resize.
            _zoomPan?.ApplyBestFit(CropInset);
            return;
        }
        _zoomPan?.HandleSizeChanged(e.PreviousSize);
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (IsCropping) return;
        DebugLog.WriteLine($"ImageViewerOverlay: Wheel changed, delta={e.GetCurrentPoint(RootGrid).Properties.MouseWheelDelta}");
        _zoomPan?.HandlePointerWheelChanged(e);
    }

    private void ImageScrollViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsCropping) return;
        _zoomPan?.HandlePointerPressed(ImageScrollViewer, e);
    }

    private void ImageScrollViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (IsCropping) return;
        _zoomPan?.HandlePointerMoved(ImageScrollViewer, e);
    }

    private void ImageScrollViewer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (IsCropping) return;
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
        }
    }
}
