// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Xml.Linq;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class GridHeaderTests
{
    [Fact]
    public void SearchAndIconReviewActionsShareTheGridHeaderNotTheMainWindowStatusBar()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var main = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "MainWindow.xaml"));
        var grid = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "ImageGridView.xaml"));
        var resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "Resources.resw"));
        var namedControls = grid.Descendants()
            .Where(element => element.Attribute(xaml + "Name") is not null)
            .ToDictionary(element => (string)element.Attribute(xaml + "Name")!);

        foreach (var name in new[] { "LibrarySearchBox", "PeopleReviewButton", "DuplicateReviewButton" })
        {
            Assert.DoesNotContain(main.Descendants(), element =>
                (string?)element.Attribute(xaml + "Name") == name);
            Assert.True(namedControls.ContainsKey(name));
        }

        var toolbar = namedControls["GroupByCombo"].Ancestors()
            .First(element => element.Name.LocalName == "Grid");
        foreach (var name in new[] { "LibrarySearchBox", "PeopleReviewButton", "DuplicateReviewButton" })
            Assert.Contains(toolbar, namedControls[name].Ancestors());

        foreach (var name in new[] { "PeopleReviewButton", "DuplicateReviewButton" })
        {
            var button = namedControls[name];
            Assert.Contains(button.Elements(), child => child.Name.LocalName == "FontIcon");
            Assert.Null(button.Attribute("Content"));
            var uid = (string)button.Attribute(xaml + "Uid")!;
            Assert.Contains(resources.Descendants("data"), resource =>
                ((string?)resource.Attribute("name"))?.StartsWith(uid + ".", StringComparison.Ordinal) == true &&
                ((string?)resource.Attribute("name"))?.EndsWith("ToolTipService.ToolTip", StringComparison.Ordinal) == true &&
                !string.IsNullOrWhiteSpace(resource.Element("value")?.Value));
        }
    }
}
