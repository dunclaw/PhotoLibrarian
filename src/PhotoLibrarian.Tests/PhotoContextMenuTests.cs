// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Xml.Linq;
using Microsoft.UI.Input;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PhotoContextMenuTests
{
    [Theory]
    [InlineData(PointerDeviceType.Mouse, false, true, false)]
    [InlineData(PointerDeviceType.Mouse, false, false, false)]
    [InlineData(PointerDeviceType.Mouse, true, true, true)]
    [InlineData(PointerDeviceType.Touch, false, true, true)]
    [InlineData(PointerDeviceType.Pen, false, true, true)]
    [InlineData(PointerDeviceType.Pen, false, false, false)]
    public void RightMouseContactDoesNotCaptureOrPanButLeftTouchAndPenStillDo(
        PointerDeviceType device, bool leftButton, bool contact, bool expected) =>
        Assert.Equal(expected, ImageZoomPanController.ShouldStartPan(device, leftButton, contact));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void EmptyTargetCannotOfferFileOperations(string? path)
    {
        Assert.Null(PhotoContextMenuTarget.Create(null, false));
        Assert.Null(PhotoContextMenuTarget.Create(new ImageEntry { FilePath = path!, FileName = "" }, false));
    }

    [Fact]
    public void ActiveEditsCannotOfferFileOperations() =>
        Assert.Null(PhotoContextMenuTarget.Create(Photo("first.jpg"), true));

    [Theory]
    [InlineData("first.jpg", true)]
    [InlineData("first.PNG", true)]
    [InlineData("first.bmp", true)]
    [InlineData("first.gif", false)]
    [InlineData("first.heic", false)]
    [InlineData("first.cr3", false)]
    [InlineData("first.mp4", false)]
    public void WallpaperOnlyOffersSupportedRasterImages(string name, bool expected) =>
        Assert.Equal(expected, PhotoContextMenuTarget.SupportsWallpaper(Photo(name)));

    [Theory]
    [InlineData("first.jpg", MediaType.Image, true)]
    [InlineData("first.JPEG", MediaType.Image, true)]
    [InlineData("first.png", MediaType.Image, true)]
    [InlineData("first.tiff", MediaType.Image, true)]
    [InlineData("first.bmp", MediaType.Image, true)]
    [InlineData("first.gif", MediaType.Image, false)]
    [InlineData("first.heic", MediaType.Image, false)]
    [InlineData("first.cr3", MediaType.Image, false)]
    [InlineData("first.mp4", MediaType.Video, false)]
    [InlineData("first.jpg", MediaType.Video, false)]
    public void RotationOnlyOffersSupportedStillPixelEdits(string name, MediaType media, bool expected)
    {
        var entry = Photo(name);
        entry.MediaType = media;
        var target = PhotoContextMenuTarget.Create(entry, false)!;
        Assert.Equal(expected, target.CanRotate);
        Assert.Equal(expected, PhotoContextMenuTarget.SupportsRotation(entry));
    }

    [Fact]
    public async Task MenuTargetsDisplayedFileNotPreviousGridMultiselection()
    {
        var first = Video("first.mp4");
        var second = Video("second.mp4");
        var third = Video("third.mp4");
        var gridSelection = new[] { first, second };
        var viewer = new ImageViewerViewModel();
        viewer.OpenImage(first, [first, second, third]);
        await viewer.PreviousImageCommand.ExecuteAsync(null);

        var target = PhotoContextMenuTarget.Create(viewer.CurrentEntry, false)!;

        Assert.Same(third, target.Entry);
        Assert.True(target.IsCurrent(viewer.CurrentEntry));
        Assert.Equal(new[] { first, second }, gridSelection);
        await viewer.NextImageCommand.ExecuteAsync(null);
        Assert.False(target.IsCurrent(viewer.CurrentEntry));
        Assert.Same(first, viewer.CurrentEntry);
        viewer.CloseCommand.Execute(null);
        Assert.False(target.IsCurrent(viewer.CurrentEntry));
    }

    [Fact]
    public void MenuAcceptsSamePathRefreshButRejectsRenamedSnapshot()
    {
        var entry = Photo("first.jpg");
        var target = PhotoContextMenuTarget.Create(entry, false)!;
        Assert.True(target.IsCurrent(Photo("FIRST.JPG")));
        entry.FilePath = @"C:\MenuTests\renamed.jpg";
        Assert.False(target.IsCurrent(entry));
        Assert.False(target.IsCurrent(Photo("first.jpg")));
    }

    [Theory]
    [InlineData(false, ConfirmedPhotoActionResult.Cancelled, 0)]
    [InlineData(true, ConfirmedPhotoActionResult.Completed, 1)]
    public async Task DeleteRequiresExplicitConfirmation(
        bool confirm, ConfirmedPhotoActionResult expected, int expectedCalls)
    {
        var entry = Photo("first.jpg");
        var target = PhotoContextMenuTarget.Create(entry, false)!;
        var calls = 0;
        var result = await target.ExecuteConfirmedAsync(
            () => entry,
            () => Task.FromResult(confirm),
            () => { calls++; return Task.CompletedTask; });

        Assert.Equal(expected, result);
        Assert.Equal(expectedCalls, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NavigationOrCloseDuringConfirmationNeverDeletes(bool close)
    {
        ImageEntry? current = Photo("first.jpg");
        var target = PhotoContextMenuTarget.Create(current, false)!;
        var calls = 0;
        var result = await target.ExecuteConfirmedAsync(
            () => current,
            () =>
            {
                current = close ? null : Photo("second.jpg");
                return Task.FromResult(true);
            },
            () => { calls++; return Task.CompletedTask; });

        Assert.Equal(ConfirmedPhotoActionResult.Stale, result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AlreadyStaleTargetNeverConfirmsOrExecutes()
    {
        var target = PhotoContextMenuTarget.Create(Photo("first.jpg"), false)!;
        var result = await target.ExecuteConfirmedAsync(
            () => Photo("second.jpg"),
            () => throw new InvalidOperationException("Must not confirm"),
            () => throw new InvalidOperationException("Must not delete"));
        Assert.Equal(ConfirmedPhotoActionResult.Stale, result);
    }

    [Fact]
    public async Task OperationFailureIsNotReportedAsSuccess()
    {
        var entry = Photo("first.jpg");
        var target = PhotoContextMenuTarget.Create(entry, false)!;
        await Assert.ThrowsAsync<IOException>(() => target.ExecuteConfirmedAsync(
            () => entry, () => Task.FromResult(true),
            () => throw new IOException("File is locked")));
    }

    [Fact]
    public void MouseAndKeyboardUseOneStandardFlyoutWithoutNewShortcutBindings()
    {
        var xaml = XDocument.Load(Asset("ImageViewerOverlay.xaml"));
        var context = Assert.Single(xaml.Descendants().Where(
            element => element.Name.LocalName == "UserControl.ContextFlyout"));
        Assert.Equal("MenuFlyout", Assert.Single(context.Elements()).Name.LocalName);
        var source = File.ReadAllText(Asset("ImageViewerOverlay.ContextMenu.cs"));
        Assert.Contains("StopFocusGuard();", source);
        Assert.Contains("ViewModel?.CurrentEntry", source);
        Assert.Contains("IsCropping || IsStraightening || IsRedEyeRemoving || IsManualFaceTagging", source);
        Assert.DoesNotContain("SelectedImages", source);
        Assert.DoesNotContain("KeyDown", source);
        var viewer = File.ReadAllText(Asset("ImageViewerOverlay.xaml.cs"));
        Assert.Contains("if (_isPhotoContextMenuOpen) return false;", viewer);
        Assert.Contains("if (ViewerOverlay.IsPhotoContextMenuOpen) return;",
            File.ReadAllText(Asset("MainWindow.xaml.cs")));
    }

    [Fact]
    public void ViewerAndGridUseSameActionsAndExistingSafetyFlows()
    {
        var source = File.ReadAllText(Asset("ImageGridView.xaml.cs"));
        Assert.Contains("PopulatePhotoContextMenu(menu, primary, selected);", source);
        Assert.Contains("PopulatePhotoContextMenu(menu, primary, [primary], target);", source);
        foreach (var id in new[]
        {
            "ContextSlideshow", "OpenDefault", "OpenLocation", "Wallpaper", "RotateRight",
            "RotateLeft", "ResizeExportSelection", "ShareSelection", "Flag", "Copy",
            "CopyFullPath", "Delete", "Rename", "Properties"
        })
            Assert.Contains($"\"{id}\");", source);
        Assert.Contains("if (!isViewer)", source);
        Assert.Contains("viewerTarget.IsCurrent(App.ViewModel.ImageViewer.CurrentEntry)", source);
        Assert.Contains("BuildOpenWithSubMenu(menu, primary, selected, CanExecute,", source);
        Assert.Contains("DefaultButton = ContentDialogButton.Close", source);
        Assert.Contains("viewerTarget.ExecuteConfirmedAsync(", source);
        Assert.Contains("ops.DeleteToRecycleBinAsync(", source);
        Assert.Contains("RefreshAfterPixelEditAsync(", source);
        Assert.Contains("RenameEntriesAsync(selectedEntries)", source);
        Assert.Contains("ViewerOverlay.PhotoContextMenuOpening += ImageGridPanel.PopulateViewerContextMenu;",
            File.ReadAllText(Asset("MainWindow.xaml.cs")));
    }

    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "TestAssets", name);
    private static ImageEntry Photo(string name) => new()
    {
        FilePath = Path.Combine(@"C:\MenuTests", name), FileName = name, MediaType = MediaType.Image
    };
    private static ImageEntry Video(string name)
    {
        var entry = Photo(name);
        entry.MediaType = MediaType.Video;
        return entry;
    }
}
