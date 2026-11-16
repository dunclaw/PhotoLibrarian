// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Xml.Linq;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ToolbarStyleTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void SharedToolbarStylesDefineCommonSizesAndThemeAwareSeparators()
    {
        XDocument app = Load("App.xaml");

        XElement commandBarStyle = FindStyle(app, "ToolbarCommandBarStyle");
        Assert.Equal("{StaticResource DefaultCommandBarStyle}",
            (string?)commandBarStyle.Attribute("BasedOn"));
        Assert.Equal("Right", Setter(app, "ToolbarCommandBarStyle", "DefaultLabelPosition"));
        Assert.Equal("True", Setter(app, "ToolbarCommandBarStyle", "IsDynamicOverflowEnabled"));
        Assert.Equal("32", Setter(app, "ToolbarCommandBarStyle", "MinHeight"));
        Assert.Equal("Transparent", Setter(app, "ToolbarCommandBarStyle", "Background"));

        Assert.Equal("32", Setter(app, "ToolbarIconButtonStyle", "MinWidth"));
        Assert.Equal("32", Setter(app, "ToolbarIconButtonStyle", "MinHeight"));
        Assert.Equal("4", Setter(app, "ToolbarIconButtonStyle", "Padding"));
        Assert.Equal("16", Setter(app, "ToolbarIconStyle", "FontSize"));
        Assert.Equal("20", Setter(app, "RibbonIconStyle", "FontSize"));
        Assert.Equal("{ThemeResource DividerStrokeColorDefaultBrush}",
            Setter(app, "RibbonSeparatorStyle", "Fill"));
        Assert.Equal("8,12", Setter(app, "RibbonSeparatorStyle", "Margin"));
    }

    [Fact]
    public void CommandBarsShareTheCompactStyleAcrossSurfaces()
    {
        var commandBars = new[]
        {
            Load("TopRibbon.xaml"),
            Load("PhotoComparisonView.xaml"),
            Load("PeopleReviewView.xaml")
        }
        .SelectMany(document => document.Descendants(Presentation + "CommandBar"))
        .ToList();

        Assert.Equal(5, commandBars.Count);
        Assert.All(commandBars, commandBar =>
            Assert.Equal("{StaticResource ToolbarCommandBarStyle}",
                (string?)commandBar.Attribute("Style")));
    }

    [Fact]
    public void EditorAndGridIconButtonsUseSharedHitTargetsAndIconSizing()
    {
        XDocument editor = Load("ImageEditorView.xaml");
        foreach (string name in new[]
        {
            "PreviousBtn", "NextBtn", "SaveBtn", "CloseBtn",
            "RetouchUndoBtn", "RetouchRedoBtn", "UndoBtn", "RedoBtn"
        })
            AssertButtonStyle(FindByName(editor, name));

        foreach (string clickHandler in new[]
        {
            "OnZoomOut", "OnZoomIn", "OnZoomFit", "OnZoomActual"
        })
            AssertButtonStyle(FindByAttribute(editor, "Click", clickHandler));

        XDocument grid = Load("ImageGridView.xaml");
        foreach (string automationId in new[]
        {
            "GridImportButton", "GridComparison", "GridSlideshowButton", "GridShareButton"
        })
            AssertButtonStyle(FindByAttribute(grid, "AutomationProperties.AutomationId", automationId));

        foreach (string name in new[] { "SortOrderBtn" })
            AssertButtonStyle(FindByName(grid, name));

        foreach (string clickHandler in new[] { "OnDecreaseSize", "OnIncreaseSize" })
            AssertButtonStyle(FindByAttribute(grid, "Click", clickHandler));
    }

    private static string Setter(XDocument app, string styleKey, string property)
    {
        XElement style = FindStyle(app, styleKey);
        XElement setter = style.Elements(Presentation + "Setter")
            .Single(element => (string?)element.Attribute("Property") == property);
        return (string)setter.Attribute("Value")!;
    }

    private static XElement FindStyle(XDocument document, string key) =>
        document.Descendants(Presentation + "Style")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == key);

    private static XElement FindByName(XDocument document, string name) =>
        document.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == name);

    private static XElement FindByAttribute(XDocument document, string attribute, string value) =>
        document.Descendants().Single(element => (string?)element.Attribute(attribute) == value);

    private static void AssertButtonStyle(XElement button)
    {
        Assert.Equal("{StaticResource ToolbarIconButtonStyle}", (string?)button.Attribute("Style"));
        Assert.All(button.Descendants(Presentation + "FontIcon"), icon =>
            Assert.Equal("{StaticResource ToolbarIconStyle}", (string?)icon.Attribute("Style")));
    }

    private static XDocument Load(string fileName) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", fileName));
}
