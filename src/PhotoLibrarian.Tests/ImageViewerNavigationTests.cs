// Copyright (c) Duncan Lawler. All Rights Reserved.

using PhotoLibrarian.Core.Models;
using PhotoLibrarian.ViewModels;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ImageViewerNavigationTests
{
    [Fact]
    public async Task InformationPanePreference_SurvivesNavigationAndReopenButDoesNotHideGridPane()
    {
        var first = Video("first");
        var second = Video("second");
        var viewer = new ImageViewerViewModel();
        Assert.True(viewer.IsInformationPaneVisible);
        Assert.True(viewer.ShouldShowInformationPane);
        viewer.OpenImage(first, [first, second]);
        viewer.ToggleInformationPaneCommand.Execute(null);
        Assert.False(viewer.ShouldShowInformationPane);

        await viewer.NextImageCommand.ExecuteAsync(null);
        viewer.UpdateLibraryImages([second]);
        Assert.False(viewer.ShouldShowInformationPane);
        viewer.CloseCommand.Execute(null);
        Assert.True(viewer.ShouldShowInformationPane);
        Assert.False(viewer.IsInformationPaneVisible);

        viewer.OpenImage(first, [first]);
        Assert.False(viewer.ShouldShowInformationPane);
        viewer.ToggleInformationPaneCommand.Execute(null);
        Assert.True(viewer.ShouldShowInformationPane);
        Assert.True(new ImageViewerViewModel().IsInformationPaneVisible);
    }

    [Fact]
    public void InformationPaneLayout_NotifiesOnToggleOpenAndClose()
    {
        var viewer = new ImageViewerViewModel();
        var visibility = new List<bool>();
        viewer.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ImageViewerViewModel.ShouldShowInformationPane))
                visibility.Add(viewer.ShouldShowInformationPane);
        };
        viewer.OpenImage(Video("first"), [Video("first")]);
        viewer.ToggleInformationPaneCommand.Execute(null);
        viewer.CloseCommand.Execute(null);
        Assert.Equal(new[] { true, false, true }, visibility);
    }

    [Fact]
    public async Task Navigation_NotifiesSelectionAndWrapsInBothDirections()
    {
        var first = Video("first");
        var second = Video("second");
        var viewer = new ImageViewerViewModel();
        var selections = new List<ImageEntry?>();
        viewer.CurrentEntryChanged += selections.Add;

        viewer.OpenImage(first, [first, second]);
        await viewer.NextImageCommand.ExecuteAsync(null);
        Assert.Same(second, viewer.CurrentEntry);
        Assert.Equal(second.FilePath, viewer.VideoPath);
        Assert.Equal("2 / 2", viewer.ImageInfo);

        await viewer.NextImageCommand.ExecuteAsync(null);
        Assert.Same(first, viewer.CurrentEntry);
        await viewer.PreviousImageCommand.ExecuteAsync(null);
        Assert.Same(second, viewer.CurrentEntry);
        viewer.CloseCommand.Execute(null);

        Assert.Equal(new ImageEntry?[] { first, second, first, second, null }, selections);
        Assert.Null(viewer.CurrentEntry);
        Assert.Null(viewer.VideoPath);
        Assert.False(viewer.IsVideo);
    }

    [Fact]
    public async Task UpdatedResults_PreserveCurrentPathAndUseNewNavigationOrder()
    {
        var first = Video("first");
        var second = Video("second");
        var third = Video("third");
        var viewer = new ImageViewerViewModel();
        viewer.OpenImage(second, [first, second, third]);
        var refreshedSecond = Video("SECOND");
        ImageEntry? notified = null;
        viewer.CurrentEntryChanged += entry => notified = entry;

        viewer.UpdateLibraryImages([third, refreshedSecond, first]);

        Assert.Same(refreshedSecond, viewer.CurrentEntry);
        Assert.Same(refreshedSecond, notified);
        Assert.Equal("2 / 3", viewer.ImageInfo);
        await viewer.NextImageCommand.ExecuteAsync(null);
        Assert.Same(first, viewer.CurrentEntry);
        await viewer.PreviousImageCommand.ExecuteAsync(null);
        Assert.Same(refreshedSecond, viewer.CurrentEntry);
    }

    [Fact]
    public async Task FilterExcludingCurrent_MovesToFirstResultWithoutAddingExcludedItem()
    {
        var excluded = Video("excluded");
        var first = Video("first");
        var second = Video("second");
        var viewer = new ImageViewerViewModel();
        viewer.OpenImage(excluded, [excluded, first, second]);
        var filtered = new List<ImageEntry> { first, second };

        viewer.UpdateLibraryImages(filtered);

        Assert.True(viewer.IsOpen);
        Assert.Same(first, viewer.CurrentEntry);
        Assert.Equal(first.FilePath, viewer.VideoPath);
        Assert.Equal("1 / 2", viewer.ImageInfo);
        Assert.Equal(new[] { first, second }, filtered);
        await viewer.PreviousImageCommand.ExecuteAsync(null);
        Assert.Same(second, viewer.CurrentEntry);
        await viewer.NextImageCommand.ExecuteAsync(null);
        Assert.Same(first, viewer.CurrentEntry);
    }

    [Fact]
    public async Task EmptyResults_CloseViewerAndCannotBeNavigated()
    {
        var first = Video("first");
        var viewer = new ImageViewerViewModel();
        viewer.OpenImage(first, [first]);
        var closed = false;
        viewer.CurrentEntryChanged += entry => closed = entry is null;

        viewer.UpdateLibraryImages([]);
        await viewer.NextImageCommand.ExecuteAsync(null);
        await viewer.PreviousImageCommand.ExecuteAsync(null);
        await viewer.ReloadCurrentImageAsync();

        Assert.True(closed);
        Assert.False(viewer.IsOpen);
        Assert.Null(viewer.CurrentEntry);
        Assert.Null(viewer.VideoPath);
        Assert.Null(viewer.CurrentImage);
    }

    [Fact]
    public void UpdatedResults_DoNotReopenClosedViewer()
    {
        var first = Video("first");
        var viewer = new ImageViewerViewModel();
        viewer.OpenImage(first, [first]);
        viewer.CloseCommand.Execute(null);

        viewer.UpdateLibraryImages([Video("second")]);

        Assert.False(viewer.IsOpen);
        Assert.Null(viewer.CurrentEntry);
    }

    [Fact]
    public async Task ViewerSnapshotsInputs_AndResolvesEntriesByPath()
    {
        var first = Video("first");
        var second = Video("second");
        var images = new List<ImageEntry> { first, second };
        var viewer = new ImageViewerViewModel();

        viewer.OpenImage(Video("SECOND"), images);
        Assert.Same(second, viewer.CurrentEntry);
        images.Clear();
        await viewer.NextImageCommand.ExecuteAsync(null);
        Assert.Same(first, viewer.CurrentEntry);

        var filtered = new List<ImageEntry> { second, first };
        viewer.UpdateLibraryImages(filtered);
        filtered.Clear();
        await viewer.PreviousImageCommand.ExecuteAsync(null);
        Assert.Same(second, viewer.CurrentEntry);
    }

    [Fact]
    public void OpenImage_OutsideResultsShowsRequestedEntryWithoutMutatingResults()
    {
        var first = Video("first");
        var copy = Video("copy");
        var images = new List<ImageEntry> { first };
        var viewer = new ImageViewerViewModel();

        viewer.OpenImage(copy, images);

        Assert.Same(copy, viewer.CurrentEntry);
        Assert.Equal(copy.FilePath, viewer.VideoPath);
        Assert.Equal("2 / 2", viewer.ImageInfo);
        Assert.Single(images);
    }

    [Fact]
    public async Task GridNavigation_UsesDisplayedGroupAndSortOrder()
    {
        var first = new ImageThumbnailViewModel(Video("first"));
        var second = new ImageThumbnailViewModel(Video("second"));
        var third = new ImageThumbnailViewModel(Video("third"));
        var grid = new ImageGridViewModel(null!, null!, null!, null!, null!, null!, null!);
        grid.Images.Add(first);
        grid.Images.Add(second);
        grid.Images.Add(third);
        grid.GroupedImages.Add(new PhotoGroup { Header = "A", Items = [third, first] });
        grid.GroupedImages.Add(new PhotoGroup { Header = "B", Items = [second] });

        var viewer = new ImageViewerViewModel();
        viewer.OpenImage(third.Entry, grid.GetViewerImages());

        Assert.Equal(new[] { third.Entry, first.Entry, second.Entry }, grid.GetViewerImages());
        await viewer.NextImageCommand.ExecuteAsync(null);
        Assert.Same(first.Entry, viewer.CurrentEntry);
        await viewer.NextImageCommand.ExecuteAsync(null);
        Assert.Same(second.Entry, viewer.CurrentEntry);
    }

    private static ImageEntry Video(string name) => new()
    {
        FilePath = $@"C:\Photos\{name}.mp4",
        FileName = $"{name}.mp4",
        MediaType = MediaType.Video
    };
}
