// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Xml.Linq;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class OverlayNavigationStateTests
{
    [Theory]
    [InlineData(false, false, false, false, false, false, (int)OverlaySurface.Library)]
    [InlineData(false, false, false, false, false, true, (int)OverlaySurface.Viewer)]
    [InlineData(false, false, false, false, true, true, (int)OverlaySurface.Editor)]
    [InlineData(false, false, false, true, true, true, (int)OverlaySurface.Settings)]
    [InlineData(false, false, true, true, true, true, (int)OverlaySurface.PeopleReview)]
    [InlineData(false, true, true, true, true, true, (int)OverlaySurface.DuplicateReview)]
    [InlineData(true, true, true, true, true, true, (int)OverlaySurface.Comparison)]
    public void Resolve_PreservesOverlayZOrder(
        bool comparison,
        bool duplicates,
        bool people,
        bool settings,
        bool editor,
        bool viewer,
        int expected)
    {
        var actual = OverlayNavigationState.Resolve(
            comparison,
            duplicates,
            people,
            settings,
            editor,
            viewer);

        Assert.Equal((OverlaySurface)expected, actual);
    }

    [Fact]
    public void MainWindow_OverlaysRetainTheirSurfaceSpecificLayouts()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var controls = XDocument.Load(Path.Combine(
                AppContext.BaseDirectory,
                "TestAssets",
                "MainWindow.xaml"))
            .Descendants()
            .ToList();

        var viewer = controls.Single(element =>
            (string?)element.Attribute(x + "Name") == "ViewerOverlay");
        Assert.Equal("2", (string?)viewer.Parent?.Attribute("Grid.Column"));

        var editor = controls.Single(element =>
            (string?)element.Attribute(x + "Name") == "EditorOverlay");
        Assert.Equal("1", (string?)editor.Attribute("Grid.Row"));

        var settings = controls.Single(element =>
            (string?)element.Attribute(x + "Name") == "SettingsOverlay");
        Assert.Equal("0", (string?)settings.Attribute("Grid.Row"));
        Assert.Equal("2", (string?)settings.Attribute("Grid.RowSpan"));
        var settingsCard = settings.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Border");
        Assert.Equal("760", (string?)settingsCard?.Attribute("MaxWidth"));

        foreach (var name in new[] { "PeopleReviewOverlay", "DuplicateReviewOverlay" })
        {
            var overlay = controls.Single(element =>
                (string?)element.Attribute(x + "Name") == name);
            Assert.Equal("0", (string?)overlay.Attribute("Grid.Row"));
            Assert.Equal("2", (string?)overlay.Attribute("Grid.RowSpan"));
        }

        var comparison = controls.Single(element =>
            (string?)element.Attribute(x + "Name") == "ComparisonOverlay");
        Assert.Equal("4", (string?)comparison.Attribute("Grid.RowSpan"));
    }

    [Fact]
    public void OverlayFocusTargets_ArePresentAndUsedByTheHost()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var assets = Path.Combine(AppContext.BaseDirectory, "TestAssets");
        var mainWindow = XDocument.Load(Path.Combine(assets, "MainWindow.xaml"));
        var settings = XDocument.Load(Path.Combine(assets, "SettingsPanel.xaml"));
        var people = XDocument.Load(Path.Combine(assets, "PeopleReviewView.xaml"));
        var duplicates = XDocument.Load(Path.Combine(assets, "DuplicateReviewView.xaml"));
        var hostCode = File.ReadAllText(Path.Combine(assets, "MainWindow.xaml.cs"));

        AssertNamedElement(settings, x, "MapProviderCombo");
        AssertNamedElement(people, x, "ModeSelector");
        AssertNamedElement(duplicates, x, "ScanButton");
        AssertNamedElement(mainWindow, x, "PeopleReviewPanel");
        AssertNamedElement(mainWindow, x, "DuplicateReviewPanel");
        Assert.Contains("SettingsPanel.FocusInitialControl()", hostCode);
        Assert.Contains("PeopleReviewPanel.FocusInitialControl()", hostCode);
        Assert.Contains("DuplicateReviewPanel.FocusInitialControl()", hostCode);
    }

    private static void AssertNamedElement(XDocument document, XNamespace x, string name) =>
        Assert.Contains(document.Descendants(), element =>
            (string?)element.Attribute(x + "Name") == name);
}
