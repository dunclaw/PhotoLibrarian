// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Globalization;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class MapLinkTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"PhotoLibrarian-Map-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(MapProvider.Google, "https://www.google.com/maps/search/?api=1&query=47.123456789,-122.25")]
    [InlineData(MapProvider.Bing, "https://www.bing.com/maps?cp=47.123456789~-122.25&lvl=16")]
    public void LinksUseInvariantFullPrecisionCoordinates(MapProvider provider, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.True(MapLinkService.TryCreateUri(provider, 47.123456789, -122.25, out var uri));
            Assert.Equal(expected, uri!.AbsoluteUri);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(null, 0d)]
    [InlineData(0d, null)]
    [InlineData(90.1, 0d)]
    [InlineData(0d, -180.1)]
    [InlineData(double.NaN, 0d)]
    [InlineData(0d, double.PositiveInfinity)]
    public void InvalidCoordinatesDoNotCreateLinks(double? latitude, double? longitude)
    {
        Assert.False(MapLinkService.TryCreateUri(MapProvider.Google, latitude, longitude, out var uri));
        Assert.Null(uri);
    }

    [Fact]
    public void BoundsAndUnknownProviderAreHandled()
    {
        Assert.True(MapLinkService.TryCreateUri(MapProvider.Google, -90, 180, out _));
        Assert.False(MapLinkService.TryCreateUri((MapProvider)42, 0, 0, out _));
    }

    [Fact]
    public void SelectedCoordinatesMustMatchExactly()
    {
        var panel = new MetadataPanelViewModel();
        var first = new ImageEntry { FilePath = "first.jpg", FileName = "first.jpg", GpsLatitude = 12.12345671, GpsLongitude = 1 };
        var second = new ImageEntry { FilePath = "second.jpg", FileName = "second.jpg", GpsLatitude = 12.12345672, GpsLongitude = 1 };
        panel.ShowMetadata([first, second]);
        Assert.False(panel.TryGetMapCoordinates(out _, out _));

        second.GpsLatitude = first.GpsLatitude;
        panel.ShowMetadata([first, second]);
        Assert.True(panel.TryGetMapCoordinates(out var latitude, out var longitude));
        Assert.Equal(first.GpsLatitude, latitude);
        Assert.Equal(first.GpsLongitude, longitude);

        second.GpsLongitude = null;
        panel.ShowMetadata([first, second]);
        Assert.False(panel.TryGetMapCoordinates(out _, out _));
    }

    [Fact]
    public void ProviderPersistsAndInvalidPreferencesFallBack()
    {
        var path = Path.Combine(_directory, "map-settings.json");
        var store = new MapSettingsStore(path);
        Assert.Equal(MapProvider.Google, store.Load());
        store.Save(MapProvider.Bing);
        Assert.Equal(MapProvider.Bing, new MapSettingsStore(path).Load());
        File.WriteAllText(path, """{"Provider":999}""");
        Assert.Equal(MapProvider.Google, store.Load());
        File.WriteAllText(path, "not json");
        Assert.Equal(MapProvider.Google, store.Load());
    }

    [Fact]
    public void FailedSaveIsReportedAndTemporaryFileIsRemoved()
    {
        var path = Path.Combine(_directory, "map-settings.json");
        Directory.CreateDirectory(path);
        Assert.Throws<UnauthorizedAccessException>(() => new MapSettingsStore(path).Save(MapProvider.Bing));
        Assert.False(File.Exists(path + ".tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
