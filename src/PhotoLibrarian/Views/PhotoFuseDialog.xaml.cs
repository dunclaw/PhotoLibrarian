// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using Windows.Foundation;

namespace PhotoLibrarian.Views;

public sealed partial class PhotoFuseDialog : ContentDialog
{
    private readonly string[] _paths;
    private readonly List<PhotoFuseRegion> _regions = [];
    private PhotoFuseSession? _session;
    private PhotoFusePreview? _preview;
    private CancellationTokenSource? _operation;
    private bool _busy, _closeRequested, _updatingRectangle;
    private Point? _dragStart;
    private bool _reviewedAtActualPixels;
    private bool _actualReviewRequested;
    public string? ExportedPath { get; private set; }

    public PhotoFuseDialog(IEnumerable<string> paths)
    {
        InitializeComponent();
        _paths = paths.ToArray();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        await RunAsync(async token =>
        {
            _session = await Task.Run(() => PhotoFuseSession.PrepareAsync(_paths, token), token);
            foreach (var name in _session.SourceNames.Skip(1))
                DonorBox.Items.Add(name);
            FitPreviewToViewport();
            LeftBox.Maximum = _session.Width - 16;
            TopBox.Maximum = _session.Height - 16;
            WidthBox.Maximum = _session.Width;
            HeightBox.Maximum = _session.Height;
            DonorBox.SelectedIndex = 0;
            await ShowFrameAsync(token);
            ShowStatus(PhotoFuseStrings.Format("FuseReady", _session.SourceNames[0], _session.Width, _session.Height),
                InfoBarSeverity.Informational);
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        _busy = true;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        Workspace.IsEnabled = false;
        ConfirmBox.IsEnabled = false;
        IsPrimaryButtonEnabled = false;
        CloseButtonText = PhotoFuseStrings.Get("FuseCancel");
        try { await action(cancellation.Token); }
        catch (OperationCanceledException)
        {
            ShowStatus(PhotoFuseStrings.Get("FuseCanceled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
        finally
        {
            _busy = false;
            _operation = null;
            Workspace.IsEnabled = _session is not null;
            ConfirmBox.IsEnabled = _preview is not null && _reviewedAtActualPixels;
            UpdateSave();
            CloseButtonText = PhotoFuseStrings.Get("FuseClose");
        }
        if (_closeRequested) Hide();
    }

    private async void OnDonorChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_session is null || _busy) return;
        DisplayBox.SelectedIndex = 0;
        await RunAsync(ShowFrameAsync);
    }

    private async void OnDisplayChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_session is null || _busy) return;
        await RunAsync(ShowFrameAsync);
    }

    private async Task ShowFrameAsync(CancellationToken token)
    {
        if (_session is null) return;
        byte[] pixels;
        if (DisplayBox.SelectedIndex == 2)
        {
            if (_preview is null)
                throw new InvalidOperationException(PhotoFuseStrings.Get("FusePreviewRequired"));
            pixels = _preview.CopyPixels();
        }
        else
        {
            int frame = DisplayBox.SelectedIndex == 1 ? 0 : DonorBox.SelectedIndex + 1;
            pixels = await _session.GetAlignedFrameAsync(frame, token);
        }
        var bitmap = new WriteableBitmap(_session.Width, _session.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
            await stream.WriteAsync(pixels, token);
        token.ThrowIfCancellationRequested();
        PreviewImage.Source = bitmap;
        UpdateRectangle();
    }

    private void OnRectangleChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingRectangle || SelectionRectangle is null) return;
        InvalidateReview();
        UpdateRectangle();
    }

    private PhotoFuseRegion CurrentRegion()
    {
        double[] values = [LeftBox.Value, TopBox.Value, WidthBox.Value, HeightBox.Value];
        if (values.Any(value => !double.IsFinite(value) || value != Math.Round(value)))
            throw new ArgumentException(PhotoFuseStrings.Get("FuseWholePixelsRequired"));
        return new(DonorBox.SelectedIndex + 1, (int)values[0], (int)values[1], (int)values[2], (int)values[3]);
    }

