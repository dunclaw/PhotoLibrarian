// Copyright (c) Duncan Lawler. All Rights Reserved.

using PhotoLibrarian.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ViewerZoomScaleTests
{
    [Theory]
    [InlineData(6000, 4000, 1)]
    [InlineData(6000, 4000, 1.25)]
    [InlineData(4000, 6000, 1.5)]
    [InlineData(1500, 1000, 2)]
    [InlineData(100, 75, 3)]
    public void ActualSize_MapsDecodedPixelsToPhysicalDisplayPixels(int width, int height, double dpiScale)
    {
        var zoom = new ViewerZoomScale();
        zoom.Fit(ViewerZoomScale.FitScale(width, height, 800, 600));

        zoom.ActualSize(dpiScale);

        Assert.False(zoom.IsFit);
        Assert.True(zoom.IsActualSize);
        Assert.Equal(width, width * zoom.Scale * dpiScale, 9);
        Assert.Equal(height, height * zoom.Scale * dpiScale, 9);
    }

    [Fact]
    public void ActualSize_SurvivesPaneResizeAndMonitorScaleChanges()
    {
        var zoom = new ViewerZoomScale();
        zoom.ActualSize(1.25);

        zoom.UpdateViewport(0.4, 1.25);
        Assert.Equal(0.8, zoom.Scale);
        zoom.UpdateViewport(0.6, 2);
        Assert.Equal(0.5, zoom.Scale);
        Assert.True(zoom.IsActualSize);
    }

    [Fact]
    public void SmallImage_ActualSizeIsNotClampedUpToFit()
    {
        var zoom = new ViewerZoomScale();
        var fit = ViewerZoomScale.FitScale(100, 75, 800, 600);
        Assert.Equal(8, fit);

        zoom.ActualSize(2);
        zoom.UpdateViewport(fit, 2);

        Assert.Equal(0.5, zoom.Scale);
        Assert.Equal(0.5, zoom.MinimumScale(fit, 2));
    }

    [Fact]
    public void FitAfterActualSize_FollowsViewportAndNewImageResolution()
    {
        var zoom = new ViewerZoomScale();
        zoom.ActualSize(1.5);
        zoom.Fit(ViewerZoomScale.FitScale(6000, 4000, 900, 600));
        Assert.Equal(0.15, zoom.Scale);
        Assert.True(zoom.IsFit);
        Assert.False(zoom.IsActualSize);

        zoom.UpdateViewport(ViewerZoomScale.FitScale(6000, 4000, 600, 600), 2);
        Assert.Equal(0.1, zoom.Scale);
        zoom.Fit(ViewerZoomScale.FitScale(1500, 1000, 900, 600));
        Assert.Equal(0.6, zoom.Scale);
    }

    [Fact]
    public void ManualZoom_LeavesActualModeAndRetainsScaleOnResize()
    {
        var zoom = new ViewerZoomScale();
        zoom.ActualSize(2);
        zoom.ZoomTo(0.6);
        zoom.UpdateViewport(0.2, 1.25);

        Assert.False(zoom.IsFit);
        Assert.False(zoom.IsActualSize);
        Assert.Equal(0.6, zoom.Scale);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ActualSize_RejectsInvalidDisplayScale(double dpiScale) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ViewerZoomScale.ActualSizeScale(dpiScale));
}
