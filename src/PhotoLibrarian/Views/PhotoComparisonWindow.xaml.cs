// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Diagnostics;
using PhotoLibrarian.Services;
using Windows.Graphics;
using Windows.System;

namespace PhotoLibrarian.Views;

public sealed partial class PhotoComparisonWindow : Window
{
    private readonly PhotoComparisonSession _session;
    private readonly List<PhotoComparisonPane> _panes = [];
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<PhotoComparisonPane, CancellationTokenSource> _requests = [];
    private readonly Func<string, CancellationToken, Task<BitmapImage>> _load;
    private ComparisonViewport _view = ComparisonViewport.Fit;
    private bool _closed;

    internal PhotoComparisonWindow(IReadOnlyList<ImageEntry> entries, bool survey,
        Func<string, CancellationToken, Task<BitmapImage>>? load = null)
    {
        InitializeComponent();
        _session = new PhotoComparisonSession(entries);
        _load = load ?? ViewerImageLoader.LoadPhotoAsync;
        Title = ComparisonStrings.Get(survey ? "ComparisonSurveyTitle" : "ComparisonTitle");
        var dpi = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(dpi) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1280 * scale), (int)(840 * scale)));
        Closed += OnClosed;
        RestoreCandidates();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

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
        if (_closed || _requests.ContainsKey(pane)) return;
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

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _lifetime.Cancel();
        foreach (var pane in _panes) ReleasePane(pane);
        _panes.Clear();
        PanesGrid.Children.Clear();
        _lifetime.Dispose();
    }
}
