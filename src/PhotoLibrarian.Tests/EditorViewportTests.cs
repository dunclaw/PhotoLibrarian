using PhotoLibrarian.Services;
using System.Numerics;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class EditorViewportTests
{
    private static EditorViewport Create(float canvasW = 400, float canvasH = 300, float imageW = 800, float imageH = 600)
    {
        var viewport = new EditorViewport();
        viewport.SetCanvasSize(new Vector2(canvasW, canvasH));
        viewport.SetContentSize(new Vector2(imageW, imageH), resetView: true);
        return viewport;
    }

    [Fact]
    public void StartsInFitModeCentered()
    {
        var viewport = Create(canvasW: 500);

        Assert.True(viewport.IsFit);
        Assert.Equal(0.5f, viewport.Scale, 3);
        Assert.Equal(new Vector2(50, 0), viewport.ContentOrigin);
    }

    [Fact]
    public void ZoomIn_KeepsAnchorPointStationary()
    {
        var viewport = Create();
        var anchor = new Vector2(100, 75);
        var contentPointBefore = (anchor - viewport.ContentOrigin) / viewport.Scale;

        viewport.ZoomIn(anchor);

        var contentPointAfter = (anchor - viewport.ContentOrigin) / viewport.Scale;
        Assert.False(viewport.IsFit);
        Assert.Equal(0.5f * EditorViewport.ZoomStep, viewport.Scale, 3);
        Assert.Equal(contentPointBefore.X, contentPointAfter.X, 2);
        Assert.Equal(contentPointBefore.Y, contentPointAfter.Y, 2);
    }

    [Fact]
    public void ZoomOut_StopsAtFitAndReturnsToFitMode()
    {
        var viewport = Create();
        viewport.ZoomTo(2f);

        for (var i = 0; i < 20; i++) viewport.ZoomOut();

        Assert.True(viewport.IsFit);
        Assert.Equal(viewport.FitScale, viewport.Scale, 3);
    }

    [Fact]
    public void ZoomIn_ClampsToMaxScale()
    {
        var viewport = Create();

        for (var i = 0; i < 50; i++) viewport.ZoomIn();

        Assert.Equal(EditorViewport.MaxScale, viewport.Scale, 3);
    }

    [Fact]
    public void PanBy_IsClampedToContentEdges()
    {
        var viewport = Create();
        viewport.ZoomTo(1f);

        viewport.PanBy(new Vector2(10_000, 10_000));
        Assert.Equal(Vector2.Zero, viewport.ContentOrigin);

        viewport.PanBy(new Vector2(-20_000, -20_000));
        Assert.Equal(new Vector2(400 - 800, 300 - 600), viewport.ContentOrigin);
    }

    [Fact]
    public void PanBy_IsIgnoredInFitMode()
    {
        var viewport = Create();
        var origin = viewport.ContentOrigin;

        viewport.PanBy(new Vector2(50, 50));

        Assert.Equal(origin, viewport.ContentOrigin);
    }

    [Fact]
    public void SmallImage_CanZoomOutToActualSize()
    {
        var viewport = Create(imageW: 100, imageH: 75);
        viewport.ActualSizeScale = 1f;

        viewport.ZoomTo(1f);

        Assert.False(viewport.IsFit);
        Assert.Equal(1f, viewport.Scale, 3);
        Assert.Equal(new Vector2(150, 112.5f), viewport.ContentOrigin);
    }
}
