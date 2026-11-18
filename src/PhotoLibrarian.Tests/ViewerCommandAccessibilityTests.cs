// Copyright (c) Duncan Lawler. All Rights Reserved.

using PhotoLibrarian.Views;
using PhotoLibrarian.Core.Models;
using System.Reflection;
using System.Xml.Linq;
using Windows.System;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ViewerCommandAccessibilityTests
{
    [Theory]
    [InlineData(VirtualKey.Tab)]
    [InlineData(VirtualKey.F10)]
    [InlineData(VirtualKey.Application)]
    public void NavigationAndContextKeysRemainAvailable(VirtualKey key)
    {
        foreach (var editing in new[] { false, true })
        foreach (var interactive in new[] { false, true })
            Assert.False(ImageViewerOverlay.ShouldHandleShortcut(key, interactive, editing));
    }

    [Theory]
    [InlineData(VirtualKey.Left)]
    [InlineData(VirtualKey.Right)]
    [InlineData(VirtualKey.Enter)]
    [InlineData(VirtualKey.Space)]
    [InlineData(VirtualKey.A)]
    [InlineData(VirtualKey.I)]
    [InlineData(VirtualKey.F)]
    [InlineData(VirtualKey.Add)]
    [InlineData(VirtualKey.Subtract)]
    public void ChildControlsOwnActivationArrowsAndText(VirtualKey key)
    {
        Assert.False(ImageViewerOverlay.ShouldHandleShortcut(key, true, false));
        Assert.False(ImageViewerOverlay.ShouldHandleShortcut(key, true, true));
    }

    [Fact]
    public void EditingViewportKeepsCancelApplyAndNavigationProtection()
    {
        foreach (var key in new[] { VirtualKey.Escape, VirtualKey.Enter, VirtualKey.Left, VirtualKey.Right })
            Assert.True(ImageViewerOverlay.ShouldHandleShortcut(key, false, true));
        foreach (var key in new[] { VirtualKey.A, VirtualKey.I, VirtualKey.F })
            Assert.False(ImageViewerOverlay.ShouldHandleShortcut(key, false, true));
        Assert.True(ImageViewerOverlay.ShouldHandleShortcut(VirtualKey.Escape, true, true));
        Assert.True(ImageViewerOverlay.ShouldHandleShortcut(VirtualKey.Right, false, false));
    }

    [Fact]
    public void ViewerChromeUsesSystemContrastBrushesAndNativeButtonForeground()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "ImageViewerOverlay.xaml"));
        var themes = xaml.Descendants().Where(e => e.Name.LocalName == "ResourceDictionary" &&
            e.Attribute(x + "Key") is not null).ToList();
        Assert.Equal(new[] { "Light", "Dark", "HighContrast" },
            themes.Select(e => (string)e.Attribute(x + "Key")!));
        var contrast = themes.Single(e => (string?)e.Attribute(x + "Key") == "HighContrast");
        Assert.Equal(4, contrast.Elements().Count());
        Assert.All(contrast.Elements(), brush =>
        {
            Assert.Equal("StaticResource", brush.Name.LocalName);
            Assert.Contains((string?)brush.Attribute("ResourceKey"),
                new[] { "SystemColorWindowColorBrush", "SystemColorWindowTextColorBrush" });
        });
        Assert.All(xaml.Descendants().Where(e => e.Name.LocalName == "FontIcon"),
            icon => Assert.Null(icon.Attribute("Foreground")));
        foreach (var id in new[] { "ViewerPrevious", "ViewerNext" })
        {
            var button = xaml.Descendants().Single(e =>
                (string?)e.Attribute("AutomationProperties.AutomationId") == id);
            Assert.Null(button.Attribute("Opacity"));
        }
    }

    [Fact]
    public void PresetControlsHaveAccessibleNamesUniqueActionsAndGroupOptions()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "ImageViewerOverlay.xaml"));
        var resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "Resources.resw"));
        var resourceNames = resources.Descendants("data")
            .Select(element => (string?)element.Attribute("name"))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var uid in new[]
        {
            "EditorPresetGallery", "EditorPresetAmountSlider", "EditorSavePresetButton",
            "EditorApplyPresetButton", "EditorPresetFavorite", "EditorPresetDelete"
        })
            Assert.Contains(
                $"{uid}.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
                resourceNames);

        var favorite = Assert.Single(xaml.Descendants().Where(element =>
            (string?)element.Attribute(x + "Uid") == "EditorPresetFavorite"));
        Assert.Contains("PresetFavoriteAutomationId",
            (string?)favorite.Attribute("AutomationProperties.AutomationId"));
        var delete = Assert.Single(xaml.Descendants().Where(element =>
            (string?)element.Attribute(x + "Uid") == "EditorPresetDelete"));
        Assert.Contains("PresetDeleteAutomationId",
            (string?)delete.Attribute("AutomationProperties.AutomationId"));

        foreach (var handler in new[]
        {
            (string?)favorite.Attribute("Click"),
            (string?)delete.Attribute("Click"),
            (string?)xaml.Descendants().Single(element =>
                (string?)element.Attribute(x + "Uid") == "EditorSavePresetButton").Attribute("Click"),
            (string?)xaml.Descendants().Single(element =>
                (string?)element.Attribute(x + "Uid") == "EditorApplyPresetButton").Attribute("Click")
        })
            Assert.NotNull(typeof(ImageViewerOverlay).GetMethod(
                handler!, BindingFlags.Instance | BindingFlags.NonPublic));

        Assert.Equal(EditSettingGroup.All & ~EditSettingGroup.RawDevelopment,
            EditSettingGroupDialog.AvailableForPhoto("photo.png"));
        Assert.Equal(EditSettingGroup.All,
            EditSettingGroupDialog.AvailableForPhoto("photo.dng"));
    }
}
