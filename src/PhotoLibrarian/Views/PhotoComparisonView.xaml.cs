// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Diagnostics;
using PhotoLibrarian.Services;
using Windows.System;

namespace PhotoLibrarian.Views;

public sealed partial class PhotoComparisonView : UserControl
{
    private PhotoComparisonSession _session = new([]);
    private readonly List<PhotoComparisonPane> _panes = [];
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private readonly Dictionary<PhotoComparisonPane, CancellationTokenSource> _requests = [];
    private readonly Func<string, CancellationToken, Task<BitmapImage>> _load;
    private ComparisonViewport _view = ComparisonViewport.Fit;
    internal bool IsOpen { get; private set; }
    internal event EventHandler? Closed;

    public PhotoComparisonView() : this(ViewerImageLoader.LoadPhotoAsync) { }

    internal PhotoComparisonView(Func<string, CancellationToken, Task<BitmapImage>> load)
    {
        InitializeComponent();
        _load = load;
        Unloaded += (_, _) => Close();
    }

    internal void Open(IReadOnlyList<ImageEntry> entries)
    {
        ReleaseCandidates();
        _session = new PhotoComparisonSession(entries);
        _view = ComparisonViewport.Fit;
        _lifetime = new CancellationTokenSource();
        IsOpen = true;
        RestoreCandidates();
    }

    internal void FocusWorkspace()
    {
        if (FitButton.Focus(FocusState.Programmatic)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsOpen) FitButton.Focus(FocusState.Programmatic);
        });
    }

    private void RestoreCandidates()
    {
        foreach (var pane in _panes) ReleasePane(pane);
        _panes.Clear();
        PanesGrid.Children.Clear();
        _session.Restore();
        foreach (var entry in _session.Candidates)
        {
            var pane = new PhotoComparisonPane(entry);
            pane.RemoveRequested += RemovePane;
            pane.RetryRequested += RetryPane;
            pane.ViewChanged += Synchronize;
            pane.ApplyView(_view);
            _panes.Add(pane);
            PanesGrid.Children.Add(pane);
            _ = LoadPaneAsync(pane);
        }
        LayoutPanes();
    }

    private async Task LoadPaneAsync(PhotoComparisonPane pane)
    {
        if (!IsOpen || _lifetime is null || _requests.ContainsKey(pane)) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _requests.Add(pane, request);
        pane.SetLoading();
        try
        {
            await _loadGate.WaitAsync(request.Token);
            try
            {
                var image = await _load(pane.Entry.FilePath, request.Token);
                if (!request.IsCancellationRequested) pane.SetImage(image);
            }
            finally { _loadGate.Release(); }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"Compare load failed for '{pane.Entry.FilePath}': {ex}");
            if (!request.IsCancellationRequested) pane.SetError(ex.Message);
        }
        finally { _requests.Remove(pane); }
    }

    private void RetryPane(PhotoComparisonPane pane) => _ = LoadPaneAsync(pane);

    private void ReleasePane(PhotoComparisonPane pane)
    {
        if (_requests.TryGetValue(pane, out var request)) request.Cancel();
        pane.Release();
        pane.RemoveRequested -= RemovePane;
        pane.RetryRequested -= RetryPane;
        pane.ViewChanged -= Synchronize;
    }

    private void RemovePane(PhotoComparisonPane pane)
    {
        if (!_session.Remove(pane.Entry)) return;
        ReleasePane(pane);
        _panes.Remove(pane);
        PanesGrid.Children.Remove(pane);
        LayoutPanes();
        RestoreButton.Focus(FocusState.Keyboard);
    }

    private void Synchronize(ComparisonViewport view)
    {
        _view = view;
        foreach (var pane in _panes) pane.ApplyView(view);
    }

    private void LayoutPanes()
    {
        int columns = _panes.Count <= 2 ? Math.Max(1, _panes.Count) :
            Math.Max(1, (int)Math.Ceiling(Math.Sqrt(_panes.Count *
                Math.Max(1, PanesGrid.ActualWidth) / Math.Max(1, PanesGrid.ActualHeight))));
        columns = Math.Min(Math.Max(1, _panes.Count), columns);
        PanesGrid.ColumnDefinitions.Clear();
        PanesGrid.RowDefinitions.Clear();
        for (int i = 0; i < columns; i++) PanesGrid.ColumnDefinitions.Add(new ColumnDefinition());
        int rows = Math.Max(1, (int)Math.Ceiling((double)_panes.Count / columns));
        for (int i = 0; i < rows; i++) PanesGrid.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < _panes.Count; i++)
        {
            Grid.SetRow(_panes[i], i / columns);
            Grid.SetColumn(_panes[i], i % columns);
        }
        RestoreButton.IsEnabled = _panes.Count < _session.OriginalCount;
        CountText.Text = ComparisonStrings.Format("ComparisonCount", _panes.Count, _session.OriginalCount);
    }

    private void OnPanesSizeChanged(object sender, SizeChangedEventArgs e) => LayoutPanes();
    private void OnFit(object sender, RoutedEventArgs e) => Synchronize(ComparisonViewport.Fit);
    private void OnActualSize(object sender, RoutedEventArgs e) =>
        Synchronize(new(1, 0.5, 0.5, IsActualSize: true));
    private void OnZoomIn(object sender, RoutedEventArgs e) => _panes.FirstOrDefault(pane => pane.IsReady)?.Zoom(true);
    private void OnZoomOut(object sender, RoutedEventArgs e) => _panes.FirstOrDefault(pane => pane.IsReady)?.Zoom(false);
    private void OnRestore(object sender, RoutedEventArgs e) => RestoreCandidates();
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        Close();
        e.Handled = true;
    }

    private void ReleaseCandidates()
    {
        _lifetime?.Cancel();
        foreach (var pane in _panes) ReleasePane(pane);
        _panes.Clear();
        PanesGrid.Children.Clear();
        _lifetime?.Dispose();
        _lifetime = null;
    }

    internal void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        ReleaseCandidates();
        _session = new([]);
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
