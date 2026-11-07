// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using System.Globalization;

namespace PhotoLibrarian.Views;

public sealed partial class VideoTrimDialog : ContentDialog
{
    private readonly string _sourcePath;
    private readonly VideoTrimmingService _service = new();
    private CancellationTokenSource? _cancellation;
    private VideoTrimInfo? _info;
    private bool _updatingRange;
    private double _durationSeconds;
    private double _frameStepSeconds;
    private double _minimumSelectionSeconds;
    private double _startSeconds;
    private double _endSeconds;

    public string? ExportedPath { get; private set; }

    public VideoTrimDialog(string sourcePath)
    {
        InitializeComponent();
        _sourcePath = sourcePath;
        OutputBox.Text = VideoTrimmingService.GetAvailableOutputPath(sourcePath);
        IsPrimaryButtonEnabled = false;
    }

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        try
        {
            _info = await _service.GetVideoInfoAsync(_sourcePath);
            InitializeRange(_info.Value);
            IsPrimaryButtonEnabled = true;
            StatusText.Text = VideoTrimStrings.Format(
                "VideoTrimRangeReady", FormatTime(_info.Value.Duration));
        }
        catch (Exception ex)
        {
            ShowError(VideoTrimStrings.Format("VideoTrimOpenFailed", ex.Message));
        }
    }

    private void InitializeRange(VideoTrimInfo info)
    {
        _updatingRange = true;
        try
        {
            double duration = info.Duration.TotalSeconds;
            double step = 1 / info.FramesPerSecond;
            _durationSeconds = duration;
            _frameStepSeconds = step;
            _minimumSelectionSeconds = Math.Min(duration, step);
            StartSlider.Maximum = duration;
            EndSlider.Maximum = duration;
            StartSlider.StepFrequency = step;
            EndSlider.StepFrequency = step;
            StartSeconds.Minimum = 0;
            StartSeconds.Maximum = duration;
            EndSeconds.Minimum = 0;
            EndSeconds.Maximum = duration;
            StartSeconds.SmallChange = step;
            EndSeconds.SmallChange = step;
            StartSlider.Value = 0;
            StartSeconds.Value = 0;
            _endSeconds = SnapToFrame(duration);
            EndSlider.Value = _endSeconds;
            EndSeconds.Value = _endSeconds;
            _startSeconds = 0;
            StartSlider.IsEnabled = true;
            EndSlider.IsEnabled = true;
            StartSeconds.IsEnabled = true;
            EndSeconds.IsEnabled = true;
            UpdateTimeLabels();
        }
        finally
        {
            _updatingRange = false;
        }
    }

    private void OnStartSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_updatingRange) SetStart(e.NewValue);
    }

    private void OnEndSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_updatingRange) SetEnd(e.NewValue);
    }

    private void OnStartSecondsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_updatingRange && double.IsFinite(args.NewValue)) SetStart(args.NewValue);
    }

    private void OnEndSecondsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_updatingRange && double.IsFinite(args.NewValue)) SetEnd(args.NewValue);
    }

    private void SetStart(double value)
    {
        double latestStart = Math.Max(0, _endSeconds - _minimumSelectionSeconds);
        _startSeconds = Math.Min(SnapToFrame(Math.Clamp(value, 0, latestStart)), latestStart);
        _updatingRange = true;
        try
        {
            StartSlider.Value = _startSeconds;
            StartSeconds.Value = _startSeconds;
            UpdateTimeLabels();
        }
        finally
        {
            _updatingRange = false;
        }
    }

    private void SetEnd(double value)
    {
        double earliestEnd = Math.Min(_durationSeconds, _startSeconds + _minimumSelectionSeconds);
        _endSeconds = Math.Max(
            SnapToFrame(Math.Clamp(value, earliestEnd, _durationSeconds)), earliestEnd);
        _updatingRange = true;
        try
        {
            EndSlider.Value = _endSeconds;
            EndSeconds.Value = _endSeconds;
            UpdateTimeLabels();
        }
        finally
        {
            _updatingRange = false;
        }
    }

    private void UpdateTimeLabels()
    {
        StartTimeText.Text = FormatTime(TimeSpan.FromSeconds(_startSeconds));
        EndTimeText.Text = FormatTime(TimeSpan.FromSeconds(_endSeconds));
    }

    private static string FormatTime(TimeSpan value) =>
        string.Format(CultureInfo.CurrentCulture, "{0:00}:{1:00}:{2:00}.{3:000}",
            (long)value.TotalHours, value.Minutes, value.Seconds, value.Milliseconds);

    private double SnapToFrame(double seconds) =>
        Math.Clamp(Math.Round(seconds / _frameStepSeconds, MidpointRounding.AwayFromZero) * _frameStepSeconds,
            0, _durationSeconds);

    private async void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(
                XamlRoot.ContentIslandEnvironment.AppWindowId);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                OutputBox.Text = VideoTrimmingService.GetAvailableOutputPath(_sourcePath, folder.Path);
        }
        catch (Exception ex)
        {
            ShowError(VideoTrimStrings.Format("VideoTrimFolderFailed", ex.Message));
        }
    }

    private async void OnExportClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_info is not { } info)
        {
            ShowError(VideoTrimStrings.Get("VideoTrimRangeUnavailable"));
            return;
        }

        (TimeSpan Start, TimeSpan End) selection;
        try
        {
            selection = VideoTrimmingService.NormalizeSelection(
                TimeSpan.FromSeconds(_startSeconds), TimeSpan.FromSeconds(_endSeconds),
                info.Duration, info.FramesPerSecond);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            ShowError(ex.Message);
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        OptionsPanel.IsEnabled = false;
        IsPrimaryButtonEnabled = false;
        CloseButtonText = VideoTrimStrings.Get("VideoTrimCancelExport");
        ExportProgress.Visibility = Visibility.Visible;
        ExportProgress.Value = 0;
        ResultBar.IsOpen = false;
        StatusText.Text = VideoTrimStrings.Get("VideoTrimStarting");
        try
        {
            string destination = OutputBox.Text.Trim();
            await _service.TrimAsync(
                _sourcePath, destination, selection.Start, selection.End,
                new Progress<double>(percent =>
                {
                    if (_cancellation != cancellation || cancellation.IsCancellationRequested) return;
                    ExportProgress.Value = percent;
                    StatusText.Text = VideoTrimStrings.Format("VideoTrimProgressFormat", percent);
                }), cancellation.Token);
            ExportedPath = destination;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = VideoTrimStrings.Get("VideoTrimCanceled");
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            ShowError(ex.Message);
        }
        finally
        {
            _cancellation = null;
            OptionsPanel.IsEnabled = true;
            IsPrimaryButtonEnabled = true;
            CloseButtonText = VideoTrimStrings.Get("VideoTrimClose");
            ExportProgress.Visibility = Visibility.Collapsed;
        }
        if (ExportedPath is not null) Hide();
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_cancellation is null) return;
        args.Cancel = true;
        StatusText.Text = VideoTrimStrings.Get("VideoTrimCanceling");
        _cancellation.Cancel();
    }

    private void ShowError(string message)
    {
        ResultBar.Severity = InfoBarSeverity.Error;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
    }
}
