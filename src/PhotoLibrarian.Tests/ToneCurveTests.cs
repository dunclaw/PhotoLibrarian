// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ToneCurveTests
{
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
    public async Task CurveGesturesAreUndoableAndSaveAsSidecarWithoutChangingPhoto()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"curve-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "photo.jpg");
            byte[] original = [1, 2, 3];
            await File.WriteAllBytesAsync(path, original);
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
            Assert.True(await vm.SaveAsync((_, _) => throw new InvalidOperationException("Must not bake")));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.True(curves.ContentEquals(ImageEditService.ReadEditParameters(path).ToneCurves));
            vm.ResetAllCommand.Execute(null);
            Assert.True(await vm.SaveAsync((_, _) => throw new InvalidOperationException("Must not bake")));
            Assert.True(ImageEditService.ReadEditParameters(path).ToneCurves.IsIdentity);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
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
            var original = await File.ReadAllBytesAsync(path);
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
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
