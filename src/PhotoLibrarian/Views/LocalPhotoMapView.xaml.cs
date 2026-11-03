// Copyright (c) Duncan Lawler. All Rights Reserved.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;

namespace PhotoLibrarian.Views;

public sealed record PhotoMapGroup(PhotoMapCluster Cluster, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class LocalPhotoMapView : UserControl
{
    private ImageEntry[] _entries = [];
    private PhotoMap? _map;
    private PhotoMapViewport? _viewport;
    private CancellationTokenSource? _renderCts;
    private HashSet<string>? _selectedPaths;
    private bool _updatingList;
    private bool _active;
    private int _visibleCount;
    private int _groupCount;

    public event EventHandler<IReadOnlyList<string>?>? FilterRequested;

    public LocalPhotoMapView()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => { if (_active) _ = RenderAsync(); };
    }

    public void Refresh(IEnumerable<ImageEntry> entries)
    {
        _active = true;
        // Copy metadata: an edit or watcher refresh must not mutate a worker's snapshot.
        var snapshot = entries.Select(entry => new ImageEntry
        {
            FilePath = entry.FilePath,
            FileName = entry.FileName,
            GpsLatitude = entry.GpsLatitude,
            GpsLongitude = entry.GpsLongitude
        }).ToArray();
        bool unchanged = snapshot.Length == _entries.Length &&
            snapshot.Zip(_entries).All(pair =>
                pair.First.FilePath == pair.Second.FilePath &&
                Nullable.Equals(pair.First.GpsLatitude, pair.Second.GpsLatitude) &&
                Nullable.Equals(pair.First.GpsLongitude, pair.Second.GpsLongitude));
        if (unchanged && _map is not null)
            return;
        _entries = snapshot;
        _map = null;
        _viewport = null;
        MapCanvas.Children.Clear();
        _updatingList = true;
        GroupList.ItemsSource = null;
        _updatingList = false;
        _ = RenderAsync();
    }

    public void SetSelection(IEnumerable<string>? paths)
    {
        var selection = paths is null ? null : new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        if (selection is null && _selectedPaths is null ||
            selection is not null && _selectedPaths?.SetEquals(selection) == true)
            return;
        _selectedPaths = selection;
        ClearButton.IsEnabled = _selectedPaths is not null;
        UpdateSelectionVisuals();
    }

    public void Deactivate()
    {
        _active = false;
        _renderCts?.Cancel();
        _map = null;
        _entries = [];
        MapCanvas.Children.Clear();
        GroupList.ItemsSource = null;
        Summary.Text = "";
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Deactivate();

    private void OnMapSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_active)
            _ = RenderAsync();
    }

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (_viewport is null || _map is null || sender is not Button button)
            return;
        _viewport = (button.Tag as string) switch
        {
            "Fit" => _map.Bounds,
            "In" => _viewport.Zoom(0.5),
            "Out" => _viewport.Zoom(2),
            "West" => _viewport.Pan(-0.5, 0),
            "East" => _viewport.Pan(0.5, 0),
            "North" => _viewport.Pan(0, 0.5),
            "South" => _viewport.Pan(0, -0.5),
            _ => _viewport
        };
        _ = RenderAsync();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        SetSelection(null);
        FilterRequested?.Invoke(this, null);
    }

    private void OnGroupSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingList && GroupList.SelectedItem is PhotoMapGroup group)
            Select(group);
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: PhotoMapGroup group })
            Select(group);
    }

    private void Select(PhotoMapGroup group)
    {
        SetSelection(group.Cluster.Paths);
        FilterRequested?.Invoke(this, group.Cluster.Paths);
    }

    private async Task RenderAsync()
    {
        _renderCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _renderCts = cts;
        var ct = cts.Token;
        var entries = _entries;
        var map = _map;
        var viewport = _viewport;
        double width = MapCanvas.ActualWidth;
        double height = MapCanvas.ActualHeight;
        if (!_active || width <= 0 || height <= 0)
        {
            _renderCts = null;
            return;
        }
        try
        {
            var result = await Task.Run(() =>
            {
                var geometry = map ?? PhotoMap.Create(entries, ct);
                var bounds = viewport ?? geometry.Bounds;
                return (Map: geometry, Viewport: bounds, Clusters: geometry.Cluster(bounds, width, height, ct));
            }, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(_renderCts, cts) || !_active)
                return;
            _map = result.Map;
            _viewport = result.Viewport;
            ShowClusters(result.Clusters, width, height);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded scope/viewport or the map was closed.
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_renderCts, cts) || !_active)
                return;
            MapCanvas.Children.Clear();
            GroupList.ItemsSource = null;
            Summary.Text = PhotoMapStrings.Format("PhotoMapError", ex.Message);
            Diagnostics.DebugLog.WriteLine($"[PHOTO MAP] {ex}");
        }
        finally
        {
            if (ReferenceEquals(_renderCts, cts))
                _renderCts = null;
        }
    }

    private void ShowClusters(IReadOnlyList<PhotoMapCluster> clusters, double width, double height)
    {
        MapCanvas.Children.Clear();
        MapCanvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, height) };
        var lineBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
        for (int i = 1; i < 4; i++)
        {
            MapCanvas.Children.Add(new Line
            {
                X1 = width * i / 4, X2 = width * i / 4, Y1 = 0, Y2 = height,
                Stroke = lineBrush, IsHitTestVisible = false
            });
            MapCanvas.Children.Add(new Line
            {
                X1 = 0, X2 = width, Y1 = height * i / 4, Y2 = height * i / 4,
                Stroke = lineBrush, IsHitTestVisible = false
            });
        }
        var groups = new List<PhotoMapGroup>();
        foreach (var cluster in clusters)
        {
            string label = PhotoMapStrings.Format("PhotoMapGroup", cluster.Paths.Count,
                cluster.Latitude, cluster.Longitude);
            var group = new PhotoMapGroup(cluster, label);
            groups.Add(group);
            var button = new ToggleButton
            {
                Content = cluster.Paths.Count.ToString("N0"),
                Tag = group,
                Width = 64,
                Height = 32,
                Padding = new Thickness(4),
                IsChecked = _selectedPaths?.SetEquals(cluster.Paths) == true
            };
            AutomationProperties.SetAutomationId(button, $"PhotoMapPin{groups.Count}");
            AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, label);
            button.Click += OnPinClick;
            Canvas.SetLeft(button, Math.Clamp(cluster.X * width - 32, 0, Math.Max(0, width - 64)));
            Canvas.SetTop(button, Math.Clamp(cluster.Y * height - 16, 0, Math.Max(0, height - 32)));
            MapCanvas.Children.Add(button);
        }
        _updatingList = true;
        GroupList.ItemsSource = groups;
        _updatingList = false;
        _visibleCount = clusters.Sum(cluster => cluster.Paths.Count);
        _groupCount = clusters.Count;
        UpdateSelectionVisuals();
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var button in MapCanvas.Children.OfType<ToggleButton>())
        {
            if (button.Tag is PhotoMapGroup group)
                button.IsChecked = _selectedPaths?.SetEquals(group.Cluster.Paths) == true;
        }
        _updatingList = true;
        GroupList.SelectedItem = GroupList.Items.OfType<PhotoMapGroup>()
            .FirstOrDefault(group => _selectedPaths?.SetEquals(group.Cluster.Paths) == true);
        _updatingList = false;
        if (_map is null || _viewport is null)
            return;
        int selected = _selectedPaths is null ? 0 : _map!.Points.Count(point => _selectedPaths.Contains(point.FilePath));
        Summary.Text = PhotoMapStrings.Format("PhotoMapCounts", _map!.Points.Count, _map.ExcludedCount,
            _visibleCount, _groupCount, selected) + " " +
            PhotoMapStrings.Format("PhotoMapBounds", _viewport!.South, _viewport.North,
                PhotoMap.NormalizeLongitude(_viewport.West), PhotoMap.NormalizeLongitude(_viewport.East));
    }
}
