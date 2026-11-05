// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Diagnostics;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace PhotoLibrarian.Views;

public sealed partial class ImageViewerOverlay
{
    private ImageViewerViewModel? _motionViewModel;
    private CancellationTokenSource? _motionLifetime;
    private CancellationTokenSource? _motionPlaybackCancellation;
    private string? _motionPath;
    private AppleLivePhotoPair? _appleLivePhotoPair;
    private MemoryStream? _motionStream;
    private MediaSource? _motionSource;
    private MediaPlayer? _motionPlayer;
    private DispatcherTimer? _motionOpenTimer;
    private bool _motionPaused;

    private void InitializeMotionPhotos()
    {
        Loaded += OnMotionViewerLoaded;
        Unloaded += OnMotionViewerUnloaded;
        MotionPlayer.AddHandler(UIElement.TappedEvent, new TappedEventHandler(OnMotionImageTapped), true);
        SetMotionButtonLabel(false);
    }

    private void OnMotionViewerLoaded(object sender, RoutedEventArgs args)
    {
        if (_motionViewModel is not null) return;
        _motionViewModel = ViewModel;
        if (_motionViewModel is null) return;
        _motionViewModel.CurrentEntryChanged += OnMotionEntryChanged;
        OnMotionEntryChanged(_motionViewModel.CurrentEntry);
    }

    private void OnMotionViewerUnloaded(object sender, RoutedEventArgs args)
    {
        if (_motionViewModel is not null)
            _motionViewModel.CurrentEntryChanged -= OnMotionEntryChanged;
        _motionViewModel = null;
        ResetMotionPhoto();
    }

    private void OnMotionEntryChanged(ImageEntry? entry)
    {
        ResetMotionPhoto();
        if (ViewModel?.IsOpen != true || entry?.MediaType != MediaType.Image) return;
        string extension = Path.GetExtension(entry.FilePath);
        if (!IsMotionPhotoStillExtension(extension)) return;
        _motionPath = entry.FilePath;
        var lifetime = new CancellationTokenSource();
        _motionLifetime = lifetime;
        _ = DetectMotionPhotoAsync(entry.FilePath, lifetime);
    }

    private bool IsCurrentMotion(string path, CancellationTokenSource lifetime) =>
        ReferenceEquals(lifetime, _motionLifetime) && !lifetime.IsCancellationRequested &&
        ViewModel?.IsOpen == true && IsLoaded &&
        string.Equals(ViewModel.CurrentEntry?.FilePath, path, StringComparison.OrdinalIgnoreCase);

    private async Task DetectMotionPhotoAsync(string path, CancellationTokenSource lifetime)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var pixelInfo = IsPixelMotionPhotoStillExtension(Path.GetExtension(path))
                ? await Task.Run(
                    () => MotionPhotoReader.ReadAsync(path, timeout.Token), timeout.Token)
                : null;
            if (pixelInfo is not null)
            {
                if (IsCurrentMotion(path, lifetime))
                {
                    MotionPhotoButton.Visibility = Visibility.Visible;
                    if (!_motionPaused)
                        await StartMotionPlaybackAsync(path, lifetime);
                }
                return;
            }

            var applePair = await AppleLivePhotoReader.FindPairAsync(path, timeout.Token);
            if (!IsCurrentMotion(path, lifetime)) return;
            if (applePair.State == AppleLivePhotoPairState.NotLivePhoto) return;

