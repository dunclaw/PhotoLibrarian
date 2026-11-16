// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Runtime.InteropServices;
using System.Xml.Linq;
using PhotoLibrarian.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PhotoClipboardTests
{
    [Theory]
    [InlineData("Vacation photo.JPG", "filename")]
    [InlineData(@"D:\Photos\Vacation", "folder path")]
    [InlineData(@"D:\Photos\Vacation\Vacation photo.JPG", "full path")]
    [InlineData("\\\\server\\Photos\\caf\u00e9.jpg", "full path")]
    [InlineData("caf\u00e9 \u65c5\u884c.HEIC", "filename")]
    public void CopyPreservesExactTextAndReportsSuccess(string text, string description)
    {
        string? copiedText = null;

        var status = PhotoClipboardService.CopyText(text, description, value => copiedText = value);

        Assert.Equal(text, copiedText);
        Assert.Equal($"Copied {description} to the clipboard.", status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void UnavailableValueDoesNotReplaceClipboard(string? text)
    {
        bool wroteText = false;

        var status = PhotoClipboardService.CopyText(text, "full path", _ => wroteText = true);

        Assert.False(wroteText);
        Assert.Equal("Could not copy full path: no value is available.", status);
    }

    [Fact]
    public void ClipboardFailureReportsTheError()
    {
        var status = PhotoClipboardService.CopyText(
            @"D:\Photos\photo.jpg",
            "full path",
            _ => throw new COMException("Clipboard is busy."));

        Assert.Equal("Could not copy full path: Clipboard is busy.", status);
    }

    [Theory]
    [InlineData("CopyFileName", "Copy filename with extension", "OnCopyFileNameClick")]
    [InlineData("CopyFolderPath", "Copy folder path", "OnCopyFolderPathClick")]
    [InlineData("CopyFullPath", "Copy full path", "OnCopyFullPathClick")]
    public void DetailCopyButtonsAreAccessibleAndInitiallyDisabled(
        string automationId, string name, string clickHandler)
    {
        var button = LoadXaml("MetadataPanel.xaml").Descendants()
            .Single(element => (string?)element.Attribute("AutomationProperties.AutomationId") == automationId);

        Assert.Equal("Button", button.Name.LocalName);
        Assert.Equal(name, (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Equal(clickHandler, (string?)button.Attribute("Click"));
        Assert.Equal("False", (string?)button.Attribute("IsEnabled"));
        Assert.NotNull(button.Attribute("ToolTipService.ToolTip"));
        Assert.Null(button.Attribute("IsTabStop"));
    }

    [Fact]
    public void ViewerHasStandardContextFlyoutForMouseAndKeyboard()
    {
        var root = LoadXaml("ImageViewerOverlay.xaml").Root!;
        var contextFlyout = root.Elements()
            .Single(element => element.Name.LocalName == "UserControl.ContextFlyout");
        var menu = Assert.Single(contextFlyout.Elements());
        Assert.Equal("MenuFlyout", menu.Name.LocalName);
        Assert.Equal("OnPhotoContextMenuOpening", (string?)menu.Attribute("Opening"));
        Assert.Equal("OnPhotoContextMenuClosed", (string?)menu.Attribute("Closed"));
        Assert.Empty(menu.Elements()); // Populated by the same builder as the grid.
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "ImageGridView.xaml.cs"));
        Assert.Contains("Text = \"Copy full path\"", source);
        Assert.Contains("PhotoClipboardService.CopyText(", source);
        Assert.Contains("\"CopyFullPath\");", source);
    }

    private static XDocument LoadXaml(string fileName) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", fileName));
}
