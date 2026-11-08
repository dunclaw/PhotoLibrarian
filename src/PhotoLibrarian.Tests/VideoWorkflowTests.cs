// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Reflection;
using System.Xml.Linq;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ViewModels;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class VideoWorkflowTests
{
    [Fact]
    public void ExportedVideoOpensImmediatelyAndRemainsNavigableWithoutIndexing()
    {
        var original = new ImageEntry { FilePath = @"D:\original.mp4", FileName = "original.mp4", MediaType = MediaType.Video };
        var next = new ImageEntry { FilePath = @"D:\next.mp4", FileName = "next.mp4", MediaType = MediaType.Video };
        var viewer = new ImageViewerViewModel();
        viewer.OpenImage(original, [original, next]);

        var copy = new ImageEntry
        {
            Id = 3, FilePath = @"D:\elsewhere\result.mp4", FileName = "result.mp4", MediaType = MediaType.Video
        };
        viewer.OpenExportedVideo(copy);

        Assert.True(viewer.IsOpen);
        Assert.True(viewer.IsVideo);
        Assert.Equal(@"D:\elsewhere\result.mp4", viewer.VideoPath);
        Assert.Equal("result.mp4", viewer.Title);
        Assert.Equal(viewer.CurrentEntry, viewer.Images[1]);
        Assert.Equal(original, viewer.Images[0]);
        Assert.Equal(next, viewer.Images[2]);
        Assert.Same(copy, viewer.CurrentEntry);
        Assert.Equal(3, viewer.CurrentEntry!.Id);

        // A watcher refresh must not jump playback back to the original while indexing catches up.
        viewer.UpdateLibraryImages([original, next]);
        Assert.Equal(@"D:\elsewhere\result.mp4", viewer.CurrentEntry!.FilePath);
        Assert.Equal(@"D:\elsewhere\result.mp4", viewer.VideoPath);
        viewer.OpenExportedVideo(copy);
        Assert.Equal(3, viewer.Images.Count);
        var refreshed = new ImageEntry
        {
            Id = 3, FilePath = copy.FilePath, FileName = copy.FileName, MediaType = MediaType.Video,
            Caption = "Indexed caption"
        };
        viewer.OpenExportedVideo(refreshed);
        Assert.Same(refreshed, viewer.CurrentEntry);
        Assert.Equal(3, viewer.Images.Count);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    public void RefreshOnlyReusesUnchangedThumbnailContent(int sizeChange, int timeChange, bool expected)
    {
        var previous = new ImageEntry { FilePath = @"D:\clip.mp4", FileName = "clip.mp4", MediaType = MediaType.Video, FileSize = 100,
            DateModified = new DateTime(2025, 4, 1) };
        var current = new ImageEntry { FilePath = @"d:\CLIP.mp4", FileName = "CLIP.mp4", MediaType = MediaType.Video, FileSize = 100 + sizeChange,
            DateModified = previous.DateModified.AddSeconds(timeChange) };
        var method = typeof(ImageThumbnailViewModel).GetMethod("CanReuseThumbnail", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, method.Invoke(null, [previous, current]));
        current.FilePath = @"D:\another.mp4";
        Assert.Equal(false, method.Invoke(null, [previous, current]));
    }

    [Fact]
    public void VideoEditingHasItsOwnRibbonAndIsNotAmongZoomControls()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var ribbon = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "TopRibbon.xaml"));
        var videoTools = ribbon.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "VideoTools");
        var buttons = videoTools.Descendants().Where(element => element.Name.LocalName == "Button").ToList();
        var stabilize = Assert.Single(buttons.Where(button => (string?)button.Attribute("Click") == "OnStabilizeVideoClick"));
        Assert.Equal("OnStabilizeVideoClick", (string?)stabilize.Attribute("Click"));
        Assert.Contains(buttons, button => (string?)button.Attribute("Click") == "OnEditVideoClick");
        Assert.Contains(buttons, button => (string?)button.Attribute("Click") == "OnStabilizeVideoClick");
        Assert.Contains(buttons, button => (string?)button.Attribute("Click") == "OnTrimVideoClick");
        var viewer = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "ImageViewerOverlay.xaml"));
        Assert.DoesNotContain(viewer.Descendants(), element => (string?)element.Attribute("Click") == "OnStabilizeVideo");
        var dialog = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "VideoStabilizationDialog.xaml"));
        var checkBoxes = dialog.Descendants(presentation + "CheckBox")
            .ToDictionary(element => (string?)element.Attribute("AutomationProperties.AutomationId") ?? "");
        Assert.Equal("True", (string?)checkBoxes["StabilizationAutoCrop"].Attribute("IsChecked"));
        Assert.Equal("True", (string?)checkBoxes["StabilizationRotation"].Attribute("IsChecked"));
        Assert.Null(checkBoxes["StabilizationLockFrame"].Attribute("IsChecked"));
        var smoothness = Assert.Single(dialog.Descendants(presentation + "ComboBox"));
        Assert.Equal("StabilizationSmoothness", (string?)smoothness.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal("1", (string?)smoothness.Attribute("SelectedIndex"));

        var trimDialog = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "VideoTrimDialog.xaml"));
        Assert.Contains(trimDialog.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "VideoTrimStartSlider");
        Assert.Contains(trimDialog.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "VideoTrimEndSlider");
        Assert.Contains(trimDialog.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "VideoTrimStartSeconds");
        Assert.Contains(trimDialog.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "VideoTrimEndSeconds");
    }

    [Fact]
    public void VideoEditDialogRequiresPreparedPreviewAndLocalizesAccessibleControls()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dialog = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "VideoEditDialog.xaml"));
        Assert.Equal("False", (string?)dialog.Root!.Attribute("IsPrimaryButtonEnabled"));
        var resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "Resources.resw"))
            .Descendants("data").Select(element => (string)element.Attribute("name")!).ToHashSet();
        foreach (var control in dialog.Descendants().Where(element =>
                     element.Name.LocalName is "Button" or "ComboBox" or "CheckBox" or "TextBox" or "MediaPlayerElement"))
        {
            Assert.False(string.IsNullOrWhiteSpace((string?)control.Attribute("AutomationProperties.AutomationId")));
            string uid = (string)control.Attribute(x + "Uid")!;
            Assert.Contains(resources, key => key.StartsWith(uid + ".", StringComparison.Ordinal));
        }
        Assert.Contains("VideoEditPreview.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name", resources);
        Assert.Contains("VideoEditRibbon.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name", resources);
        var prepare = dialog.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "PrepareButton");
        Assert.Equal("OnPrepare", (string?)prepare.Attribute("Click"));
        Assert.Equal("False", (string?)prepare.Attribute("IsEnabled"));
    }

    [Fact]
    public void MetadataPanelDeclaresOptionalVideoDetailRowsAndKeepsPhotoDetails()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var panel = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "MetadataPanel.xaml"));
        var rows = panel.Descendants()
            .Select(element => (string?)element.Attribute(x + "Name"))
            .Where(name => name is not null)
            .ToHashSet();

        Assert.Contains("InfoDimensionsRow", rows);
        Assert.Contains("InfoCameraRow", rows);
        Assert.Contains("InfoVideoTitleRow", rows);
        Assert.Contains("InfoVideoDurationRow", rows);
        Assert.Contains("InfoVideoBitrateRow", rows);
        Assert.Contains("InfoVideoFrameRateRow", rows);
        Assert.Contains("InfoVideoCodecRow", rows);
        Assert.Contains("InfoVideoAlbumRow", rows);
        Assert.Contains("InfoVideoYearRow", rows);
        Assert.Contains("InfoVideoDateEncodedRow", rows);
        Assert.Contains("VideoMetadataErrorBar", rows);
    }

    [Fact]
    public void PhotoMetadataPanelValuesRemainIndependentOfVideoDetails()
    {
        var panel = new MetadataPanelViewModel();
        var photo = new ImageEntry
        {
            FilePath = @"C:\Photos\photo.jpg",
            FileName = "photo.jpg",
            Width = 1920,
            Height = 1080,
            MediaType = MediaType.Image
        };

        panel.ShowMetadata(photo);

        Assert.Equal("1920 x 1080", panel.Dimensions);
        Assert.Equal("", panel.VideoDuration);
        Assert.Equal("", panel.VideoBitrate);
        Assert.Equal("", panel.VideoTitle);
        Assert.Equal("", panel.VideoAlbum);
        Assert.Equal("", panel.VideoYear);
        Assert.Equal("", panel.VideoMetadataError);
    }

    [Fact]
    public async Task ThumbnailRequestsHonorPreCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsThumbnailService.GetThumbnailStreamAsync(@"D:\clip.mp4", 180, cancellation.Token));
    }
}
