// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Services;
using System.Numerics;
using Windows.Graphics.DirectX;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class EditorClippingTests
{
    [Theory]
    [InlineData(0, 0, 0, 1, true, false)]
    [InlineData(1f / 255, 0, 0, 1, false, false)]
    [InlineData(.5f / 255, 0, 0, 1, true, false)]
    [InlineData(254f / 255, 0, 0, 1, false, false)]
    [InlineData(254.5f / 255, 0, 0, 1, false, true)]
    [InlineData(1, 0, 0, 1, false, true)]
    [InlineData(0, 1, 0, 1, false, true)]
    [InlineData(0, 0, 1, 1, false, true)]
    [InlineData(.5f, 0, 0, .5f, false, true)]
    [InlineData(0, 0, 0, .5f, true, false)]
    [InlineData(0, 0, 0, 0, false, false)]
    public void AnalysisUsesStraightRgbSdrEndpointsAndIgnoresTransparentPixels(
        float red, float green, float blue, float alpha, bool shadows, bool highlights)
    {
        var result = EditorPreviewAnalysis.Calculate([red, green, blue, alpha]);
        Assert.Equal(shadows, result.HasShadows);
        Assert.Equal(highlights, result.HasHighlights);
        Assert.Equal(alpha == 0 ? 0f : 1f, result.Bins.Sum());
    }

    [Fact]
    public void ShaderMarksChannelWhiteAndNeutralBlackPreservingAlphaAndUnclippedColors()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        byte[] pixels =
        [
            0, 0, 0, 255, 1, 1, 1, 255, 254, 254, 254, 255,
            255, 255, 255, 255, 0, 0, 255, 255,
            128, 0, 0, 128, 0, 0, 0, 128, 0, 0, 0, 0
        ];
        using var source = Bitmap(device, pixels);
        var original = Render(device, source, new(), false, false);
        var both = Render(device, source, new(), true, true);
        byte[] expected =
        [
            255, 0, 0, 255, 1, 1, 1, 255, 254, 254, 254, 255,
            0, 0, 255, 255, 0, 0, 255, 255,
            0, 0, 128, 128, 128, 0, 0, 128, 0, 0, 0, 0
        ];
        Assert.Equal(expected, both);
        var highlights = Render(device, source, new(), false, true);
        var shadows = Render(device, source, new(), true, false);
        Assert.Equal(pixels[..4], highlights[..4]);
        Assert.Equal(pixels[12..16], shadows[12..16]);
        Assert.Equal(pixels, Render(device, source, new(), false, false));
        Assert.Equal(pixels, original);
        Assert.Equal(pixels, source.GetPixelBytes());
    }

    [Fact]
    public void DiagnosticsFollowEditedGraphCurvesReplacementSourceAndRawSdrWithoutDoubleAdjusting()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = Bitmap(device, [200, 200, 200, 255]);
        var parameters = new EditParameters { Exposure = 1 };
        Assert.False(EditorPreviewAnalysis.Render(source, new(), false).HasHighlights);
        Assert.True(EditorPreviewAnalysis.Render(source, parameters, false).HasHighlights);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Render(device, source, parameters, true, true));
        parameters.ToneCurves.Rgb = [new(0, 0), new(1, .5)];
        Assert.False(EditorPreviewAnalysis.Render(source, parameters, false).HasHighlights);
        Assert.NotEqual(new byte[] { 0, 0, 255, 255 }, Render(device, source, parameters, true, true));
        using var retouched = Bitmap(device, [0, 0, 0, 255]);
        Assert.True(EditorPreviewAnalysis.Render(retouched, new(), false).HasShadows);
        Assert.False(EditorPreviewAnalysis.Render(source, new(), false).HasShadows);
        Assert.False(EditorPreviewAnalysis.Render(source, parameters, true).HasHighlights);
        using var rawTarget = new CanvasRenderTarget(device, 1, 1, 96);
        using (var session = rawTarget.CreateDrawingSession())
            ImageEditRenderPass.DrawRawPreview(session, source, Matrix3x2.Identity, true, true);
        Assert.Equal(source.GetPixelBytes(), rawTarget.GetPixelBytes());
    }

    [Fact]
    public void ComparisonMarksOnlyAfterAndRotationPaddingIsNotClipped()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var source = Bitmap(device, [255, 255, 255, 255, 255, 255, 255, 255]);
        using var target = new CanvasRenderTarget(device, 4, 1, 96);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            EditorComparisonRenderer.Draw(session, source, source, new(), EditorComparisonMode.SideBySide,
                new EditorViewport(), new(4, 1), .5f, true, true);
        }
        Assert.Equal(new byte[] { 255, 255, 255, 255, 255, 255, 255, 255,
            0, 0, 255, 255, 0, 0, 255, 255 }, target.GetPixelBytes());
        var rotated = EditorPreviewAnalysis.Render(source, new() { RotationAngle = 45 }, false);
        Assert.False(rotated.HasShadows);
        Assert.True(rotated.HasHighlights);
    }

    private static CanvasBitmap Bitmap(CanvasDevice device, byte[] pixels) =>
        CanvasBitmap.CreateFromBytes(device, pixels, pixels.Length / 4, 1,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);

    private static byte[] Render(CanvasDevice device, CanvasBitmap source, EditParameters parameters,
        bool shadows, bool highlights)
    {
        using var target = new CanvasRenderTarget(device, (float)source.Size.Width, 1, 96);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            ImageEditRenderPass.Draw(session, source, parameters,
                new((float)source.Size.Width, 1), Matrix3x2.Identity, shadows, highlights);
        }
        return target.GetPixelBytes();
    }
}
