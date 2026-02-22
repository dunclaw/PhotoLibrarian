using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoLibrarian.ViewModels;
using System;

namespace PhotoLibrarian.Views;

public sealed partial class ImageViewerOverlay : UserControl
{
    private ImageViewerViewModel? ViewModel => App.ViewModel?.ImageViewer;

    public ImageViewerOverlay()
    {
        this.InitializeComponent();
        this.Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
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
                    FullImage.Source = ViewModel.CurrentImage;
                    // Wait for image to load, then calculate zoom to fit
                    FullImage.ImageOpened += OnImageOpened;
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
    private void OnZoomIn(object sender, RoutedEventArgs e) => ViewModel?.ZoomInCommand.Execute(null);
    private void OnZoomOut(object sender, RoutedEventArgs e) => ViewModel?.ZoomOutCommand.Execute(null);
    private void OnZoomFit(object sender, RoutedEventArgs e)
    {
        ViewModel?.ZoomFitCommand.Execute(null);
        ImageScrollViewer.ChangeView(0, 0, 1.0f);
    }

    private void OnImageOpened(object sender, RoutedEventArgs e)
    {
        // Unhook to avoid multiple firings
        FullImage.ImageOpened -= OnImageOpened;

        // Calculate zoom factor to fit image in viewport
        // With Stretch="Uniform", zoom 1.0 should already fit the image
        // Set MinZoomFactor dynamically so user can't zoom out past fit
        DispatcherQueue.TryEnqueue(() =>
        {
            var imageWidth = FullImage.ActualWidth;
            var imageHeight = FullImage.ActualHeight;
            var viewportWidth = ImageScrollViewer.ViewportWidth;
            var viewportHeight = ImageScrollViewer.ViewportHeight;

            if (imageWidth > 0 && imageHeight > 0 && viewportWidth > 0 && viewportHeight > 0)
            {
                // Calculate zoom needed to fit image in viewport
                var zoomToFitWidth = (float)(viewportWidth / imageWidth);
                var zoomToFitHeight = (float)(viewportHeight / imageHeight);
                var zoomToFit = Math.Min(zoomToFitWidth, zoomToFitHeight);

                // Set MinZoomFactor to the fit zoom so user can't zoom out too far
                ImageScrollViewer.MinZoomFactor = Math.Max(0.1f, zoomToFit * 0.95f);

                // Start at fit zoom
                ImageScrollViewer.ChangeView(0, 0, zoomToFit);
            }
            else
            {
                // Fallback if dimensions not available
                ImageScrollViewer.MinZoomFactor = 0.1f;
                ImageScrollViewer.ChangeView(0, 0, 1.0f);
            }
        });
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // Handle mouse wheel for zooming instead of scrolling
        var pointer = e.GetCurrentPoint(ImageScrollViewer);
        var delta = pointer.Properties.MouseWheelDelta;

        // Get current zoom factor
        var currentZoom = ImageScrollViewer.ZoomFactor;
        var zoomDelta = delta > 0 ? 1.1f : 0.9f;
        var newZoom = currentZoom * zoomDelta;

        // Clamp to min/max
        newZoom = Math.Max(ImageScrollViewer.MinZoomFactor, Math.Min(ImageScrollViewer.MaxZoomFactor, newZoom));

        // Apply zoom centered on pointer position
        ImageScrollViewer.ChangeView(null, null, newZoom);
        
        e.Handled = true;
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
