// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Xml.Linq;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class SettingsDiagnosticsTests
{
    [Fact]
    public void ManageFoldersRoutesToExistingIndexMaintenanceAfterDialogCloses()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var assets = Path.Combine(AppContext.BaseDirectory, "TestAssets");
        var dialog = XDocument.Load(Path.Combine(assets, "ManageFoldersDialog.xaml"));
        var button = Assert.Single(dialog.Descendants(), element =>
            (string?)element.Attribute(xaml + "Name") == "IndexMaintenanceButton");
        Assert.Equal("OnIndexMaintenanceClick", (string?)button.Attribute("Click"));
        Assert.Equal("ManageFoldersIndexMaintenanceButton", (string?)button.Attribute(xaml + "Uid"));
        var resources = XDocument.Load(Path.Combine(assets, "Resources.resw"));
        Assert.Contains(resources.Descendants("data"), element =>
            (string?)element.Attribute("name") == "ManageFoldersIndexMaintenanceButton.Content");

        var dialogCode = File.ReadAllText(Path.Combine(assets, "ManageFoldersDialog.xaml.cs"));
        Assert.DoesNotContain("File.Delete", dialogCode);
        Assert.DoesNotContain("AppInstance.Restart", dialogCode);
        Assert.DoesNotContain("new ContentDialog", dialogCode);
        Assert.Contains("IndexMaintenanceRequested = true;", dialogCode);
        Assert.Contains("Hide();", dialogCode);

        var folderCode = File.ReadAllText(Path.Combine(assets, "FolderNavigationPanel.xaml.cs"));
        var show = folderCode.IndexOf("await dialog.ShowAsync();", StringComparison.Ordinal);
        var route = folderCode.IndexOf("IndexMaintenanceRequested?.Invoke", StringComparison.Ordinal);
        Assert.True(show >= 0 && route > show);
        Assert.Contains("if (dialog.IndexMaintenanceRequested)", folderCode[show..route]);

        var window = XDocument.Load(Path.Combine(assets, "MainWindow.xaml"));
        Assert.Contains(window.Descendants(), element =>
            (string?)element.Attribute("IndexMaintenanceRequested") == "OnIndexMaintenanceRequested");
        var windowCode = File.ReadAllText(Path.Combine(assets, "MainWindow.xaml.cs"));
        var handler = windowCode[windowCode.IndexOf("private void OnIndexMaintenanceRequested", StringComparison.Ordinal)..];
        Assert.Contains("ViewModel.Settings.OpenCommand.Execute(null);", handler);
        Assert.Contains("SettingsPanel.FocusIndexMaintenance();", handler);
        var settingsCode = File.ReadAllText(Path.Combine(assets, "SettingsPanel.xaml.cs"));
        Assert.Contains("RepairLibraryIndexButton.StartBringIntoView();", settingsCode);
        Assert.Contains("RepairLibraryIndexButton.Focus(FocusState.Programmatic);", settingsCode);
        Assert.Contains("await ViewModel.RepairLibraryIndexCommand.ExecuteAsync(null);", settingsCode);
    }

    [Fact]
    public void SettingsConfirmationsUseDestructiveDialogAndRequirePrimary()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "TestAssets", "SettingsPanel.xaml.cs"));
        var start = source.IndexOf("private async Task<bool> ConfirmAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var helper = source[start..];
        Assert.Contains("DialogService.Instance.ConfirmDestructiveAsync(", helper);
        Assert.Contains("this, title, message, primaryButtonText, \"Cancel\"", helper);
        Assert.Contains("return result == ContentDialogResult.Primary;", helper);
        Assert.DoesNotContain("new ContentDialog", helper);
    }

    [Fact]
    public void SidecarMigrationErrorsUseSharedDialogAndLocalizedLabels()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "TestAssets", "SettingsPanel.SidecarMigration.cs"));
        var start = source.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("finally", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var errorHandler = source[start..end];
        Assert.Contains("DialogService.Instance.ErrorAsync(", errorHandler);
        Assert.Contains("SidecarMigrationViewModel.Text(\"MigrationTitle\")", errorHandler);
        Assert.Contains("SidecarMigrationViewModel.Text(\"MigrationClose\")", errorHandler);
        Assert.Contains("ex,", errorHandler);
        Assert.DoesNotContain("new ContentDialog", errorHandler);
    }

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
