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
    public void TopBarActions_UseSharedToolbarStylesWithoutChangingOrderOrCommands()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var overlay = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "ImageViewerOverlay.xaml"));
        var topBar = overlay.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "TopBar");
        var toolbar = topBar.Elements().Single(e => (string?)e.Attribute("Grid.Column") == "1");
        var actions = toolbar.Elements().ToList();

        Assert.Equal("StackPanel", toolbar.Name.LocalName);
        Assert.Equal("Horizontal", (string?)toolbar.Attribute("Orientation"));
        Assert.Equal(
            new[]
            {
                "ViewerMotionPhoto",
                "ViewerZoomOut",
                "ViewerZoomFit",
                "ViewerZoomIn",
                "ViewerActualSize",
                "ViewerInformationPane",
                "CancelManualFaceTagging",
                "ViewerClose"
            },
            actions.Select(e => (string?)e.Attribute("AutomationProperties.AutomationId")));
        Assert.Equal(
            new[]
            {
                "OnMotionPhotoClick",
                "OnZoomOut",
                "OnZoomFit",
                "OnZoomIn",
                "OnZoomActualSize",
                null,
                "OnCancelManualFaceTagging",
                "OnClose"
            },
            actions.Select(e => (string?)e.Attribute("Click")));

        foreach (var action in actions.Where(e => e.Name.LocalName == "Button"))
        {
            Assert.Equal(
                "{StaticResource ToolbarIconButtonStyle}",
                (string?)action.Attribute("Style"));
        }

        foreach (var icon in actions.SelectMany(action => action.Descendants(presentation + "FontIcon")))
        {
            Assert.Equal(
                "{StaticResource ToolbarIconStyle}",
                (string?)icon.Attribute("Style"));
        }

        var information = actions.Single(e =>
            (string?)e.Attribute("AutomationProperties.AutomationId") == "ViewerInformationPane");
        Assert.Contains("ToggleInformationPaneCommand", (string?)information.Attribute("Command"));
        Assert.Equal("8", (string?)information.Attribute("Padding"));
        Assert.Equal("Collapsed", (string?)actions[0].Attribute("Visibility"));
        Assert.Equal("Collapsed", (string?)actions[6].Attribute("Visibility"));

        var app = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "App.xaml"));
        var styleKeys = app.Descendants()
            .Select(e => (string?)e.Attribute(x + "Key"))
            .Where(key => key is not null)
            .ToHashSet();
        Assert.Contains("ToolbarIconButtonStyle", styleKeys);
        Assert.Contains("ToolbarIconStyle", styleKeys);
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
