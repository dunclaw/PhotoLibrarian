// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Diagnostics;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.Views;

namespace PhotoLibrarian;

public sealed partial class MainWindow
{
    private string? _videoEditSourcePath;
    private VideoEditingService? _videoEditing;
    private CancellationTokenSource? _videoEditCancellation;
    private Task? _videoEditTask;
    private TimeSpan _videoEditSourcePosition;
    private bool _videoEditSourceWasPlaying;
    private bool _videoEditExitInProgress;
    private string? _videoToolbarSourcePath;

    private void BeginVideoEditing()
    {
        if (_videoEditSourcePath is not null ||
            App.ViewModel?.ImageViewer.CurrentEntry is not { MediaType: MediaType.Video } entry)
            return;
        _videoEditSourcePath = entry.FilePath;
        _videoEditSourcePosition = ViewerOverlay.VideoPosition;
        _videoEditSourceWasPlaying = ViewerOverlay.IsVideoPlaying;
        ViewerOverlay.PauseVideo();
        ViewerOverlay.SetVideoEditingMode(true);
    }

    private async void OnVideoEditOptionsChanged(object? sender, EventArgs args)
    {
        if (_videoEditCancellation is not null || _videoEditExitInProgress) return;
        BeginVideoEditing();
        bool restoreSource = _videoEditing?.PreviewPath is not null;
        string? cleanupError = await ReleaseVideoEditPreviewAsync(restoreSource);
        if (cleanupError is not null)
            TopRibbon.SetVideoEditState(false, preparing: false, hasPreview: false, progress: 0,
                VideoEditStrings.Get("VideoEditPreparePrompt"), cleanupError);
    }

    private async void OnVideoEditPrepareClicked(object? sender, EventArgs args) =>
        await StartVideoEditOperationAsync(PrepareVideoEditPreviewAsync);

    private async void OnVideoEditExportClicked(object? sender, EventArgs args) =>
        await StartVideoEditOperationAsync(ExportVideoEditAsync);

    private async void OnVideoEditCancelClicked(object? sender, EventArgs args)
    {
        if (_videoEditCancellation is { } cancellation)
        {
            TopRibbon.SetVideoEditState(true, preparing: false, hasPreview: TopRibbon.HasVideoEditPreview,
                progress: 0, VideoEditStrings.Get("VideoEditCanceling"));
            cancellation.Cancel();
            return;
        }

        await ExitVideoEditAsync(restoreSource: true);
        if (_videoEditSourcePath is null)
            TopRibbon.EnterVideoEditMode();
    }

