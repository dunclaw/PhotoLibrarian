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
        Assert.Equal(2, rootGrid.Elements().Single(element => element.Name.LocalName == "Grid.RowDefinitions").Elements().Count());
        var standalone = controls.Single(element => (string?)element.Attribute(x + "Name") == "VideoPlayer");
        Assert.Equal("True", (string?)standalone.Attribute("AutoPlay"));
        var resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "Resources.resw"));
        foreach (string key in new[] { "MotionPhotoPlay", "MotionPhotoPause", "MotionPhotoUnavailable",
            "MotionPhotoTimeout", "MotionPhotoMediaLimit", "MotionPhotoCodecUnavailable",
            "MotionPhotoPlayer.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name" })
        {
            Assert.Contains(resources.Descendants("data"), element =>
                (string?)element.Attribute("name") == key && !string.IsNullOrWhiteSpace(element.Element("value")?.Value));
        }
    }
}
