// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Xml.Linq;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class GridHeaderTests
{
    [Fact]
    public void ReviewActionsUseGridHeaderAndSearchFillsRightPaneHeader()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var main = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "MainWindow.xaml"));
        var grid = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "ImageGridView.xaml"));
        var resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "Resources.resw"));
        var namedControls = grid.Descendants()
            .Where(element => element.Attribute(xaml + "Name") is not null)
            .ToDictionary(element => (string)element.Attribute(xaml + "Name")!);

        foreach (var name in new[] { "PeopleReviewButton", "DuplicateReviewButton" })
        {
            Assert.DoesNotContain(main.Descendants(), element =>
                (string?)element.Attribute(xaml + "Name") == name);
            Assert.True(namedControls.ContainsKey(name));
        }

        var toolbar = namedControls["GroupByCombo"].Ancestors()
            .First(element => element.Name.LocalName == "Grid");
        foreach (var name in new[] { "PeopleReviewButton", "DuplicateReviewButton" })
            Assert.Contains(toolbar, namedControls[name].Ancestors());

        foreach (var name in new[] { "PeopleReviewButton", "DuplicateReviewButton" })
        {
            var button = namedControls[name];
            Assert.Contains(button.Elements(), child => child.Name.LocalName is "FontIcon" or "SymbolIcon");
            Assert.Null(button.Attribute("Content"));
            var uid = (string)button.Attribute(xaml + "Uid")!;
            Assert.Contains(resources.Descendants("data"), resource =>
                ((string?)resource.Attribute("name"))?.StartsWith(uid + ".", StringComparison.Ordinal) == true &&
                ((string?)resource.Attribute("name"))?.EndsWith("ToolTipService.ToolTip", StringComparison.Ordinal) == true &&
                !string.IsNullOrWhiteSpace(resource.Element("value")?.Value));
        }
        var search = main.Descendants().Single(element =>
            (string?)element.Attribute(xaml + "Name") == "LibrarySearchBox");
        Assert.Null(search.Attribute("Width"));
        Assert.Contains(search.Ancestors(), element => (string?)element.Attribute("Grid.Column") == "4");
        Assert.DoesNotContain(grid.Descendants(), element =>
            (string?)element.Attribute(xaml + "Name") == "LibrarySearchBox");
        Assert.Equal("2", (string?)namedControls["PeopleReviewButton"].Attribute("Grid.Column"));
        Assert.Equal("3", (string?)namedControls["DuplicateReviewButton"].Attribute("Grid.Column"));
        var zoom = namedControls["SizeSlider"].Parent!;
        Assert.Equal("Right", (string?)zoom.Attribute("HorizontalAlignment"));
        Assert.Equal("5", (string?)zoom.Attribute("Grid.Column"));
        Assert.Equal("6", (string?)namedControls["FilterButton"].Attribute("Grid.Column"));
        Assert.Equal("Copy", (string?)namedControls["DuplicateReviewButton"]
            .Elements().Single().Attribute("Symbol"));
    }
}
