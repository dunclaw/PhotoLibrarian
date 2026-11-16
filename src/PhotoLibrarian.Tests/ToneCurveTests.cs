// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using PhotoLibrarian.Views;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ToneCurveTests
{
    [Fact]
    public void SplineInterpolatesPointsSmoothlyAndIsNotPiecewiseLinear()
    {
        ToneCurvePoint[] points = [new(0, 0), new(.3, .15), new(.7, .8), new(1, 1)];
        foreach (var point in points)
            Assert.Equal(point.Y, ToneCurves.Evaluate(points, point.X), 12);
        Assert.True(Math.Abs(ToneCurves.Evaluate(points, .15) - .075) > .005);
        const double step = 1e-6;
        foreach (var point in points[1..^1])
        {
            var leftSlope = (point.Y - ToneCurves.Evaluate(points, point.X - step)) / step;
            var rightSlope = (ToneCurves.Evaluate(points, point.X + step) - point.Y) / step;
            Assert.InRange(Math.Abs(leftSlope - rightSlope), 0, .0001);
        }
    }

    [Fact]
    public void SplineCannotOvershootOrReverseWithFlatOrUnevenlySpacedPoints()
    {
        ToneCurvePoint[][] curves =
        [
            [new(0, .1), new(1, .9)],
            [new(0, 0), new(.01, .8), new(.6, .8), new(1, 1)],
            [new(0, .2), new(.3, .2), new(.7, .2), new(1, .2)],
            [new(0, 0), new(.5, .1), new(Math.BitIncrement(.5), .9), new(1, 1)]
        ];
        foreach (var points in curves)
        {
            var previous = points[0].Y;
            for (var segment = 1; segment < points.Length; segment++)
            for (var sample = 0; sample <= 1000; sample++)
            {
                var left = points[segment - 1];
                var right = points[segment];
                var x = left.X + (right.X - left.X) * sample / 1000;
                var y = ToneCurves.Evaluate(points, x);
                Assert.InRange(y, left.Y, right.Y);
                Assert.True(y >= previous - 1e-14);
                previous = y;
            }
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(.5, .5)]
    [InlineData(1, 1)]
    public void DraggingNearNeighborOrEndpointRemovesOnlyTheInteriorPoint(double x, double y)
    {
        var points = new List<ToneCurvePoint>
        {
            new(0, 0), new(.25, .3), new(.5, .5), new(1, 1)
        };
        var index = x == 1 ? 2 : 1;
        Assert.True(ImageEditorView.TryMergeCurvePoint(points, index, new(x + .01, y), 200, 200));
        Assert.Equal(3, points.Count);
        Assert.Equal(new(0, 0), points[0]);
        Assert.Equal(new(1, 1), points[^1]);
        new ToneCurves { Rgb = points }.Validate();
    }

    [Fact]
    public void DraggingAwayFromNeighborsOrDraggingAnEndpointDoesNotRemovePoints()
    {
        var points = new List<ToneCurvePoint> { new(0, 0), new(.5, .5), new(1, 1) };
        Assert.False(ImageEditorView.TryMergeCurvePoint(points, 1, new(.5, .5), 200, 200));
        Assert.False(ImageEditorView.TryMergeCurvePoint(points, 0, new(.5, .5), 200, 200));
        Assert.False(ImageEditorView.TryMergeCurvePoint(points, 2, new(.5, .5), 200, 200));
        Assert.Equal(3, points.Count);
    }

    [Fact]
    public void DraggingToAnEndpointCanRemoveAPointBeyondOtherInteriorPoints()
    {
        var points = new List<ToneCurvePoint>
        {
            new(0, 0), new(.25, .3), new(.5, .5), new(.75, .7), new(1, 1)
        };
        Assert.True(ImageEditorView.TryMergeCurvePoint(points, 2, new(0, 0), 200, 200));
        Assert.Equal(new ToneCurvePoint[] { new(0, 0), new(.25, .3), new(.75, .7), new(1, 1) }, points);
    }

    [Fact]
    public void CurvesAreMonotoneAndRoundTripWithoutChangingOldRecipes()
    {
        var old = EditParametersSerializer.ReadFromXmp(XmpMetaFactory.Create());
        Assert.True(old.ToneCurves.IsIdentity);
        Assert.False(old.HasAdjustments);
        var curves = new ToneCurves
        {
            Rgb = [new(0, .1), new(.3, .6), new(.7, .6), new(1, .9)],
            Red = [new(0, 0), new(.5, .2), new(1, 1)]
        };
        curves.Validate();
        var last = 0d;
        for (var i = 0; i <= 1000; i++)
        {
            var value = ToneCurves.Evaluate(curves.Rgb, i / 1000d);
            Assert.True(value >= last);
            last = value;
        }
        var parameters = new EditParameters { ToneCurves = curves };
        var copy = parameters.Clone();
        copy.ToneCurves.Red[1] = new(.5, .4);
        Assert.Equal(.2, parameters.ToneCurves.Red[1].Y);
        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, parameters);
        Assert.True(curves.ContentEquals(EditParametersSerializer.ReadFromXmp(xmp).ToneCurves));
        EditParametersSerializer.ClearFromXmp(xmp);
        Assert.False(EditParametersSerializer.ReadFromXmp(xmp).HasAdjustments);
        parameters.ToneCurves = new() { Rgb = [new(0, 0), new(.5, .5), new(1, 1)] };
        EditParametersSerializer.WriteToXmp(xmp, parameters);
        Assert.True(parameters.ToneCurves.ContentEquals(EditParametersSerializer.ReadFromXmp(xmp).ToneCurves));
    }

    [Fact]
    public void InvalidAndUnknownCurvesAreRejected()
    {
        var curves = new ToneCurves { Green = [new(0, 0), new(.5, .8), new(.7, .2), new(1, 1)] };
        Assert.Throws<InvalidDataException>(curves.Validate);
        curves.Green = [new(0, 0), new(.5, .5), new(.5, .7), new(1, 1)];
        Assert.Throws<InvalidDataException>(curves.Validate);
        var xmp = XmpMetaFactory.Create();
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(EditParameters.XmpNamespace, EditParameters.XmpPrefix);
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:ToneCurveVersion", "2");
        Assert.Throws<NotSupportedException>(() => EditParametersSerializer.ReadFromXmp(xmp));
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:ToneCurveVersion", "1");
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:ToneCurvesV1", "{}");
        Assert.Throws<InvalidDataException>(() => EditParametersSerializer.ReadFromXmp(xmp));
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:ToneCurvesV1",
            "{\"Rgb\":[{\"X\":0},{\"X\":1,\"Y\":1}],\"Red\":[],\"Green\":[],\"Blue\":[]}");
        Assert.Throws<InvalidDataException>(() => EditParametersSerializer.ReadFromXmp(xmp));
    }

    [Fact]
    public async Task CurveGesturesAreUndoableAndSaveEmbeddedWithoutChangingPixels()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"curve-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "photo.jpg");
            await WicTestImage.CreateAsync(path, 16, 12, jpeg: true);
            var originalPixels = await ReadPixelsAsync(path);
            var vm = new ImageEditorViewModel(new OriginalBackupService(Path.Combine(folder, "backups")));
            await vm.OpenForEditAsync(new ImageEntry { FilePath = path, FileName = "photo.jpg" });
            vm.BeginEdit("Tone curve");
            var curves = new ToneCurves { Rgb = [new(0, 0), new(.5, .3), new(1, 1)] };
            vm.SetToneCurves(curves);
            curves.Rgb[1] = new(.5, .4);
            vm.SetToneCurves(curves);
            vm.EndEdit();
            Assert.Single(vm.UndoHistory);
            vm.UndoCommand.Execute(null);
            Assert.True(vm.GetCurrentParameters().ToneCurves.IsIdentity);
            vm.RedoCommand.Execute(null);
            vm.BeginEdit("Tone curve");
            var dragged = curves.Clone();
            dragged.Rgb[1] = new(.98, .98);
            vm.SetToneCurves(dragged);
            Assert.True(ImageEditorView.TryMergeCurvePoint(dragged.Rgb, 1, new(1, 1), 200, 200));
            vm.SetToneCurves(dragged);
            vm.EndEdit();
            Assert.Equal(2, vm.UndoHistory.Count);
            vm.UndoCommand.Execute(null);
            Assert.Single(vm.UndoHistory);
            Assert.True(curves.ContentEquals(vm.GetCurrentParameters().ToneCurves));
            Assert.True(await vm.SaveAsync((_, _) => throw new InvalidOperationException("Must not bake")));
            Assert.Equal(originalPixels, await ReadPixelsAsync(path));
            Assert.True(curves.ContentEquals(ImageEditService.ReadEditParameters(path).ToneCurves));
            vm.ResetAllCommand.Execute(null);
            Assert.True(await vm.SaveAsync((_, _) => throw new InvalidOperationException("Must not bake")));
            Assert.True(ImageEditService.ReadEditParameters(path).ToneCurves.IsIdentity);
            Assert.Equal(originalPixels, await ReadPixelsAsync(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void HistogramReflectsChannelCurve()
    {
        var pixels = new byte[] { 128, 128, 128, 255 };
        var baseline = ImageHistogram.Calculate(pixels, new());
        var parameters = new EditParameters
        {
            ToneCurves = new() { Green = [new(0, 0), new(.5, .1), new(1, 1)] }
        };
        var adjusted = ImageHistogram.Calculate(pixels, parameters);
        Assert.NotEqual(Array.IndexOf(baseline, 1), Array.IndexOf(adjusted, 1));
    }

    [Fact]
    public async Task ResizedExportUsesCurveRecipeAndLeavesOriginalUnchanged()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"curve-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "photo.png");
            var pixels = Enumerable.Repeat(new byte[] { 128, 128, 128, 255 }, 64).SelectMany(x => x).ToArray();
            await RenderQualityFixtures.WriteImageAsync(path, 8, 8, pixels);
            var originalPixels = await RenderQualityFixtures.ReadPngPixelsAsync(path);
            var recipe = new EditParameters
            {
                ToneCurves = new() { Red = [new(0, 0), new(.5, .1), new(1, 1)] }
            };
            await new ImageEditService(new OriginalBackupService(Path.Combine(folder, "backups")))
                .SaveEditParametersAsync(path, recipe);
            var unsupported = await ImageExportService.ExportAsync([path], folder, new());
            Assert.Empty(unsupported.ExportedPaths);
            Assert.Contains("cannot render tone curve", Assert.Single(unsupported.Failures).Message);
            var result = await ImageExportService.ExportAsync([path], folder, new() { Quality = 100 },
                renderRecipe: async (source, parameters, token) =>
                {
                    var rendered = await ImageEditRenderer.RenderToPixelsAsync(source, parameters);
                    return new(rendered.Width, rendered.Height, rendered.Pixels);
                });
            Assert.Empty(result.Failures);
            await using var stream = File.OpenRead(Assert.Single(result.ExportedPaths));
            var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
            var output = (await decoder.GetPixelDataAsync()).DetachPixelData();
            Assert.True(output[2] + 30 < output[1]);
            Assert.Equal(originalPixels, await RenderQualityFixtures.ReadPngPixelsAsync(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task RasterRenderingUsesTheSplineRatherThanStraightSegments()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"curve-spline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "photo.png");
            var pixels = Enumerable.Repeat(new byte[] { 64, 64, 64, 255 }, 64).SelectMany(x => x).ToArray();
            await RenderQualityFixtures.WriteImageAsync(path, 8, 8, pixels);
            var recipe = new EditParameters
            {
                ToneCurves = new() { Red = [new(0, 0), new(.5, .2), new(1, 1)] }
            };
            var rendered = await ImageEditRenderer.RenderToPixelsAsync(path, recipe);
            var splineRed = 255 * ToneCurves.Evaluate(recipe.ToneCurves.Red, 64 / 255d);
            var linearRed = 64 * .4;
            Assert.True(Math.Abs(splineRed - linearRed) > 3);
            for (var offset = 0; offset < rendered.Pixels.Length; offset += 4)
            {
                Assert.InRange(Math.Abs(rendered.Pixels[offset + 2] - splineRed), 0, 1);
                Assert.Equal(64, rendered.Pixels[offset]);
                Assert.Equal(64, rendered.Pixels[offset + 1]);
                Assert.Equal(255, rendered.Pixels[offset + 3]);
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static async Task<byte[]> ReadPixelsAsync(string path)
    {
        await using var file = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(file.AsRandomAccessStream());
        return (await decoder.GetPixelDataAsync()).DetachPixelData();
    }
}