            if (applePair.State == AppleLivePhotoPairState.Paired)
            {
                _appleLivePhotoPair = applePair;
                _motionPaused = true;
                MotionPhotoButton.Visibility = Visibility.Visible;
                SetMotionButtonLabel(false);
            }
            else
            {
                MotionPhotoStatus.Message = MotionPhotoStrings.Get(applePair.State switch
                {
                    AppleLivePhotoPairState.MissingMovie => "AppleLivePhotoMissingMovie",
                    AppleLivePhotoPairState.AmbiguousMovies => "AppleLivePhotoAmbiguous",
                    AppleLivePhotoPairState.MissingStillImageTime => "AppleLivePhotoMissingTiming",
                    AppleLivePhotoPairState.DiscoveryLimitExceeded => "AppleLivePhotoSearchLimit",
                    AppleLivePhotoPairState.UnsupportedStillFormat => "AppleLivePhotoUnsupportedStillFormat",
                    _ => "AppleLivePhotoUnavailable"
                });
                MotionPhotoStatus.IsOpen = true;
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentMotion(path, lifetime)) ReportMotionFailure("MotionPhotoTimeout");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException or OverflowException)
        {
            if (IsCurrentMotion(path, lifetime)) ReportMotionFailure("MotionPhotoUnavailable", ex);
        }
    }

    private async void OnMotionPhotoClick(object sender, RoutedEventArgs args)
    {
        if (_motionPlayer is not null || _motionPlaybackCancellation is not null)
        {
            StopMotionPlayback();
            return;
        }
        if (_motionPath is { } path && _motionLifetime is { } lifetime)
            await StartMotionPlaybackAsync(path, lifetime);
    }

    private void OnMotionImageTapped(object sender, TappedRoutedEventArgs args)
    {
        if (_motionPlayer is null) return;
        StopMotionPlayback();
        args.Handled = true;
    }

    private async Task StartMotionPlaybackAsync(string path, CancellationTokenSource lifetime)
    {
        if (_motionPlayer is not null || _motionPlaybackCancellation is not null ||
            !IsCurrentMotion(path, lifetime) || IsCropping || IsStraightening ||
            IsRedEyeRemoving || IsManualFaceTagging) return;

        _motionPaused = false;
        SetMotionButtonLabel(true);
        MotionPhotoStatus.IsOpen = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _motionPlaybackCancellation = timeout;
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        try
        {
            MediaSource source;
            if (_appleLivePhotoPair is { } applePair)
            {
                await AppleLivePhotoReader.ValidatePairAsync(applePair, token);
                string moviePath = applePair.MoviePath
                    ?? throw new InvalidDataException("The verified Live Photo movie path is missing.");
                var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(moviePath)
                    .AsTask(token);
                source = MediaSource.CreateFromStorageFile(storageFile);
            }
            else
            {
                var clip = await Task.Run(
                    () => MotionPhotoReader.OpenVideoAsync(path, token), token);
                if (!IsCurrentMotion(path, lifetime) || token.IsCancellationRequested)
                {
                    clip.Video.Dispose();
                    return;
                }
                _motionStream = clip.Video;
                source = MediaSource.CreateFromStream(clip.Video.AsRandomAccessStream(), "video/mp4");
            }

            if (!IsCurrentMotion(path, lifetime) || token.IsCancellationRequested)
            {
                source.Dispose();
                return;
            }
            _motionSource = source;
            var player = new MediaPlayer { AutoPlay = false, IsLoopingEnabled = true };
            _motionPlayer = player;
            player.MediaOpened += OnMotionMediaOpened;
            player.MediaFailed += OnMotionMediaFailed;
            MotionPlayer.SetMediaPlayer(player);
            player.Source = _motionSource;
            _motionOpenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _motionOpenTimer.Tick += OnMotionOpenTimeout;
            _motionOpenTimer.Start();
            SetMotionButtonLabel(true);
            MotionPhotoButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentMotion(path, lifetime) && ReferenceEquals(timeout, _motionPlaybackCancellation))
                ReportMotionFailure("MotionPhotoTimeout");
        }
        catch (Exception ex)
        {
            if (IsCurrentMotion(path, lifetime) && ReferenceEquals(timeout, _motionPlaybackCancellation))
                ReportMotionFailure("MotionPhotoUnavailable", ex);
        }
        finally
        {
            if (ReferenceEquals(timeout, _motionPlaybackCancellation))
                _motionPlaybackCancellation = null;
        }
    }

    private void OnMotionMediaOpened(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, _motionPlayer)) return;
            StopMotionOpenTimer();
            var session = sender.PlaybackSession;
            if (!IsSupportedMotionMedia(session.NaturalDuration, session.NaturalVideoWidth, session.NaturalVideoHeight))
            {
                ReportMotionFailure("MotionPhotoMediaLimit");
                return;
            }

            MotionPlayer.Visibility = Visibility.Visible;
            SetZoomControlsEnabled(false);
            sender.Play();
        });
    }

    internal static bool IsSupportedMotionMedia(TimeSpan duration, uint width, uint height) =>
        duration > TimeSpan.Zero && duration <= TimeSpan.FromSeconds(30) &&
        width is > 0 and <= 4096 && height is > 0 and <= 4096;

    private void OnMotionMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(sender, _motionPlayer))
                ReportMotionFailure("MotionPhotoCodecUnavailable", args.ExtendedErrorCode);
        });
    }

    private void OnMotionOpenTimeout(object? sender, object args)
    {
        if (_motionOpenTimer is not null && ReferenceEquals(sender, _motionOpenTimer))
            ReportMotionFailure("MotionPhotoTimeout");
    }

    private void ReportMotionFailure(string key, Exception? error = null)
    {
        DebugLog.WriteLine($"Motion photo: {key}: {error}");
        StopMotionPlayback();
        MotionPhotoStatus.Message = key == "MotionPhotoUnavailable"
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, MotionPhotoStrings.Get(key), error?.Message)
            : MotionPhotoStrings.Get(key);
        MotionPhotoStatus.IsOpen = true;
    }

    private void SetMotionButtonLabel(bool playing)
    {
        string label = MotionPhotoStrings.Get(playing ? "MotionPhotoPause" : "MotionPhotoPlay");
        MotionPhotoIcon.Glyph = playing ? "\uE769" : "\uE768";
        AutomationProperties.SetName(MotionPhotoButton, label);
        ToolTipService.SetToolTip(MotionPhotoButton, label);
    }

    private void StopMotionOpenTimer()
    {
        if (_motionOpenTimer is null) return;
        _motionOpenTimer.Stop();
        _motionOpenTimer.Tick -= OnMotionOpenTimeout;
        _motionOpenTimer = null;
    }

    private void StopMotionPlayback()
    {
        _motionPaused = true;
        _motionPlaybackCancellation?.Cancel();
        _motionPlaybackCancellation = null;
        StopMotionOpenTimer();
        var player = _motionPlayer;
        _motionPlayer = null;
        MotionPlayer.Visibility = Visibility.Collapsed;
        MotionPlayer.SetMediaPlayer(null);
        if (player is not null)
        {
            player.MediaOpened -= OnMotionMediaOpened;
            player.MediaFailed -= OnMotionMediaFailed;
            player.Pause();
            player.Source = null;
            player.Dispose();
            SetZoomControlsEnabled(!IsStraightening && !IsManualFaceTagging);
        }
        _motionSource?.Dispose();
        _motionSource = null;
        _motionStream?.Dispose();
        _motionStream = null;
        MotionPhotoButton.IsEnabled = true;
        SetMotionButtonLabel(false);
    }

    private void ResetMotionPhoto()
    {
        _motionPath = null;
        _appleLivePhotoPair = null;
        _motionLifetime?.Cancel();
        _motionLifetime?.Dispose();
        _motionLifetime = null;
        StopMotionPlayback();
        _motionPaused = false;
        MotionPhotoButton.Visibility = Visibility.Collapsed;
        MotionPhotoStatus.IsOpen = false;
    }

    private static bool IsMotionPhotoStillExtension(string extension) =>
        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".heif", StringComparison.OrdinalIgnoreCase);

    private static bool IsPixelMotionPhotoStillExtension(string extension) =>
        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
}