    private void UpdateRectangle()
    {
        SelectionRectangle.Visibility = DisplayBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_session is null) return;
        double scale = PreviewSurface.Width / _session.Width;
        Canvas.SetLeft(SelectionRectangle, double.IsFinite(LeftBox.Value) ? Math.Max(0, LeftBox.Value * scale) : 0);
        Canvas.SetTop(SelectionRectangle, double.IsFinite(TopBox.Value) ? Math.Max(0, TopBox.Value * scale) : 0);
        SelectionRectangle.Width = double.IsFinite(WidthBox.Value) ? Math.Max(0, WidthBox.Value * scale) : 0;
        SelectionRectangle.Height = double.IsFinite(HeightBox.Value) ? Math.Max(0, HeightBox.Value * scale) : 0;
    }

    private async void OnAddRegion(object sender, RoutedEventArgs args)
    {
        await RunAsync(async token =>
        {
            if (_session is null) return;
            var candidate = CurrentRegion();
            var next = _regions.Append(candidate).ToArray();
            // Validate geometry and seams before accepting a region into the edit list.
            await _session.PreviewAsync(next, token);
            _regions.Add(candidate);
            RegionsList.Items.Add($"{_session.SourceNames[candidate.Frame]}: " +
                $"{candidate.Left},{candidate.Top} {candidate.Width} x {candidate.Height}");
            InvalidateReview();
            ShowStatus(PhotoFuseStrings.Get("FuseRegionAdded"), InfoBarSeverity.Informational);
        });
    }

    private void OnRemoveRegion(object sender, RoutedEventArgs args)
    {
        int index = RegionsList.SelectedIndex;
        if (index < 0) return;
        _regions.RemoveAt(index);
        RegionsList.Items.RemoveAt(index);
        InvalidateReview();
    }

    private async void OnBuildPreview(object sender, RoutedEventArgs args)
    {
        await RunAsync(async token =>
        {
            if (_session is null) return;
            InvalidateReview();
            _preview = await _session.PreviewAsync(_regions, token);
            DisplayBox.SelectedIndex = 2;
            await ShowFrameAsync(token);
            ShowStatus(PhotoFuseStrings.Get("FuseReviewRequired"), InfoBarSeverity.Warning);
        });
    }

    private void InvalidateReview()
    {
        _preview = null;
        _reviewedAtActualPixels = false;
        _actualReviewRequested = false;
        if (ConfirmBox is null) return;
        ConfirmBox.IsChecked = false;
        ConfirmBox.IsEnabled = false;
        IsPrimaryButtonEnabled = false;
    }

    private void OnConfirmationChanged(object sender, RoutedEventArgs args) => UpdateSave();

    private void UpdateSave() => IsPrimaryButtonEnabled =
        !_busy && _preview is not null && _reviewedAtActualPixels && ConfirmBox.IsChecked == true;

    private async void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_session is null || _preview is null || ConfirmBox.IsChecked != true || !_reviewedAtActualPixels) return;
        var accepted = _preview;
        await RunAsync(async token =>
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(
                XamlRoot.ContentIslandEnvironment.AppWindowId);
            var folder = await picker.PickSingleFolderAsync();
            token.ThrowIfCancellationRequested();
            if (folder is null) return;
            ExportedPath = await _session.ExportAsync(accepted, folder.Path, confirmed: true, token);
        });
        if (ExportedPath is not null) Hide();
    }

    private void OnFit(object sender, RoutedEventArgs args) => PreviewScroll.ChangeView(0, 0, 1);

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs args) => FitPreviewToViewport();

    private void FitPreviewToViewport()
    {
        if (_session is null || PreviewScroll.ActualWidth <= 0 || PreviewScroll.ActualHeight <= 0) return;
        double fit = Math.Min(1 / XamlRoot.RasterizationScale,
            Math.Min(PreviewScroll.ActualWidth / _session.Width, PreviewScroll.ActualHeight / _session.Height));
        PreviewSurface.Width = _session.Width * fit;
        PreviewSurface.Height = _session.Height * fit;
        PreviewScroll.MaxZoomFactor = Math.Max(8, ActualPixelZoom);
        PreviewScroll.ChangeView(0, 0, 1, disableAnimation: true);
        UpdateRectangle();
    }

    private void OnActualPixels(object sender, RoutedEventArgs args)
    {
        if (_session is null) return;
        _actualReviewRequested = _preview is not null && DisplayBox.SelectedIndex == 2;
        PreviewScroll.ChangeView(null, null, ActualPixelZoom, disableAnimation: true);
        MarkActualReview();
    }

    private float ActualPixelZoom => _session is null || PreviewSurface.Width <= 0 ? 1 :
        (float)Math.Max(1, _session.Width / (PreviewSurface.Width * XamlRoot.RasterizationScale));

    private void OnPreviewViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
    {
        if (!args.IsIntermediate) MarkActualReview();
    }

    private void MarkActualReview()
    {
        if (!_actualReviewRequested || Math.Abs(PreviewScroll.ZoomFactor - ActualPixelZoom) > 0.01) return;
        _actualReviewRequested = false;
        _reviewedAtActualPixels = true;
        ConfirmBox.IsEnabled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_session is null || _busy || DisplayBox.SelectedIndex != 0 ||
            !args.GetCurrentPoint(PreviewSurface).Properties.IsLeftButtonPressed) return;
        _dragStart = args.GetCurrentPoint(PreviewSurface).Position;
        PreviewSurface.CapturePointer(args.Pointer);
        args.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_dragStart is not { } start || _session is null) return;
        var end = args.GetCurrentPoint(PreviewSurface).Position;
        double scale = _session.Width / PreviewSurface.Width;
        _updatingRectangle = true;
        LeftBox.Value = Math.Clamp(Math.Floor(Math.Min(start.X, end.X) * scale), 0, _session.Width - 16);
        TopBox.Value = Math.Clamp(Math.Floor(Math.Min(start.Y, end.Y) * scale), 0, _session.Height - 16);
        WidthBox.Value = Math.Clamp(Math.Round(Math.Abs(end.X - start.X) * scale), 16, _session.Width - LeftBox.Value);
        HeightBox.Value = Math.Clamp(Math.Round(Math.Abs(end.Y - start.Y) * scale), 16, _session.Height - TopBox.Value);
        _updatingRectangle = false;
        InvalidateReview();
        UpdateRectangle();
        args.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        _dragStart = null;
        PreviewSurface.ReleasePointerCaptures();
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs args) => OnPointerReleased(sender, args);

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!_busy) return;
        args.Cancel = true;
        _closeRequested = true;
        _operation?.Cancel();
        ShowStatus(PhotoFuseStrings.Get("FuseCanceling"), InfoBarSeverity.Informational);
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _session?.Dispose();
        _session = null;
        _preview = null;
        PreviewImage.Source = null;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        ResultBar.Message = message;
        ResultBar.Severity = severity;
        ResultBar.IsOpen = true;
    }
}
