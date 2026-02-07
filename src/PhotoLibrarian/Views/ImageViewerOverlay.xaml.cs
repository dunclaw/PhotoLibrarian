using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoLibrarian.ViewModels;

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
                    ImageScrollViewer.ChangeView(null, null, 1.0f);
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
        ImageScrollViewer.ChangeView(null, null, 1.0f);
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
