// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Xml.Linq;
using PhotoLibrarian.Views;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class MotionPhotoViewerTests
{
    [Theory]
    [InlineData(0, 1920, 1080, false)]
    [InlineData(-1, 1920, 1080, false)]
    [InlineData(1, 1, 1, true)]
    [InlineData(30000, 4096, 4096, true)]
    [InlineData(30001, 1920, 1080, false)]
    [InlineData(1000, 4097, 1080, false)]
    [InlineData(1000, 1920, 4097, false)]
    [InlineData(1000, 0, 1080, false)]
    [InlineData(1000, 1920, 0, false)]
    public void PlaybackSafetyLimitsAreInclusiveAtTheDocumentedBoundary(
        int milliseconds, uint width, uint height, bool supported)
    {
        Assert.Equal(supported, ImageViewerOverlay.IsSupportedMotionMedia(
            TimeSpan.FromMilliseconds(milliseconds), width, height));
    }

    [Fact]
    public void MotionControlsAreSeparateAccessibleAndLocalized()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var controls = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "ImageViewerOverlay.xaml"))
            .Descendants().ToList();
        var player = controls.Single(element => (string?)element.Attribute(x + "Name") == "MotionPlayer");
        Assert.Equal("False", (string?)player.Attribute("AutoPlay"));
        Assert.Equal("False", (string?)player.Attribute("AreTransportControlsEnabled"));
        Assert.Equal("ViewerMotionPlayer", (string?)player.Attribute("AutomationProperties.AutomationId"));
        var button = controls.Single(element => (string?)element.Attribute(x + "Name") == "MotionPhotoButton");
        Assert.Equal("ViewerMotionPhoto", (string?)button.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal("OnMotionPhotoClick", (string?)button.Attribute("Click"));
        Assert.Equal("Collapsed", (string?)button.Attribute("Visibility"));
        Assert.Contains(button.Ancestors(), element => (string?)element.Attribute(x + "Name") == "TopBar");
        Assert.Single(button.Elements(), element => element.Name.LocalName == "FontIcon");
        Assert.DoesNotContain(controls, element => (string?)element.Attribute(x + "Name") == "MotionPhotoTools");
        var rootGrid = controls.Single(element => (string?)element.Attribute(x + "Name") == "RootGrid");
        Assert.Equal(3, rootGrid.Elements().Single(element => element.Name.LocalName == "Grid.RowDefinitions").Elements().Count());
        Assert.Equal("1", (string?)player.Attribute("Grid.Row"));
        var fuseStatus = controls.Single(element => (string?)element.Attribute(x + "Name") == "FuseStatusBar");
        Assert.Equal("2", (string?)fuseStatus.Attribute("Grid.Row"));
        var standalone = controls.Single(element => (string?)element.Attribute(x + "Name") == "VideoPlayer");
        Assert.Equal("True", (string?)standalone.Attribute("AutoPlay"));
        var resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "Resources.resw"));
        foreach (string key in new[] { "MotionPhotoPlay", "MotionPhotoPause", "MotionPhotoUnavailable",
            "MotionPhotoTimeout", "MotionPhotoMediaLimit", "MotionPhotoCodecUnavailable",
            "AppleLivePhotoMissingMovie", "AppleLivePhotoAmbiguous", "AppleLivePhotoMissingTiming",
            "AppleLivePhotoSearchLimit", "AppleLivePhotoUnsupportedStillFormat", "AppleLivePhotoUnavailable",
            "MotionPhotoPlayer.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name" })
        {
            Assert.Contains(resources.Descendants("data"), element =>
                (string?)element.Attribute("name") == key && !string.IsNullOrWhiteSpace(element.Element("value")?.Value));
        }
    }

    [Theory]
    [InlineData(0, 0.5)]
    [InlineData(1, 0.75)]
    [InlineData(2, 1.0)]
    [InlineData(3, 1.25)]
    [InlineData(4, 1.5)]
    [InlineData(5, 2.0)]
    public void VideoPlaybackRateSelectionMapsToSupportedRates(int selectedIndex, double expectedRate)
    {
        Assert.True(ImageViewerOverlay.TryGetVideoPlaybackRate(selectedIndex, out var playbackRate));
        Assert.Equal(expectedRate, playbackRate);
    }

    [Fact]
    public void DefaultVideoPlaybackRateAndInvalidSelectionFallbackAreOneTimesSpeed()
    {
        Assert.Equal(2, ImageViewerOverlay.DefaultVideoPlaybackRateIndex);
        Assert.True(ImageViewerOverlay.TryGetVideoPlaybackRate(
            ImageViewerOverlay.DefaultVideoPlaybackRateIndex, out var playbackRate));
        Assert.Equal(1.0, playbackRate);

        Assert.False(ImageViewerOverlay.TryGetVideoPlaybackRate(-1, out var invalidRate));
        Assert.Equal(1.0, invalidRate);
        Assert.False(ImageViewerOverlay.TryGetVideoPlaybackRate(6, out invalidRate));
        Assert.Equal(1.0, invalidRate);
    }

    [Fact]
    public void VideoPlaybackSpeedControlIsAccessibleAndLocalized()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var controls = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "ImageViewerOverlay.xaml"))
            .Descendants().ToList();
        var speed = controls.Single(element => (string?)element.Attribute(x + "Name") == "VideoPlaybackSpeed");
        Assert.Equal("2", (string?)speed.Attribute("SelectedIndex"));
        Assert.Equal("Collapsed", (string?)speed.Attribute("Visibility"));
        Assert.Equal("OnVideoPlaybackSpeedChanged", (string?)speed.Attribute("SelectionChanged"));
        Assert.Equal(new[]
        {
            "ViewerPlaybackSpeedHalf",
            "ViewerPlaybackSpeedThreeQuarter",
            "ViewerPlaybackSpeedNormal",
            "ViewerPlaybackSpeedFiveQuarter",
            "ViewerPlaybackSpeedOneAndHalf",
            "ViewerPlaybackSpeedDouble"
        }, speed.Elements()
            .Where(element => element.Name.LocalName == "ComboBoxItem")
            .Select(element => (string?)element.Attribute(x + "Uid")));

        var resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "Resources.resw"));
        foreach (string key in new[]
        {
            "ViewerPlaybackSpeed.Header",
            "ViewerPlaybackSpeed.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
            "ViewerPlaybackSpeedHalf.Content",
            "ViewerPlaybackSpeedThreeQuarter.Content",
            "ViewerPlaybackSpeedNormal.Content",
            "ViewerPlaybackSpeedFiveQuarter.Content",
            "ViewerPlaybackSpeedOneAndHalf.Content",
            "ViewerPlaybackSpeedDouble.Content"
        })
        {
            Assert.Contains(resources.Descendants("data"), element =>
                (string?)element.Attribute("name") == key && !string.IsNullOrWhiteSpace(element.Element("value")?.Value));
        }
    }
}
