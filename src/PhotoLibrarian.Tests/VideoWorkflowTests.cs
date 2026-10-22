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

        viewer.OpenExportedVideo(@"D:\elsewhere\result.mp4");

        Assert.True(viewer.IsOpen);
        Assert.True(viewer.IsVideo);
        Assert.Equal(@"D:\elsewhere\result.mp4", viewer.VideoPath);
        Assert.Equal("result.mp4", viewer.Title);
        Assert.Equal(viewer.CurrentEntry, viewer.Images[1]);
        Assert.Equal(original, viewer.Images[0]);
        Assert.Equal(next, viewer.Images[2]);

        // A watcher refresh must not jump playback back to the original while indexing catches up.
        viewer.UpdateLibraryImages([original, next]);
        Assert.Equal(@"D:\elsewhere\result.mp4", viewer.CurrentEntry!.FilePath);
        Assert.Equal(@"D:\elsewhere\result.mp4", viewer.VideoPath);
        viewer.OpenExportedVideo(@"D:\elsewhere\result.mp4");
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
        var stabilize = Assert.Single(buttons);
        Assert.Equal("OnStabilizeVideoClick", (string?)stabilize.Attribute("Click"));
        var viewer = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "ImageViewerOverlay.xaml"));
        Assert.DoesNotContain(viewer.Descendants(), element => (string?)element.Attribute("Click") == "OnStabilizeVideo");
        var dialog = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "VideoStabilizationDialog.xaml"));
        var autoCrop = Assert.Single(dialog.Descendants(presentation + "CheckBox"));
        Assert.Equal("StabilizationAutoCrop", (string?)autoCrop.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal("True", (string?)autoCrop.Attribute("IsChecked"));
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
