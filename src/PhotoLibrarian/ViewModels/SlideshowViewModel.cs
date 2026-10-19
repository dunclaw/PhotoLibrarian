using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Diagnostics;

namespace PhotoLibrarian.ViewModels;

public partial class SlideshowViewModel : ObservableObject
{
    private readonly SlideshowSequence _sequence;
    private int _loadVersion;
    private bool _closed;

    [ObservableProperty]
    public partial BitmapImage? CurrentImage { get; set; }

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

    public string PauseLabel => IsPaused ? "Resume" : "Pause";

    public SlideshowViewModel(SlideshowSequence sequence, double intervalSeconds)
    {
        _sequence = sequence;
        IntervalSeconds = intervalSeconds;
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
        IsLoading = true;
        CurrentImage = null;
        PhotoLabel = $"{entry.FileName} ({_sequence.Index + 1} / {_sequence.Count})";
        Status = "Loading photo...";

        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(entry.FilePath);
            using var stream = await file.OpenReadAsync();
            var image = new BitmapImage { DecodePixelWidth = 3840 };
            await image.SetSourceAsync(stream);
            if (_closed || version != _loadVersion) return;
            CurrentImage = image;
            Status = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or System.Runtime.InteropServices.COMException)
        {
            DebugLog.WriteLine($"Slideshow: could not load '{entry.FilePath}': {ex}");
            if (!_closed && version == _loadVersion)
                Status = $"Could not display {entry.FileName}. Use the arrows to continue, or Esc to exit.";
        }
        finally
        {
            if (!_closed && version == _loadVersion) IsLoading = false;
        }
    }

    public void Close()
    {
        _closed = true;
        ++_loadVersion;
        CurrentImage = null;
    }
}
