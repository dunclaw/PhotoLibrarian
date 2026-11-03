// Copyright (c) Duncan Lawler. All Rights Reserved.
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Diagnostics;
using PhotoLibrarian.Services;

namespace PhotoLibrarian.ViewModels;

public partial class SlideshowViewModel : ObservableObject
{
    private readonly SlideshowSequence _sequence;
    private int _loadVersion;
    private CancellationTokenSource? _activeLoad;
    private bool _closed;

    [ObservableProperty]
    public partial SoftwareBitmapSource? CurrentImage { get; set; }

    [ObservableProperty]
    public partial string PhotoLabel { get; set; } = "";

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial double IntervalSeconds { get; set; } = 5;

    public SlideshowTheme Theme { get; }
    public string ThemeName => Theme switch
    {
        SlideshowTheme.PanAndZoom => "Pan and Zoom",
        SlideshowTheme.Contemporary => "Contemporary",
        SlideshowTheme.Cinematic => "Cinematic",
        SlideshowTheme.Fade => "Fade",
        SlideshowTheme.BlackAndWhite => "Black and White",
        SlideshowTheme.Sepia => "Sepia",
        _ => throw new ArgumentOutOfRangeException(nameof(Theme), Theme, "Unknown slideshow theme.")
    };

    public string PauseLabel => IsPaused ? "Resume" : "Pause";

    public SlideshowViewModel(
        SlideshowSequence sequence,
        double intervalSeconds,
        SlideshowTheme theme = SlideshowTheme.Contemporary)
    {
        _sequence = sequence;
        IntervalSeconds = intervalSeconds;
        if (!Enum.IsDefined(theme))
            throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unknown slideshow theme.");

        Theme = theme;
    }

    partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(PauseLabel));

    partial void OnIntervalSecondsChanged(double value)
    {
        var valid = double.IsFinite(value) ? Math.Clamp(value, 1, 60) : 5;
        if (value != valid) IntervalSeconds = valid;
    }

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    [RelayCommand]
    private async Task NextAsync()
    {
        if (_closed) return;
        _sequence.Next();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        if (_closed) return;
        _sequence.Previous();
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (_closed || _sequence.Current is not { } entry) return;
        var version = ++_loadVersion;
        var cancellation = new CancellationTokenSource();
        var previousLoad = _activeLoad;
        _activeLoad = cancellation;
        previousLoad?.Cancel();
        IsLoading = true;
        PhotoLabel = $"{entry.FileName} ({_sequence.Index + 1} / {_sequence.Count})";
        Status = "Loading photo...";

        try
        {
            var image = await SlideshowImageRenderer.RenderAsync(entry.FilePath, Theme, cancellation.Token);
            if (_closed || cancellation.IsCancellationRequested || version != _loadVersion) return;
            CurrentImage = image;
            Status = "";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            NotSupportedException or
            ArgumentException or System.Runtime.InteropServices.COMException)
        {
            DebugLog.WriteLine($"Slideshow: could not load '{entry.FilePath}': {ex}");
            if (!_closed && version == _loadVersion)
            {
                CurrentImage = null;
                Status = $"Could not display {entry.FileName}. Use the arrows to continue, or Esc to exit.";
            }
        }
        finally
        {
            if (ReferenceEquals(_activeLoad, cancellation))
            {
                _activeLoad = null;
                if (!_closed && version == _loadVersion) IsLoading = false;
            }
            cancellation.Dispose();
        }
    }

    public void Close()
    {
        _closed = true;
        ++_loadVersion;
        _activeLoad?.Cancel();
        _activeLoad = null;
        IsLoading = false;
        CurrentImage = null;
    }
}
