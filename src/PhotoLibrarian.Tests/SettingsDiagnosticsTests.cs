// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Xml.Linq;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class SettingsDiagnosticsTests
{
    [Fact]
    public void ImageBenchmarkIsInLocalizedSettingsDiagnosticsAndReusesExistingCommand()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var assets = Path.Combine(AppContext.BaseDirectory, "TestAssets");
        var mainWindow = XDocument.Load(Path.Combine(assets, "MainWindow.xaml"));
        var settings = XDocument.Load(Path.Combine(assets, "SettingsPanel.xaml"));
        var resources = XDocument.Load(Path.Combine(assets, "Resources.resw"));

        var statusBar = Assert.Single(mainWindow.Descendants(),
            element => (string?)element.Attribute(xaml + "Name") == "StatusBar");
        Assert.DoesNotContain(statusBar.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            (string?)element.Attribute("AutomationProperties.AutomationId") ==
                "BenchmarkButton");

        var diagnostics = Assert.Single(settings.Descendants(),
            element => (string?)element.Attribute(xaml + "Name") ==
                "DiagnosticsSection");
        var benchmarkButton = Assert.Single(settings.Descendants(),
            element => element.Name.LocalName == "Button" &&
            (string?)element.Attribute("AutomationProperties.AutomationId") ==
                "DiagnosticsBenchmarkButton");
        Assert.Contains(diagnostics.Descendants(), element =>
            ReferenceEquals(element, benchmarkButton));
        Assert.Equal("OnRunImageBenchmark",
            (string?)benchmarkButton.Attribute("Click"));
        Assert.Equal("DiagnosticsBenchmarkButton",
            (string?)benchmarkButton.Attribute(xaml + "Uid"));

        var localizedResources = resources.Descendants("data")
            .Select(element => (string?)element.Attribute("name"))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("DiagnosticsTitle.Text", localizedResources);
        Assert.Contains("DiagnosticsBenchmarkDescription.Text",
            localizedResources);
        Assert.Contains("DiagnosticsBenchmarkButton.Content",
            localizedResources);
        Assert.Contains(
            "DiagnosticsBenchmarkButton.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
            localizedResources);
        Assert.Contains(
            "DiagnosticsBenchmarkButton.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip",
            localizedResources);

        var settingsCode = File.ReadAllText(
            Path.Combine(assets, "SettingsPanel.xaml.cs"));
        var handlerStart = settingsCode.IndexOf(
            "private async void OnRunImageBenchmark",
            StringComparison.Ordinal);
        Assert.True(handlerStart >= 0);
        var handlerEnd = settingsCode.IndexOf(
            "private void OnApproveProfile",
            handlerStart,
            StringComparison.Ordinal);
        Assert.True(handlerEnd > handlerStart);
        var handler = settingsCode[handlerStart..handlerEnd];
        Assert.Contains(
            "App.ViewModel.RunBenchmarkCommand.ExecuteAsync(null)",
            handler);
    }
}
