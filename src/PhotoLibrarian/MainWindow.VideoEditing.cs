// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Diagnostics;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using PhotoLibrarian.Views;

namespace PhotoLibrarian;

public sealed partial class MainWindow
{
    private bool _isVideoEditOpen;

    private async void OnEditVideo(object? sender, EventArgs args)
    {
        if (_isVideoEditOpen || App.ViewModel?.ImageViewer.CurrentEntry is not { MediaType: MediaType.Video } entry)
            return;
        _isVideoEditOpen = true;
        try
        {
            ViewerOverlay.PauseVideo();
            var dialog = new VideoEditDialog(entry.FilePath) { XamlRoot = ViewerOverlay.XamlRoot };
            await dialog.ShowAsync();
            if (dialog.ExportedPath is not { } path) return;
            try
            {
                await App.ViewModel.OpenEditedVideoAsync(path);
            }
            catch (Exception ex)
            {
                DebugLog.WriteLine($"Could not reveal edited video: {ex}");
                App.ViewModel.StatusText = VideoEditStrings.Format("VideoEditRevealFailed", path, ex.Message);
            }
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"Could not open video editing: {ex}");
            App.ViewModel!.StatusText = VideoEditStrings.Format("VideoEditOpenFailed", ex.Message);
        }
        finally
        {
            _isVideoEditOpen = false;
        }
    }
}
