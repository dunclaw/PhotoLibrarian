// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoLibrarian.Core.Diagnostics;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace PhotoLibrarian.Views;

public sealed partial class VideoEditDialog : ContentDialog
{
    private readonly string _sourcePath;
    private CancellationTokenSource? _workCancellation;
    private VideoEditingService? _editing;
    private MediaPlayer? _player;
    private MediaSource? _previewSource;
    public string? ExportedPath { get; private set; }

    public VideoEditDialog(string sourcePath)
    {
        InitializeComponent();
        _sourcePath = sourcePath;
        OutputBox.Text = VideoEditingService.GetAvailableOutputPath(sourcePath);
        StatusText.Text = VideoEditStrings.Get("VideoEditPreparePrompt");
    }

    private void OnOptionsChanged(object sender, RoutedEventArgs args)
    {
        // XAML can raise SelectionChanged before the remaining named controls exist.
        if (PrepareButton is null) return;
        ReleasePreview();
        DisposeEditing();
        IsPrimaryButtonEnabled = false;
        PrepareButton.IsEnabled = RotationBox.SelectedIndex != 0 || RemoveAudioBox.IsChecked == true;
        StatusText.Text = VideoEditStrings.Get("VideoEditPreparePrompt");
    }

    private async void OnPrepare(object sender, RoutedEventArgs args)
    {
        if (_workCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _workCancellation = cancellation;
        SetBusy(true, preparing: true);
        ReleasePreview();
        DisposeEditing();
        try
        {
            _editing = await VideoEditingService.CreateAsync(_sourcePath,
                new(RotationBox.SelectedIndex, RemoveAudioBox.IsChecked == true), cancellation.Token);
            await _editing.PreparePreviewAsync(new Progress<double>(value =>
            {
                if (_workCancellation == cancellation && !cancellation.IsCancellationRequested)
                    ExportProgress.Value = value;
            }), cancellation.Token);
            AttachPreview();
            StatusText.Text = VideoEditStrings.Get("VideoEditPreviewReady");
        }
        catch (OperationCanceledException)
        {
            DisposeEditing();
            StatusText.Text = VideoEditStrings.Get("VideoEditCanceled");
        }
        catch (Exception ex)
        {
            DisposeEditing();
            ShowError(ex.Message);
        }
        finally
        {
            _workCancellation = null;
            SetBusy(false);
        }
    }

    private void AttachPreview()
    {
        _previewSource = MediaSource.CreateFromUri(new Uri(_editing!.PreviewPath!));
        _player = new MediaPlayer { AutoPlay = false };
        _player.MediaFailed += OnMediaFailed;
        _player.MediaOpened += OnMediaOpened;
        _player.Source = _previewSource;
        Preview.SetMediaPlayer(_player);
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (sender == _player) IsPrimaryButtonEnabled = true;
        });
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (sender != _player) return;
            IsPrimaryButtonEnabled = false;
            ShowError(VideoEditStrings.Format("VideoEditPreviewFailed", args.ErrorMessage));
        });
    }

    private async void OnChooseFolder(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(
                XamlRoot.ContentIslandEnvironment.AppWindowId);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                OutputBox.Text = VideoEditingService.GetAvailableOutputPath(_sourcePath, folder.Path);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void OnExport(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_editing is not { PreviewPath: not null } editing || _workCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _workCancellation = cancellation;
        SetBusy(true);
        ReleasePreview();
        try
        {
            var destination = OutputBox.Text.Trim();
            await editing.ExportAsync(destination, cancellation.Token);
            ExportedPath = destination;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = VideoEditStrings.Get("VideoEditCanceled");
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            _workCancellation = null;
            SetBusy(false);
        }
        if (ExportedPath is not null) Hide();
        else
        {
            try { AttachPreview(); }
            catch (Exception ex) { ShowError(VideoEditStrings.Format("VideoEditPreviewFailed", ex.Message)); }
        }
    }

    private void SetBusy(bool busy, bool preparing = false)
    {
        OptionsPanel.IsEnabled = !busy;
        if (busy)
        {
            IsPrimaryButtonEnabled = false;
            ErrorBar.IsOpen = false;
            ExportProgress.Value = 0;
            StatusText.Text = VideoEditStrings.Get(preparing ? "VideoEditPreparing" : "VideoEditExporting");
        }
        CloseButtonText = VideoEditStrings.Get(busy ? "VideoEditCancel" : "VideoEditClose");
        ExportProgress.IsIndeterminate = busy && !preparing;
        ExportProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_workCancellation is null) return;
        args.Cancel = true;
        StatusText.Text = VideoEditStrings.Get("VideoEditCanceling");
        _workCancellation.Cancel();
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        ReleasePreview();
        DisposeEditing();
    }

    private void ReleasePreview()
    {
        Preview.SetMediaPlayer(null);
        if (_player is not null)
        {
            _player.MediaFailed -= OnMediaFailed;
            _player.MediaOpened -= OnMediaOpened;
            _player.Source = null;
            _player.Dispose();
            _player = null;
        }
        _previewSource?.Dispose();
        _previewSource = null;
    }

    private void DisposeEditing()
    {
        try { _editing?.Dispose(); }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"Could not remove temporary video preview: {ex}");
            ShowError(VideoEditStrings.Format("VideoEditCleanupFailed", _editing?.PreviewPath, ex.Message));
        }
        _editing = null;
    }

    private void ShowError(string message)
    {
        ErrorBar.Severity = InfoBarSeverity.Error;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
