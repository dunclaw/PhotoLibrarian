// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Xml.Linq;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class SharpeningControlsTests
{
    [Fact]
    public void SharpeningBlockDefaultsToOneSliderAndOffersThreeAdvancedSliders()
    {
        var document = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "TestAssets", "ImageEditorView.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var block = Assert.Single(document.Descendants().Where(
            element => (string?)element.Attribute(xaml + "Name") == "SharpeningPanel"));
        Assert.Contains(block.Elements(), element => (string?)element.Attribute("Text") == "Sharpening");
        Assert.DoesNotContain(block.Descendants(), element => element.Name.LocalName == "ComboBox");

        var checkbox = Assert.Single(block.Elements().Where(
            element => element.Name.LocalName == "CheckBox"));
        Assert.Equal("Advanced", (string?)checkbox.Attribute("Content"));
        Assert.NotEqual("True", (string?)checkbox.Attribute("IsChecked"));
        Assert.Equal("OnAdvancedSharpeningChanged", (string?)checkbox.Attribute("Checked"));
        Assert.Equal("OnAdvancedSharpeningChanged", (string?)checkbox.Attribute("Unchecked"));

        var basic = Assert.Single(block.Elements().Where(
            element => (string?)element.Attribute(xaml + "Name") == "BasicSharpeningPanel"));
        Assert.NotEqual("Collapsed", (string?)basic.Attribute("Visibility"));
        Assert.Single(basic.Descendants().Where(element => element.Name.LocalName == "Slider"));
        var advanced = Assert.Single(block.Elements().Where(
            element => (string?)element.Attribute(xaml + "Name") == "UnsharpMaskPanel"));
        Assert.Equal("Collapsed", (string?)advanced.Attribute("Visibility"));
        Assert.Equal(3, advanced.Descendants().Count(element => element.Name.LocalName == "Slider"));
        Assert.DoesNotContain(block.Descendants().Attributes(),
            attribute => attribute.Value.Contains("Legacy", StringComparison.OrdinalIgnoreCase));
    }
}
