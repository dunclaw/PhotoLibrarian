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
                    // Attach handler BEFORE setting source (in case image is cached and fires immediately)
                    FullImage.ImageOpened += OnImageOpened;
                    FullImage.Source = ViewModel.CurrentImage;
                    
                    // If image is already loaded (cached), manually trigger setup
                    if (ViewModel.CurrentImage is BitmapImage bmp && bmp.PixelWidth > 0)
                    {
                        DebugLog.WriteLine($"ImageViewerOverlay: Image already loaded, setting up manually");
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
        DebugLog.WriteLine("ImageViewerOverlay: Applied best fit");
    }

    private void ImageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _zoomPan?.HandleSizeChanged(e.PreviousSize);
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        DebugLog.WriteLine($"ImageViewerOverlay: Wheel changed, delta={e.GetCurrentPoint(RootGrid).Properties.MouseWheelDelta}");
        _zoomPan?.HandlePointerWheelChanged(e);
    }

    private void ImageScrollViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _zoomPan?.HandlePointerPressed(ImageScrollViewer, e);
    }

    private void ImageScrollViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _zoomPan?.HandlePointerMoved(ImageScrollViewer, e);
    }

    private void ImageScrollViewer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
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
