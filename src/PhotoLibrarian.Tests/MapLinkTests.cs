// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Globalization;
using System.Xml.Linq;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class MapLinkTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"PhotoLibrarian-Map-{Guid.NewGuid():N}");

    [Fact]
    public void MapActionsAreDirectlyAfterTheGeotagEditor()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "MetadataPanel.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var editor = document.Descendants().Single(element => (string?)element.Attribute(xaml + "Name") == "GeotagCoordinatesBox");
        var link = editor.ElementsAfterSelf().First();

        Assert.Equal("HyperlinkButton", link.Name.LocalName);
        Assert.Equal("GeotagMapLink", (string?)link.Attribute(xaml + "Name"));
        Assert.Equal("GeotagMapLink", (string?)link.Attribute(xaml + "Uid"));
        Assert.Equal("OnMapCoordinatesClick", (string?)link.Attribute("Click"));
        var paste = link.ElementsAfterSelf().First();
        Assert.Equal("HyperlinkButton", paste.Name.LocalName);
        Assert.Equal("GeotagPasteMapLink", (string?)paste.Attribute(xaml + "Name"));
    }

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
    [InlineData(" https://www.google.com/maps/search/?api=1&query=47.123456789%2C+-122.25 ", 47.123456789, -122.25)]
    [InlineData("https://www.google.com/maps/place/Scenic+Point/@10,20,17z/data=!4m6!3m5!1s0x1:0x2!8m2!3d-33.8688!4d151.2093!16s%2Fg%2F123?entry=ttu", -33.8688, 151.2093)]
    [InlineData("https://bing.com/maps/default.aspx?cp=10~20&sp=point.0_-180_Test%20pin", 0, -180)]
    [InlineData("https://www.openstreetmap.org/?mlon=180&mlat=-90#map=12/10/20", -90, 180)]
    public void FullMapLinksParseTheExplicitPointInAnyCulture(string link, double latitude, double longitude)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            Assert.True(MapLinkService.TryParsePoint(link, out var point));
            Assert.Equal(new GeoCoordinate(latitude, longitude), point);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("https://www.google.com/maps/@47,-122,12z")]
    [InlineData("https://www.bing.com/maps?cp=47~-122&lvl=16")]
    [InlineData("https://www.openstreetmap.org/#map=12/47/-122")]
    [InlineData("https://maps.app.goo.gl/example")]
    [InlineData("https://binged.it/example")]
    [InlineData("https://www.openstreetmap.org/go/example")]
    [InlineData("https://www.google.com/maps/search/?api=1&query=Seattle")]
    [InlineData("https://www.google.com/maps/search/?api=1&query=47,-122&query_place_id=somewhere-else")]
    [InlineData("https://www.google.com/maps/search/?api=1&query=47,-122&%71uery=1,2")]
    [InlineData("https://www.google.com/maps/search/?api=1&query=91,-122")]
    [InlineData("https://www.google.com/maps/search/?api=1&query=NaN,-122")]
    [InlineData("https://www.google.com/maps/search/?api=1&query=47,-122%00")]
    [InlineData("https://www.google.com/maps/search/?api=1&query=47,-122%")]
    [InlineData("https://www.google.com/maps/place/Park/@47,-122,17z/data=!8m2!3d47!4d-122!8m2!3d48!4d-123")]
    [InlineData("https://www.google.com/maps/place/Park/@47,-122,17z/data=!8m2!3d47!4d")]
    [InlineData("https://www.google.com/maps/place/Park/@47,-122,17z/data=!1slabel%218m2%213d47%214d-122")]
    [InlineData("https://www.google.com/maps/dir/Start/End/data=!8m2!3d47!4d-122")]
    [InlineData("https://www.google.com/maps/place/Park/@47,-122,17z")]
    [InlineData("https://www.bing.com/maps?sp=point.47_-122_First~point.48_-123_Second")]
    [InlineData("https://www.bing.com/maps?sp=point.47_-122&rtp=pos.47_-122~pos.48_-123")]
    [InlineData("https://www.openstreetmap.org/?mlat=47#map=12/47/-122")]
    [InlineData("https://www.openstreetmap.org/?mlat=47&mlon=-122&mlat=48")]
    [InlineData("https://www.openstreetmap.org/directions?mlat=47&mlon=-122")]
    [InlineData("https://www.google.com.example/maps/search/?api=1&query=47,-122")]
    [InlineData("https://example@www.google.com/maps/search/?api=1&query=47,-122")]
    [InlineData("http://www.google.com/maps/search/?api=1&query=47,-122")]
    public void NonPointAndAmbiguousLinksAreRejected(string link)
    {
        Assert.False(MapLinkService.TryParsePoint(link, out _));
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
    public void CorruptPreferences_RecoverPreviousProviderFromBackup()
    {
        var path = Path.Combine(_directory, "map-settings.json");
        var store = new MapSettingsStore(path);
        store.Save(MapProvider.Bing);
        Assert.Equal("""{"Provider":1}""", File.ReadAllText(path));
        store.Save(MapProvider.Google);
        File.WriteAllText(path, "corrupt");

        Assert.Equal(MapProvider.Bing, store.Load());
        store.Save(MapProvider.Bing);
        Assert.Equal(MapProvider.Bing, store.Load());
        Assert.Equal("""{"Provider":1}""", File.ReadAllText(path + ".bak"));
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
