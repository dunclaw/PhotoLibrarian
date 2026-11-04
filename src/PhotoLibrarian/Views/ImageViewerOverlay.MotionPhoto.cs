// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    private MemoryStream? _motionStream;
    private MediaSource? _motionSource;
    private MediaPlayer? _motionPlayer;
    private DispatcherTimer? _motionOpenTimer;

    private void InitializeMotionPhotos()
    {
        Loaded += OnMotionViewerLoaded;
        Unloaded += OnMotionViewerUnloaded;
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
        if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) return;
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
            var info = await Task.Run(() => MotionPhotoReader.ReadAsync(path, timeout.Token), timeout.Token);
            if (IsCurrentMotion(path, lifetime))
            {
                MotionPhotoButton.Visibility = info is null ? Visibility.Collapsed : Visibility.Visible;
                MotionPhotoTools.Visibility = MotionPhotoButton.Visibility;
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
        if (_motionPlayer is not null)
        {
            StopMotionPlayback();
            return;
        }
        if (_motionPath is not { } path || _motionLifetime is not { } lifetime ||
            !IsCurrentMotion(path, lifetime) || IsCropping || IsStraightening ||
            IsRedEyeRemoving || IsManualFaceTagging) return;

        MotionPhotoButton.IsEnabled = false;
        MotionPhotoStatus.IsOpen = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _motionPlaybackCancellation = timeout;
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        try
        {
            var clip = await Task.Run(() => MotionPhotoReader.OpenVideoAsync(path, token), token);
            if (!IsCurrentMotion(path, lifetime) || token.IsCancellationRequested)
            {
                clip.Video.Dispose();
                return;
            }
            _motionStream = clip.Video;
            _motionSource = MediaSource.CreateFromStream(clip.Video.AsRandomAccessStream(), "video/mp4");
            var player = new MediaPlayer { AutoPlay = false, IsLoopingEnabled = false };
            _motionPlayer = player;
            player.MediaOpened += OnMotionMediaOpened;
            player.MediaFailed += OnMotionMediaFailed;
            player.MediaEnded += OnMotionMediaEnded;
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

    private void OnMotionMediaEnded(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(sender, _motionPlayer)) StopMotionPlayback();
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
        MotionPhotoTools.Visibility = Visibility.Visible;
    }

    private void SetMotionButtonLabel(bool playing)
    {
        string label = MotionPhotoStrings.Get(playing ? "MotionPhotoShowStill" : "MotionPhotoPlay");
        MotionPhotoButton.Content = label;
        AutomationProperties.SetName(MotionPhotoButton, label);
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
            player.MediaEnded -= OnMotionMediaEnded;
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
        _motionLifetime?.Cancel();
        _motionLifetime?.Dispose();
        _motionLifetime = null;
        StopMotionPlayback();
        MotionPhotoButton.Visibility = Visibility.Collapsed;
        MotionPhotoTools.Visibility = Visibility.Collapsed;
        MotionPhotoStatus.IsOpen = false;
    }
}
