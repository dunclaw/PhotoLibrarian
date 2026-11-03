// Copyright (c) Duncan Lawler. All Rights Reserved.

using PhotoLibrarian.Core.Models;
using System.Xml.Linq;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PhotoMapTests
{
    private static ImageEntry Entry(int id, double? latitude, double? longitude) => new()
    {
        Id = id, FilePath = $@"C:\SyntheticPhotos\{id}.jpg", FileName = $"{id}.jpg",
        GpsLatitude = latitude, GpsLongitude = longitude
    };

    [Fact]
    public void MissingIncompleteNonFiniteAndOutOfRangeAreExcludedButZeroAndPolesAreValid()
    {
        var map = PhotoMap.Create([
            Entry(1, null, null), Entry(2, 1, null), Entry(3, null, 1),
            Entry(4, double.NaN, 1), Entry(5, 1, double.PositiveInfinity),
            Entry(6, -91, 1), Entry(7, 1, 181),
            Entry(8, 0, 0), Entry(9, -90, -180), Entry(10, 90, 180)
        ]);
        Assert.Equal(7, map.ExcludedCount);
        Assert.Equal(3, map.Points.Count);
        var clusters = map.Cluster(map.Bounds, 800, 200);
        Assert.Equal(3, clusters.Sum(cluster => cluster.Paths.Count));
        Assert.All(clusters, cluster =>
        {
            Assert.InRange(cluster.X, 0, 1);
            Assert.InRange(cluster.Y, 0, 1);
            Assert.InRange(cluster.Latitude, -90, 90);
        });
    }

    [Fact]
    public void FitUsesShortestLongitudeArcAcrossAntimeridian()
    {
        var map = PhotoMap.Create([Entry(1, 85, 179.9), Entry(2, 89.9, -179.9)]);
        Assert.InRange(map.Bounds.Width, 0.2, 0.23);
        Assert.InRange(map.Bounds.North, 89.9, 90);
        var clusters = map.Cluster(map.Bounds, 800, 200);
        Assert.Equal(2, clusters.Sum(cluster => cluster.Paths.Count));
        Assert.All(clusters, cluster => Assert.InRange(Math.Abs(cluster.Longitude), 179.899999, 180));
    }

    [Fact]
    public void ExactViewportEdgesSurviveLongitudeNormalizationRounding()
    {
        var map = PhotoMap.Create([Entry(1, 89, 179.9), Entry(2, 90, -179.9)]);
        var clusters = map.Cluster(new(179.9, 89, 0.2, 1), 800, 200);
        Assert.Equal(2, clusters.Sum(cluster => cluster.Paths.Count));
        Assert.All(clusters, cluster =>
        {
            Assert.InRange(cluster.X, 0, 1);
            Assert.InRange(cluster.Y, 0, 1);
        });
    }

    [Fact]
    public void EquivalentDatelineCoordinatesShareOnePin()
    {
        var map = PhotoMap.Create([Entry(1, 12, 180), Entry(2, 12, -180)]);
        var cluster = Assert.Single(map.Cluster(map.Bounds, 800, 200));
        Assert.Equal(2, cluster.Paths.Count);
        Assert.Equal(-180, cluster.Longitude, 8);
    }

    [Fact]
    public void ClusteringIsBoundedDeterministicAndPreservesEveryVisiblePath()
    {
        var entries = Enumerable.Range(0, 100_000)
            .Select(i => Entry(i, -89 + (i % 179), -179 + (i % 359))).ToArray();
        var map = PhotoMap.Create(entries);
        var clusters = map.Cluster(map.Bounds, 10_000, 10_000);
        Assert.InRange(clusters.Count, 1, 128);
        Assert.Equal(entries.Length, clusters.Sum(cluster => cluster.Paths.Count));
        Assert.Equal(entries.Length, clusters.SelectMany(cluster => cluster.Paths).Distinct().Count());
        var repeated = map.Cluster(map.Bounds, 10_000, 10_000);
        Assert.Equal(clusters.SelectMany(cluster => cluster.Paths), repeated.SelectMany(cluster => cluster.Paths));
    }

    [Fact]
    public void ViewportCullsAndZoomSeparatesWithoutLosingPolarOrDatelinePoints()
    {
        var map = PhotoMap.Create([Entry(1, 90, 179.8), Entry(2, 90, -179.8), Entry(3, 0, 0)]);
        var viewport = new PhotoMapViewport(179, 89, 2, 1);
        Assert.Equal(2, map.Cluster(viewport, 800, 200).Sum(cluster => cluster.Paths.Count));
        Assert.Empty(map.Cluster(viewport.Pan(2, 0), 800, 200));
        Assert.InRange(viewport.Zoom(0.5).North, 89.5, 90);
        Assert.Equal(-90, viewport.Pan(0, -1000).South);
        Assert.Equal(360, viewport.Zoom(1000).Width);
    }

    [Fact]
    public void EmptyAndSinglePointMapsHaveFiniteNonzeroBounds()
    {
        foreach (var entries in new[] { Array.Empty<ImageEntry>(), new[] { Entry(1, -90, -180) } })
        {
            var map = PhotoMap.Create(entries);
            Assert.True(map.Bounds.Width > 0 && map.Bounds.Height > 0);
            Assert.Equal(entries.Length, map.Cluster(map.Bounds, 800, 200).Sum(cluster => cluster.Paths.Count));
        }
    }

    [Fact]
    public void CreateAndClusterObserveCancellationAndRejectInvalidViewports()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => PhotoMap.Create([Entry(1, 1, 1)], cts.Token));
        var map = PhotoMap.Create([Entry(1, 1, 1)]);
        Assert.Throws<OperationCanceledException>(() => map.Cluster(map.Bounds, 800, 200, cts.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.Cluster(map.Bounds, 0, 200));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.Cluster(new(0, 0, double.NaN, 1), 800, 200));
    }

    [Fact]
    public void MapUiHasLocalizedKeyboardAlternativeSemanticButtonsAndNoOnlineSurface()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "TestAssets");
        var xaml = XDocument.Load(Path.Combine(root, "LocalPhotoMapView.xaml"));
        var resources = XDocument.Load(Path.Combine(root, "Resources.resw"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var list = Assert.Single(xaml.Descendants().Where(element => element.Name.LocalName == "ListView"));
        Assert.Equal("PhotoMapGroups", (string?)list.Attribute("AutomationProperties.AutomationId"));
        Assert.NotNull(list.Attribute("SelectionChanged"));
        Assert.Contains(resources.Descendants("data"), element =>
            (string?)element.Attribute("name") == "PhotoMapGroups.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.HelpText");
        Assert.All(xaml.Descendants().Where(element => element.Name.LocalName == "Button"), button =>
        {
            Assert.NotNull(button.Attribute("AutomationProperties.AutomationId"));
            string uid = (string)button.Attribute(x + "Uid")!;
            Assert.Contains(resources.Descendants("data"), element => (string?)element.Attribute("name") == uid + ".Content");
        });
        foreach (string file in new[] { "PhotoMap.cs", "LocalPhotoMapView.xaml", "LocalPhotoMapView.xaml.cs" })
        {
            string source = File.ReadAllText(Path.Combine(root, file));
            foreach (string forbidden in new[] { "HttpClient", "WebView", "MapControl", "MapServiceToken",
                "System.Net", "File.Write", "Process.Start", "Launcher.", "https:" })
                Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        }
    }
}
