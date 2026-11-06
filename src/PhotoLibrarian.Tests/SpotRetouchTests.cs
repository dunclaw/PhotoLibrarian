// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Numerics;
using System.Xml.Linq;
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class SpotRetouchTests : IDisposable
{
    private const int Size = 128;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-retouch-{Guid.NewGuid():N}");

    public SpotRetouchTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static RetouchStroke Stroke(int radius = 8, params RetouchPoint[] points) =>
        new(Size, Size, radius, points.Length == 0 ? [new(64, 64)] : points);

    private static byte[] Surface(bool gradient)
    {
        var pixels = new byte[Size * Size * 4];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var offset = (y * Size + x) * 4;
            pixels[offset] = (byte)(gradient ? 30 + x : 70);
            pixels[offset + 1] = (byte)(gradient ? 40 + y : 120);
            pixels[offset + 2] = (byte)(gradient ? 20 + (x + y) / 2 : 180);
            pixels[offset + 3] = 255;
        }
        return pixels;
    }

    private static void Blemish(byte[] pixels, int left = 60, int right = 68)
    {
        for (var y = 61; y <= 67; y++)
        for (var x = left; x <= right; x++)
        {
            var offset = (y * Size + x) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 10;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SmoothFixture_RemovesBlemishWithOneLevelErrorAndNoOutsideChanges(bool gradient, bool drag)
    {
        var reference = Surface(gradient);
        var damaged = reference.ToArray();
        Blemish(damaged, 60, drag ? 85 : 68);
        var before = damaged.ToArray();
        var stroke = drag ? Stroke(8, new(64, 64), new(82, 64)) : Stroke();
        SpotRetouchProcessor.Apply(damaged, Size, Size, [stroke]);
        var changed = 0;
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var offset = (y * Size + x) * 4;
            var dx = x - Math.Clamp(x, 64, drag ? 82 : 64);
            var inside = dx * dx + (y - 64) * (y - 64) <= 64;
            for (var c = 0; c < 4; c++)
            {
                if (inside) Assert.InRange(Math.Abs(damaged[offset + c] - reference[offset + c]), 0, 1);
                else Assert.Equal(before[offset + c], damaged[offset + c]);
                if (damaged[offset + c] != before[offset + c]) changed++;
            }
        }
        Assert.True(changed > 100);
    }

    [Theory]
    [InlineData("edge")]
    [InlineData("texture")]
    [InlineData("alpha")]
    [InlineData("ring-alpha")]
    [InlineData("boundary")]
    [InlineData("extent")]
    public void UnsuitableRegions_AreRejectedWithoutAnyWrites(string kind)
    {
        var pixels = Surface(false);
        var stroke = Stroke();
        if (kind == "edge" || kind == "texture")
        {
            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var value = kind == "edge" ? (x < 64 ? 20 : 220) : ((x + y) % 2 == 0 ? 20 : 220);
                pixels[(y * Size + x) * 4] = (byte)value;
            }
        }
        if (kind == "alpha") pixels[(64 * Size + 64) * 4 + 3] = 128;
        if (kind == "ring-alpha") pixels[(64 * Size + 73) * 4 + 3] = 128;
        if (kind == "boundary") stroke = Stroke(8, new RetouchPoint(3, 64));
        if (kind == "extent")
            stroke = new(400, 400, 24, [new(30, 30), new(300, 30)]);
        var before = pixels.ToArray();
        Assert.Throws<InvalidDataException>(() => SpotRetouchProcessor.Apply(pixels, Size, Size, [stroke]));
        Assert.Equal(before, pixels);
    }

    [Fact]
    public void ResourceAndRecipeBounds_AreEnforced()
    {
        SpotRetouchProcessor.Validate(Enumerable.Repeat(Stroke(), 32).ToList());
        SpotRetouchProcessor.Validate([new(6000, 4000, 24, [new(60, 60)])]);
        SpotRetouchProcessor.Validate(Enumerable.Repeat(Stroke(), 256).ToList());
        Assert.Throws<InvalidDataException>(() => SpotRetouchProcessor.Validate([Stroke(25)]));
        Assert.Throws<InvalidDataException>(() => SpotRetouchProcessor.Validate([Stroke(1)]));
        SpotRetouchProcessor.Validate([new(12000, 8000, 8, [new(60, 60)])]);
        Assert.Throws<InvalidDataException>(() => SpotRetouchProcessor.Validate([Stroke(8, new RetouchPoint(-1, 3))]));
        Assert.Throws<InvalidDataException>(() => SpotRetouchProcessor.Validate([Stroke(8, new RetouchPoint[129])]));
        Assert.Throws<InvalidDataException>(() => SpotRetouchProcessor.Apply(
            Surface(false), Size, Size, [new(127, 128, 8, [new(64, 64)])]));
        var large = new byte[400 * 400 * 4];
        Array.Fill(large, (byte)255);
        var tooLong = new RetouchStroke(400, 400, 8, [new(30, 30), new(220, 30)]);
        Assert.Throws<InvalidDataException>(() => SpotRetouchProcessor.Apply(large, 400, 400, [tooLong]));
        var tooWide = new RetouchStroke(400, 400, 24, [new(40, 40), new(175, 40), new(175, 175), new(40, 175)]);
        Assert.Throws<InvalidDataException>(() => SpotRetouchProcessor.Apply(large, 400, 400, [tooWide]));
        var cancelled = new CancellationToken(canceled: true);
        Assert.Throws<OperationCanceledException>(() => SpotRetouchProcessor.Apply(Surface(false), Size, Size, [Stroke()], cancelled));
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("photo.JPEG")]
    [InlineData("photo.png")]
    [InlineData("photo.tif")]
    [InlineData("photo.TIFF")]
    [InlineData("photo.bmp")]
    public void SupportedRasterFormats_AreEligibleForTheExistingSafeSavePipeline(string path) =>
        Assert.True(ImageEditRenderer.IsSupported(path));

    [Fact]
    public void Recipes_RoundTripDeepCloneResetAndClearWithoutRemovingOtherMetadata()
    {
        var xmp = XmpMetaFactory.Create();
        const string dc = "http://purl.org/dc/elements/1.1/";
        xmp.SetProperty(dc, "dc:format", "image/png");
        var p = new EditParameters { RetouchStrokes = [Stroke()], Exposure = 0.1 };
        EditParametersSerializer.WriteToXmp(xmp, p);
        var read = EditParametersSerializer.ReadFromXmp(xmp);
        Assert.True(RetouchStroke.SequenceEqual(p.RetouchStrokes, read.RetouchStrokes));
        var clone = p.Clone();
        p.RetouchStrokes[0].Points[0] = new(2, 2);
        Assert.Equal(new RetouchPoint(64, 64), clone.RetouchStrokes[0].Points[0]);
        Assert.True(clone.HasAdjustments);
        clone.Reset();
        Assert.False(clone.HasAdjustments);
        EditParametersSerializer.ClearFromXmp(xmp);
        Assert.Empty(EditParametersSerializer.ReadFromXmp(xmp).RetouchStrokes);
        Assert.Equal("image/png", xmp.GetPropertyString(dc, "dc:format"));
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:SpotRetouchV1", "[null]");
        Assert.Throws<InvalidDataException>(() => EditParametersSerializer.ReadFromXmp(xmp));
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:SpotRetouchV1", "{invalid}");
        Assert.Throws<InvalidDataException>(() => EditParametersSerializer.ReadFromXmp(xmp));
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:SpotRetouchV1", " ");
        Assert.Throws<InvalidDataException>(() => EditParametersSerializer.ReadFromXmp(xmp));
    }

    [Theory]
    [InlineData(0, 0.25f)]
    [InlineData(90, 2f)]
    [InlineData(-90, 0.75f)]
    [InlineData(23, 8f)]
    [InlineData(180, 1f)]
    public void Mapping_InvertsRotationZoomPanAndLetterbox(double angle, float scale)
    {
        var size = new Vector2(128, 96); // Already EXIF-oriented; never invert EXIF a second time.
        var origin = new Vector2(-51, 37);
        var transform = EditorImageCoordinates.SourceToCanvas(size, angle, scale, origin);
        var sourcePoint = new Vector2(31.5f, 60.5f);
        Assert.True(EditorImageCoordinates.TryGetSourcePoint(Vector2.Transform(sourcePoint, transform),
            size, angle, scale, origin, out var point));
        Assert.Equal(new RetouchPoint(31, 60), point);
        Assert.False(EditorImageCoordinates.TryGetSourcePoint(Vector2.Transform(new(-10, -10), transform),
            size, angle, scale, origin, out _));
    }

    [Fact]
    public void Gesture_ClickDragCancelCaptureLossAndOverflowNeverLeakPartialEdits()
    {
        var gesture = new RetouchGesture();
        gesture.Begin(1, Size, Size, 8, new(60, 64));
        gesture.Add(new(64, 64));
        Assert.Null(gesture.Complete(2));
        var stroke = gesture.Complete(1);
        Assert.Equal(2, stroke!.Points.Length);
        Assert.Null(gesture.Complete(1));
        gesture.Begin(2, Size, Size, 4, new(32, 32));
        gesture.Cancel(); // Escape, pointer cancellation, capture loss, navigation and resize use this path.
        Assert.Null(gesture.Complete(2));
        gesture.Begin(3, Size, Size, 4, new(32, 32));
        Assert.Single(gesture.Complete(3)!.Points);
        gesture.Begin(4, Size, Size, 4, new(32, 32));
        Assert.Throws<InvalidDataException>(() => gesture.Add(new(-1, 32)));
        Assert.Null(gesture.Complete(4));
        gesture.Begin(5, Size, Size, 4, new(32, 32));
        for (var i = 1; i < 128; i++) gesture.Add(new(i % 2 + 32, 33));
        Assert.Throws<InvalidDataException>(() => gesture.Add(new(40, 40)));
        Assert.Null(gesture.Complete(5));
    }

    [Fact]
    public async Task History_PendingSidecarSaveAndRawGateUseExistingSafeWorkflow()
    {
        var path = Path.Combine(_directory, "history.png");
        await RenderQualityFixtures.WriteImageAsync(path, Size, Size, Surface(false));
        var backups = new OriginalBackupService(Path.Combine(_directory, "originals"));
        var vm = new ImageEditorViewModel(backups);
        await vm.OpenForEditAsync(new ImageEntry { FilePath = path, FileName = "history.png" });
        vm.AddRetouchStroke(Stroke());
        Assert.Equal(new[] { "Spot retouch" }, vm.UndoHistory);
        Assert.True(vm.HasChanges);
        vm.UndoSteps(1);
        Assert.Empty(vm.GetCurrentParameters().RetouchStrokes);
        Assert.False(vm.HasChanges);
        vm.RedoSteps(1);
        vm.ResetAllCommand.Execute(null);
        Assert.Empty(vm.GetCurrentParameters().RetouchStrokes);
        vm.UndoSteps(1);
        await new ImageEditService(backups).SaveEditParametersAsync(path, vm.GetCurrentParameters());
        var reopened = new ImageEditorViewModel(backups);
        await reopened.OpenForEditAsync(new ImageEntry { FilePath = path, FileName = "history.png" });
        Assert.Single(reopened.GetCurrentParameters().RetouchStrokes);
        using var device = new CanvasDevice();
        Assert.True(await reopened.SaveAsync((file, parameters) =>
            ImageEditRenderer.RenderToFileAsync(file, parameters, device)));
        Assert.Empty(ImageEditService.ReadEditParameters(path).RetouchStrokes);
        Assert.False(reopened.HasChanges);
        Assert.True(reopened.HasBackup);
        var rawPath = Path.Combine(_directory, "source.dng");
        await File.WriteAllBytesAsync(rawPath, [1, 2, 3]);
        await vm.OpenForEditAsync(new ImageEntry { FilePath = rawPath, FileName = "source.dng" });
        Assert.Throws<InvalidOperationException>(() => vm.AddRetouchStroke(Stroke()));
        Assert.False(ImageEditRenderer.IsSupported(rawPath));
        Assert.False(ImageEditRenderer.IsSupported("animated.gif"));
    }

    [Fact]
    public async Task MoreThan32Strokes_RoundTripUndoRedoAndSaveWithoutACountOrRecipeLengthLimit()
    {
        var path = Path.Combine(_directory, "many-strokes.png");
        await RenderQualityFixtures.WriteImageAsync(path, Size, Size, Surface(false));
        var backups = new OriginalBackupService(Path.Combine(_directory, "originals"));
        var vm = new ImageEditorViewModel(backups);
        await vm.OpenForEditAsync(new ImageEntry { FilePath = path, FileName = Path.GetFileName(path) });
        for (var i = 0; i < 80; i++)
            vm.AddRetouchStroke(Stroke(2, Enumerable.Repeat(new RetouchPoint(64, 64), 128).ToArray()));
        Assert.Equal(80, vm.UndoHistory.Count);
        await new ImageEditService(backups).SaveEditParametersAsync(path, vm.GetCurrentParameters());
        Assert.True(new FileInfo(Path.ChangeExtension(path, ".xmp")).Length > 160_000);
        Assert.Equal(80, ImageEditService.ReadEditParameters(path).RetouchStrokes.Count);
        vm.UndoSteps(50);
        Assert.Equal(30, vm.GetCurrentParameters().RetouchStrokes.Count);
        vm.RedoSteps(50);
        Assert.Equal(80, vm.GetCurrentParameters().RetouchStrokes.Count);
        using var device = new CanvasDevice();
        Assert.True(await vm.SaveAsync((file, parameters) =>
            ImageEditRenderer.RenderToFileAsync(file, parameters, device)));
        Assert.Empty(ImageEditService.ReadEditParameters(path).RetouchStrokes);
    }

    [Fact]
    public async Task FortyFiveMegapixelSource_RetouchesAtFullResolutionWithoutAnImageSizeGate()
    {
        const int width = 8256;
        const int height = 5504;
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);
        var center = new RetouchPoint(width / 2, height / 2);
        var offset = (center.Y * width + center.X) * 4;
        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 0;
        using var device = new CanvasDevice();
        using var source = CanvasBitmap.CreateFromBytes(device, pixels, width, height,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96,
            CanvasAlphaMode.Premultiplied);
        using var result = await ImageEditRenderer.CreateRetouchedAsync(device, source,
            [new RetouchStroke(width, height, 8, [center])]);
        Assert.NotNull(result);
        Assert.Equal((uint)width, result.SizeInPixels.Width);
        Assert.Equal((uint)height, result.SizeInPixels.Height);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, result.GetPixelBytes(center.X, center.Y, 1, 1));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, source.GetPixelBytes(center.X, center.Y, 1, 1));
    }

    [Fact]
    public void FullPhotoRibbon_ExposesRetouchAndBrushControlsAreOutsideAdjustments()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var ribbon = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "TopRibbon.xaml"));
        var command = Assert.Single(ribbon.Descendants().Where(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == "RibbonRetouch"));
        Assert.Equal("OnRetouchClick", (string?)command.Attribute("Click"));
        Assert.Equal("ViewerTools", (string?)command.Parent?.Attribute(x + "Name"));

        var editor = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "ImageEditorView.xaml"));
        var toolbar = Assert.Single(editor.Descendants().Where(element =>
            (string?)element.Attribute(x + "Name") == "RetouchToolbar"));
        Assert.Contains(toolbar.Descendants(), element => (string?)element.Attribute(x + "Name") == "RetouchRadius");
        Assert.DoesNotContain(toolbar.Ancestors(), element =>
            (string?)element.Attribute(x + "Name") == "AdjustmentsPanel");
        var canvas = Assert.Single(editor.Descendants().Where(element =>
            (string?)element.Attribute(x + "Name") == "EditCanvas"));
        Assert.Equal("OnEditCanvasPointerEntered", (string?)canvas.Attribute("PointerEntered"));
        Assert.Equal("OnEditCanvasPointerExited", (string?)canvas.Attribute("PointerExited"));
        var warning = Assert.Single(editor.Descendants().Where(element =>
            (string?)element.Attribute(x + "Name") == "RetouchStatus"));
        Assert.Equal((string?)canvas.Attribute("Grid.Row"), (string?)warning.Attribute("Grid.Row"));
        Assert.Equal("1", (string?)warning.Attribute("Canvas.ZIndex"));
    }

    [Fact]
    public async Task IncrementalPreviewAndFullRecipeReplayProduceIdenticalPixels()
    {
        var pixels = Surface(true);
        Blemish(pixels);
        using var device = new CanvasDevice();
        using var source = CanvasBitmap.CreateFromBytes(device, pixels, Size, Size,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96,
            CanvasAlphaMode.Premultiplied);
        var strokes = new[] { Stroke(), Stroke(4, new RetouchPoint(32, 32)) };
        using var first = await ImageEditRenderer.CreateRetouchedAsync(device, source, [strokes[0]]);
        using var incremental = await ImageEditRenderer.CreateRetouchedAsync(device, first!, [strokes[1]]);
        using var replayed = await ImageEditRenderer.CreateRetouchedAsync(device, source, strokes);
        Assert.Equal(replayed!.GetPixelBytes(), incremental!.GetPixelBytes());
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(6, 90)]
    [InlineData(8, 23)]
    public async Task PreviewSaveAndLosslessReopenAgreeAtSourceResolution(int orientation, double rotation)
    {
        var path = Path.Combine(_directory, $"source-{orientation}.{(orientation == 1 ? "png" : "jpg")}");
        var pixels = Surface(true);
        Blemish(pixels);
        await RenderQualityFixtures.WriteImageAsync(path, Size, Size, pixels, dpi: 300, orientation: (ushort)orientation);
        var originalBytes = await File.ReadAllBytesAsync(path);
        using var device = new CanvasDevice();
        using var source = await ImageEditRenderer.LoadOrientedAsync(device, path);
        // Use the actually oriented blemish position; orientation 6/8 rotate around the square's centre.
        var center = orientation switch { 6 => new RetouchPoint(63, 64), 8 => new(64, 63), _ => new(64, 64) };
        var p = new EditParameters { RetouchStrokes = [Stroke(10, center)], RotationAngle = rotation, Exposure = 0.1 };
        using var retouched = await ImageEditRenderer.CreateRetouchedAsync(device, source, p.RetouchStrokes);
        var extent = EditEffectGraph.ComputeOutputExtent(Size, Size, rotation);
        using var target = new CanvasRenderTarget(device, extent.Width, extent.Height, 96);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(orientation == 1 ? Microsoft.UI.Colors.Transparent : Microsoft.UI.Colors.White);
            ImageEditRenderPass.Draw(session, retouched!, p, new(Size, Size), Matrix3x2.CreateTranslation(extent.Offset));
        }
        var expected = target.GetPixelBytes();
        var rendered = await ImageEditRenderer.RenderToPixelsAsync(device, path, p);
        Assert.Equal(expected, rendered.Pixels);
        var backups = new OriginalBackupService(Path.Combine(_directory, "originals"));
        var hash = await OriginalBackupService.ComputeFileHashAsync(path);
        await new ImageEditService(backups).ApplyEditsAsync(path, p,
            (file, parameters) => ImageEditRenderer.RenderToFileAsync(file, parameters, device));
        using var reopened = await ImageEditRenderer.LoadOrientedAsync(device, path);
        var savedPixels = reopened.GetPixelBytes();
        if (orientation == 1) Assert.Equal(expected, savedPixels);
        else
        {
            // JPEG re-encoding is lossy; the source-resolution pre-encode comparison above is exact.
            var meanSquaredError = expected.Zip(savedPixels, (a, b) => (double)(a - b) * (a - b)).Average();
            Assert.InRange(Math.Sqrt(meanSquaredError), 0, 5);
        }
        Assert.True(await backups.RestoreOriginalAsync(path, hash));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task NavigationAndClose_RespectPendingStrokesAndBusyEvaluation()
    {
        using var device = new CanvasDevice();
        Task<(uint Width, uint Height)> RenderAsync(string file, EditParameters parameters) =>
            ImageEditRenderer.RenderToFileAsync(file, parameters, device);
        var backups = new OriginalBackupService(Path.Combine(_directory, "originals"));
        var vm = new ImageEditorViewModel(backups);
        var entries = new List<ImageEntry>();
        foreach (var name in new[] { "first.png", "second.png" })
        {
            var path = Path.Combine(_directory, name);
            await RenderQualityFixtures.WriteImageAsync(path, Size, Size, Surface(false));
            entries.Add(new ImageEntry { FilePath = path, FileName = name });
        }
        vm.UpdateLibraryImages(entries);
        await vm.OpenForEditAsync(entries[0]);
        vm.AddRetouchStroke(Stroke());
        Assert.False(await vm.NavigateAsync(1, () => Task.FromResult(PendingEditDecision.Cancel),
            RenderAsync));
        Assert.Same(entries[0], vm.CurrentEntry);
        Assert.Single(vm.GetCurrentParameters().RetouchStrokes);
        vm.IsRetouching = true;
        Assert.False(vm.CanNavigate);
        Assert.False(vm.CanUndo);
        Assert.False(await vm.SaveAsync(RenderAsync));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.OpenForEditAsync(entries[1]));
        vm.IsRetouching = false;
        Assert.True(await vm.NavigateAsync(1, () => Task.FromResult(PendingEditDecision.Save),
            RenderAsync));
        Assert.Empty(vm.GetCurrentParameters().RetouchStrokes);
        Assert.False(vm.HasChanges);
        Assert.Empty(ImageEditService.ReadEditParameters(entries[0].FilePath).RetouchStrokes);
        vm.AddRetouchStroke(Stroke());
        Assert.False(await vm.TryCloseAsync(() => Task.FromResult(PendingEditDecision.Cancel),
            RenderAsync));
        Assert.True(vm.HasChanges);
        Assert.True(await vm.TryCloseAsync(() => Task.FromResult(PendingEditDecision.Discard),
            RenderAsync));
        Assert.False(vm.IsOpen);
    }
}
