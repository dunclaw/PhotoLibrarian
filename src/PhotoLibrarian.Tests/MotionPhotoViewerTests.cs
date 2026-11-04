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
        Assert.Equal("ViewerMotionPlayer", (string?)player.Attribute("AutomationProperties.AutomationId"));
        var button = controls.Single(element => (string?)element.Attribute(x + "Name") == "MotionPhotoButton");
        Assert.Equal("ViewerMotionPhoto", (string?)button.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal("OnMotionPhotoClick", (string?)button.Attribute("Click"));
        Assert.Equal("Collapsed", (string?)button.Attribute("Visibility"));
        var tools = controls.Single(element => (string?)element.Attribute(x + "Name") == "MotionPhotoTools");
        Assert.NotEqual((string?)player.Attribute("Grid.Row"), (string?)tools.Attribute("Grid.Row"));
        Assert.Equal("Collapsed", (string?)tools.Attribute("Visibility"));
        var standalone = controls.Single(element => (string?)element.Attribute(x + "Name") == "VideoPlayer");
        Assert.Equal("True", (string?)standalone.Attribute("AutoPlay"));
        var resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "Resources.resw"));
        foreach (string key in new[] { "MotionPhotoPlay", "MotionPhotoShowStill", "MotionPhotoUnavailable",
            "MotionPhotoTimeout", "MotionPhotoMediaLimit", "MotionPhotoCodecUnavailable",
            "MotionPhotoPlayer.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name" })
        {
            Assert.Contains(resources.Descendants("data"), element =>
                (string?)element.Attribute("name") == key && !string.IsNullOrWhiteSpace(element.Element("value")?.Value));
        }
    }
}
