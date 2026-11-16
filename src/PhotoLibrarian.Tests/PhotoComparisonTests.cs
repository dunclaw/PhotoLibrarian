// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PhotoComparisonTests
{
    [Fact]
    public void SelectionValidation_DoesNotSilentlyDropVideosOrExtraCandidates()
    {
        var first = Photo("first.jpg");
        var second = Photo("second.nef");
        var third = Photo("third.gif");
        Assert.False(PhotoComparisonSession.CanOpen([]));
        Assert.False(PhotoComparisonSession.CanOpen([first]));
        Assert.True(PhotoComparisonSession.CanOpen([first, second]));
        Assert.True(PhotoComparisonSession.CanOpen([first, second, third]));
        third.MediaType = MediaType.Video;
        Assert.False(PhotoComparisonSession.CanOpen([first, second, third]));
        Assert.False(PhotoComparisonSession.CanOpen([first, Photo("FIRST.JPG")]));
        Assert.True(PhotoComparisonSession.CanOpen(
            Enumerable.Range(0, 1000).Select(i => Photo($"{i}.jpg")).ToArray()));
    }

    [Fact]
    public void RemovalAndRestore_AreSessionOnlyAndPreserveOriginalOrder()
    {
        var first = Photo("first.jpg");
        var second = Photo("second.jpg");
        var third = Photo("third.jpg");
        var selected = new List<ImageEntry> { third, first, second };
        var session = new PhotoComparisonSession(selected);
        Assert.True(session.Remove(first));
        Assert.False(session.Remove(first));
        Assert.Equal(new[] { third, second }, session.Candidates);
        Assert.Equal(new[] { third, first, second }, selected);
        selected.Clear();
        session.Remove(third);
        session.Remove(second);
        Assert.Empty(session.Candidates);
        session.Restore();
        Assert.Equal(new[] { third, first, second }, session.Candidates);
        Assert.Equal("first.jpg", first.FileName);
    }

    [Theory]
    [InlineData(6000, 4000, 0.25, 0.75)]
    [InlineData(4000, 6000, 0.75, 0.25)]
    [InlineData(30000, 20000, 0.5, 0.5)]
    public void LinkedCenter_MapsSameRelativeLocationAtSamePixelScale(
        double width, double height, double centerX, double centerY)
    {
        var view = new ComparisonViewport(2, centerX, centerY);
        var (left, top) = ComparisonViewport.Position(width, height, 700, 500,
            view.Scale, view.CenterX, view.CenterY);
        Assert.Equal(centerX * width, (350 - left) / view.Scale, 8);
        Assert.Equal(centerY * height, (250 - top) / view.Scale, 8);
    }

    private static ImageEntry Photo(string name) =>
        new() { FilePath = @"C:\SyntheticPhotos\" + name, FileName = name, MediaType = MediaType.Image };
}