    private async Task StartVideoEditOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_videoEditCancellation is not null || _videoEditSourcePath is null) return;

        var cancellation = new CancellationTokenSource();
        _videoEditCancellation = cancellation;
        try
        {
            var task = operation(cancellation.Token);
            _videoEditTask = task;
            await task;
        }
        catch (OperationCanceledException)
        {
            TopRibbon.SetVideoEditState(false, preparing: false, hasPreview: TopRibbon.HasVideoEditPreview,
                progress: 0, VideoEditStrings.Get("VideoEditCanceled"));
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"Video edit operation failed: {ex}");
            TopRibbon.SetVideoEditState(false, preparing: false, hasPreview: TopRibbon.HasVideoEditPreview,
                progress: 0, VideoEditStrings.Get("VideoEditPreparePrompt"), ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_videoEditCancellation, cancellation))
            {
                _videoEditCancellation = null;
                _videoEditTask = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task PrepareVideoEditPreviewAsync(CancellationToken token)
    {
        string sourcePath = _videoEditSourcePath!;
        TopRibbon.SetVideoEditState(true, preparing: true, hasPreview: false, progress: 0,
            VideoEditStrings.Get("VideoEditPreparing"));

        try
        {
            bool hadPreview = _videoEditing?.PreviewPath is not null;
            string? cleanupError = await ReleaseVideoEditPreviewAsync(hadPreview);
            if (cleanupError is not null)
                throw new InvalidOperationException(cleanupError);

            var editing = await VideoEditingService.CreateAsync(
                sourcePath, TopRibbon.GetVideoEditOptions(), token);
            _videoEditing = editing;
            EnsureVideoEditSourceIsCurrent(sourcePath, token);

            var progress = new Progress<double>(value =>
            {
                if (!token.IsCancellationRequested && IsCurrentVideoEditSource(sourcePath))
                    TopRibbon.SetVideoEditProgress(value);
            });
            await editing.PreparePreviewAsync(progress, token);
            EnsureVideoEditSourceIsCurrent(sourcePath, token);

            if (editing.PreviewPath is not { } previewPath ||
                !await ViewerOverlay.ShowVideoEditPreviewAsync(sourcePath, previewPath))
                throw new InvalidOperationException(VideoEditStrings.Get("VideoEditPreviewOpenFailed"));

            TopRibbon.SetVideoEditState(false, preparing: false, hasPreview: true, progress: 100,
                VideoEditStrings.Get("VideoEditPreviewReady"));
        }
        catch (OperationCanceledException)
        {
            string? cleanupError = await ReleaseVideoEditPreviewAsync(
                restoreSource: IsCurrentVideoEditSource(sourcePath));
            TopRibbon.SetVideoEditState(false, preparing: false, hasPreview: false, progress: 0,
                VideoEditStrings.Get("VideoEditCanceled"), cleanupError);
        }
        catch (Exception ex)
        {
            string? cleanupError = await ReleaseVideoEditPreviewAsync(
                restoreSource: IsCurrentVideoEditSource(sourcePath));
            string error = VideoEditStrings.Format("VideoEditPreviewFailed", ex.Message);
            if (cleanupError is not null) error += Environment.NewLine + cleanupError;
            TopRibbon.SetVideoEditState(false, preparing: false, hasPreview: false, progress: 0,
                VideoEditStrings.Get("VideoEditPreparePrompt"), error);
        }
    }

    private async Task ExportVideoEditAsync(CancellationToken token)
    {
        string sourcePath = _videoEditSourcePath!;
        var editing = _videoEditing;
        if (editing?.PreviewPath is not { } previewPath)
            throw new InvalidOperationException(VideoEditStrings.Get("VideoEditPreviewRequired"));

        string destinationPath = VideoEditingService.GetAvailableOutputPath(sourcePath);
        TopRibbon.SetVideoEditState(true, preparing: false, hasPreview: true, progress: 0,
            VideoEditStrings.Get("VideoEditExporting"));
        ViewerOverlay.StopVideoEditPreview(previewPath);

        try
        {
            await editing.ExportAsync(destinationPath, token);

            string? cleanupError = await ReleaseVideoEditPreviewAsync(restoreSource: false);
            ExitVideoEditMode();
            var viewModel = App.ViewModel!;
            try
            {
                await viewModel.OpenEditedVideoAsync(destinationPath);
                if (cleanupError is not null) viewModel.StatusText = cleanupError;
            }
            catch (Exception ex)
            {
                DebugLog.WriteLine($"Could not reveal edited video: {ex}");
                await ViewerOverlay.RestoreVideoPlaybackAsync(
                    sourcePath, _videoEditSourcePosition, _videoEditSourceWasPlaying);
                viewModel.StatusText = VideoEditStrings.Format("VideoEditRevealFailed", destinationPath, ex.Message);
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentVideoEditSource(sourcePath))
                await ViewerOverlay.ShowVideoEditPreviewAsync(sourcePath, previewPath);
            TopRibbon.SetVideoEditState(false, preparing: false, hasPreview: true, progress: 100,
                VideoEditStrings.Get("VideoEditCanceled"));
            throw;
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"Could not export edited video: {ex}");
            if (IsCurrentVideoEditSource(sourcePath))
                await ViewerOverlay.ShowVideoEditPreviewAsync(sourcePath, previewPath);
            TopRibbon.SetVideoEditState(false, preparing: false, hasPreview: true, progress: 100,
                VideoEditStrings.Get("VideoEditPreviewReady"),
                VideoEditStrings.Format("VideoEditExportFailed", ex.Message));
        }
    }

    private async Task<string?> ReleaseVideoEditPreviewAsync(bool restoreSource)
    {
        string? cleanupError = null;
        if (_videoEditing is { } editing)
        {
            if (editing.PreviewPath is { } previewPath)
                ViewerOverlay.StopVideoEditPreview(previewPath);
            try
            {
                editing.Dispose();
                _videoEditing = null;
            }
            catch (Exception ex)
            {
                DebugLog.WriteLine($"Could not remove video edit preview: {ex}");
                cleanupError = VideoEditStrings.Format(
                    "VideoEditCleanupFailed", editing.PreviewPath ?? "", ex.Message);
            }
        }

        if (restoreSource && _videoEditSourcePath is { } sourcePath &&
            IsCurrentVideoEditSource(sourcePath))
        {
            await ViewerOverlay.RestoreVideoPlaybackAsync(
                sourcePath, _videoEditSourcePosition, _videoEditSourceWasPlaying);
        }
        return cleanupError;
    }

    private bool IsCurrentVideoEditSource(string sourcePath) =>
        !_isClosing && App.ViewModel?.ImageViewer is { IsOpen: true } viewer &&
        viewer.CurrentEntry is { MediaType: MediaType.Video } entry &&
        string.Equals(entry.FilePath, sourcePath, StringComparison.OrdinalIgnoreCase);

    private void EnsureVideoEditSourceIsCurrent(string sourcePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrentVideoEditSource(sourcePath))
            throw new OperationCanceledException(token);
    }

    private async Task ExitVideoEditAsync(bool restoreSource)
    {
        if (_videoEditExitInProgress || (_videoEditSourcePath is null && _videoEditing is null)) return;
        _videoEditExitInProgress = true;
        try
        {
            if (_videoEditCancellation is { } cancellation)
            {
                cancellation.Cancel();
                if (_videoEditTask is { } task)
                {
                    try { await task; }
                    catch (OperationCanceledException) { }
                }
            }

            string? cleanupError = await ReleaseVideoEditPreviewAsync(
                restoreSource && _videoEditSourcePath is { } sourcePath &&
                IsCurrentVideoEditSource(sourcePath));
            ExitVideoEditMode();
            if (cleanupError is not null && App.ViewModel is { } viewModel)
                viewModel.StatusText = cleanupError;
        }
        finally
        {
            _videoEditExitInProgress = false;
        }
    }

    private void ExitVideoEditMode()
    {
        _videoEditSourcePath = null;
        ViewerOverlay.SetVideoEditingMode(false);
        TopRibbon.ExitVideoEditMode();
    }

    private async Task UpdateVideoToolbarAsync()
    {
        string? sourcePath = !_isClosing && ViewModel.ImageViewer.IsOpen &&
            ViewModel.ImageViewer.CurrentEntry is { MediaType: MediaType.Video } entry
            ? entry.FilePath : null;
        if (string.Equals(_videoToolbarSourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
            return;

        _videoToolbarSourcePath = sourcePath;
        await ExitVideoEditAsync(restoreSource: false);
        if (sourcePath is not null && IsCurrentVideoEditSource(sourcePath))
            TopRibbon.EnterVideoEditMode();
    }
}
