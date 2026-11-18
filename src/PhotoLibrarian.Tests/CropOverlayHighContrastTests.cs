// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Xml.Linq;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class CropOverlayHighContrastTests
{
    [Fact]
    public void CropOverlayUsesSystemColorsForHighContrastAndPreservesOtherThemes()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "CropOverlay.xaml"));
        var themes = xaml.Descendants().Where(element =>
            element.Name.LocalName == "ResourceDictionary" &&
            element.Attribute(x + "Key") is not null).ToList();
        var expectedKeys = new[]
        {
            "CropMaskBrush",
            "CropBorderBrush",
            "CropGridBrush",
            "CropHandleFillBrush",
            "CropHandleStrokeBrush"
        };

        Assert.Equal(new[] { "Light", "Dark", "HighContrast" },
            themes.Select(element => (string)element.Attribute(x + "Key")!));

        foreach (var theme in themes)
            Assert.Equal(expectedKeys, theme.Elements().Select(element => (string)element.Attribute(x + "Key")!));

        var highContrast = themes.Single(element => (string?)element.Attribute(x + "Key") == "HighContrast");
        var systemBrushes = new[]
        {
            "SystemColorWindowColorBrush",
            "SystemColorHighlightColorBrush",
            "SystemColorButtonFaceColorBrush",
            "SystemColorButtonTextColorBrush"
        };
        Assert.All(highContrast.Elements(), brush =>
        {
            Assert.Equal("StaticResource", brush.Name.LocalName);
            Assert.Contains((string?)brush.Attribute("ResourceKey"), systemBrushes);
        });

        foreach (var shape in xaml.Descendants())
        foreach (var property in new[] { "Fill", "Stroke", "BorderBrush" })
        {
            var value = (string?)shape.Attribute(property);
            if (value is not null)
            {
                Assert.StartsWith("{ThemeResource Crop", value);
            }
        }
    }
}
