// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Xml.Linq;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ImageViewerOverlayFocusTests
{
    [Fact]
    public void NewViewerControls_AreAccessibleLocalizedAndWired()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var controls = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "ImageViewerOverlay.xaml"))
            .Descendants().ToList();
        var actual = controls.Single(e => (string?)e.Attribute(x + "Name") == "ZoomActualSizeButton");
        Assert.Equal("OnZoomActualSize", (string?)actual.Attribute("Click"));
        Assert.Equal("False", (string?)actual.Attribute("IsEnabled"));
        Assert.Equal("ViewerActualSize", (string?)actual.Attribute("AutomationProperties.AutomationId"));
        var information = controls.Single(e => (string?)e.Attribute(x + "Name") == "InformationPaneButton");
        Assert.Equal("ToggleButton", information.Name.LocalName);
        Assert.Contains("ToggleInformationPaneCommand", (string?)information.Attribute("Command"));
        Assert.Contains("IsInformationPaneVisible", (string?)information.Attribute("IsChecked"));

        var resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "Resources.resw"));
        foreach (var control in new[] { actual, information })
        {
            var uid = (string?)control.Attribute(x + "Uid");
            Assert.False(string.IsNullOrWhiteSpace(uid));
            Assert.False(string.IsNullOrWhiteSpace((string?)control.Attribute("AutomationProperties.Name")));
            Assert.Contains(resources.Descendants("data"),
                e => ((string?)e.Attribute("name")) == $"{uid}.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name");
        }
    }

    [Fact]
    public void OverlayCanReceiveFocusForKeyboardNavigation()
    {
        var xamlPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "ImageViewerOverlay.xaml");
        var root = XDocument.Load(xamlPath).Root;

        Assert.NotNull(root);
        Assert.Equal("True", (string?)root.Attribute("IsTabStop"));
        Assert.Equal("OnKeyDown", (string?)root.Attribute("KeyDown"));
    }

    [Fact]
    public void ImageScrollViewerDoesNotStealArrowKeys()
    {
        var xamlPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "ImageViewerOverlay.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var scrollViewer = XDocument.Load(xamlPath)
            .Descendants()
            .Single(e => (string?)e.Attribute(x + "Name") == "ImageScrollViewer");

        Assert.Equal("False", (string?)scrollViewer.Attribute("IsTabStop"));
    }
}
