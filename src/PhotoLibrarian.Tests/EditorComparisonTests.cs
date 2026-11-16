// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using System.Numerics;
using Windows.Graphics.DirectX;
using Windows.System;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class EditorComparisonTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-comparison-{Guid.NewGuid():N}");

    public EditorComparisonTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(VirtualKey.B, EditorComparisonMode.Before)]
    [InlineData(VirtualKey.N, EditorComparisonMode.SideBySide)]
    [InlineData(VirtualKey.M, EditorComparisonMode.Split)]
    public void ShortcutsRespectTypingAndModifiers(VirtualKey key, EditorComparisonMode expected)
    {
        Assert.True(EditorComparison.TryGetShortcut(key, VirtualKeyModifiers.None, false, out var mode));
        Assert.Equal(expected, mode);
        Assert.False(EditorComparison.TryGetShortcut(key, VirtualKeyModifiers.None, true, out _));
        foreach (var modifier in new[] { VirtualKeyModifiers.Control, VirtualKeyModifiers.Shift,
                     VirtualKeyModifiers.Menu, VirtualKeyModifiers.Windows })
            Assert.False(EditorComparison.TryGetShortcut(key, modifier, false, out _));
        Assert.False(EditorComparison.TryGetShortcut(VirtualKey.Z, VirtualKeyModifiers.None, false, out _));
    }

    [Fact]
    public async Task TogglingViewsDoesNotChangeSavedOrPendingRecipeHistoryOrFiles()
    {
        var path = Path.Combine(_directory, "source.png");
        var pixels = RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Gradient, 24, 16);
        await RenderQualityFixtures.WriteImageAsync(path, 24, 16, pixels);
        var backups = new OriginalBackupService(Path.Combine(_directory, "originals"));
        var saved = new EditParameters { Exposure = .25, RotationAngle = 90 };
        saved.ToneCurves.Rgb.Insert(1, new ToneCurvePoint(.5, .7));
        await new ImageEditService(backups).SaveEditParametersAsync(path, saved);
        var vm = new ImageEditorViewModel(backups);
        await vm.OpenForEditAsync(new ImageEntry { FilePath = path, FileName = "source.png" });
        vm.Exposure = .5;
        vm.Contrast = .2;
        vm.UndoCommand.Execute(null);
        var undo = vm.UndoHistory.ToArray();
        var redo = vm.RedoHistory.ToArray();
        var identity = await FileEditTransaction.ReadIdentityAsync(path);
        var previews = 0;
        vm.ParametersChanged += (_, _) => previews++;
        foreach (var mode in Enum.GetValues<EditorComparisonMode>())
        {
            vm.ToggleComparison(mode);
            Assert.Equal(mode, vm.ComparisonMode);
            vm.ToggleComparison(mode);
            Assert.Equal(EditorComparisonMode.After, vm.ComparisonMode);
        }
        Assert.Equal(.5, vm.Exposure);
        Assert.Equal(90, vm.RotationAngle);
        Assert.True(vm.HasChanges);
        Assert.Equal(undo, vm.UndoHistory);
        Assert.Equal(redo, vm.RedoHistory);
        Assert.Equal(0, previews);
        Assert.Equal(identity, await FileEditTransaction.ReadIdentityAsync(path));
        using var device = new CanvasDevice();
        using var source = await ImageEditRenderer.LoadOrientedAsync(device, path);
        Assert.Equal(pixels, Render(device, source, source, vm.GetCurrentParameters(),
            EditorComparisonMode.Before, 24, 16));
    }

    [Fact]
    public async Task BusyRawAndClosedEditorsCannotEnterComparisonAndNewOpenResetsIt()
    {
        var path = Path.Combine(_directory, "source.png");
        await RenderQualityFixtures.WriteImageAsync(path, 8, 8, new byte[8 * 8 * 4]);
        var vm = new ImageEditorViewModel(new OriginalBackupService(Path.Combine(_directory, "originals")));
        vm.ToggleComparison(EditorComparisonMode.Before);
        Assert.Equal(EditorComparisonMode.After, vm.ComparisonMode);
        var entry = new ImageEntry { FilePath = path, FileName = "source.png" };
        await vm.OpenForEditAsync(entry);
        vm.IsSaving = true;
        vm.ToggleComparison(EditorComparisonMode.Before);
        Assert.Equal(EditorComparisonMode.After, vm.ComparisonMode);
        vm.IsSaving = false;
        vm.ToggleComparison(EditorComparisonMode.Before);
        await vm.OpenForEditAsync(entry);
        Assert.Equal(EditorComparisonMode.After, vm.ComparisonMode);
        var rawPath = Path.Combine(_directory, "source.dng");
        await File.WriteAllBytesAsync(rawPath, [1, 2, 3]);
        await vm.OpenForEditAsync(new ImageEntry { FilePath = rawPath, FileName = "source.dng" });
        vm.ToggleComparison(EditorComparisonMode.Split);
        Assert.Equal(EditorComparisonMode.After, vm.ComparisonMode);
    }

    [Fact]
    public void SplitAndSideBySideDrawExactUneditedAndEditedHalvesWithoutChangingInputs()
    {
        const int width = 24, height = 16;
        using var device = new CanvasDevice();
        var pixels = RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Gradient, width, height);
        var retouched = RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Edge, width, height);
        using var before = CanvasBitmap.CreateFromBytes(device, pixels, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var after = CanvasBitmap.CreateFromBytes(device, retouched, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        var parameters = new EditParameters { Exposure = .3, Saturation = -.2 };
        var edited = Render(device, before, after, parameters, EditorComparisonMode.After, width, height);
        var split = Render(device, before, after, parameters, EditorComparisonMode.Split, width, height);
        var side = Render(device, before, after, parameters, EditorComparisonMode.SideBySide, width * 2, height);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        for (var c = 0; c < 4; c++)
        {
            var i = (int)((y * width + x) * 4 + c);
            Assert.Equal(x < width / 2 ? pixels[i] : edited[i], split[i]);
            Assert.Equal(pixels[i], side[(y * width * 2 + x) * 4 + c]);
            Assert.Equal(edited[i], side[(y * width * 2 + x + width) * 4 + c]);
        }
        Assert.Equal(pixels, Render(device, before, after, parameters, EditorComparisonMode.Split, width, height, 1));
        Assert.Equal(edited, Render(device, before, after, parameters, EditorComparisonMode.Split, width, height, 0));
        Assert.Equal(pixels, before.GetPixelBytes());
        Assert.Equal(retouched, after.GetPixelBytes());
    }

    [Fact]
    public void RotatedAfterAndUnrotatedBeforeShareExtentAndSynchronizedZoomPan()
    {
        using var device = new CanvasDevice();
        var pixels = RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Gradient, 24, 16);
        using var source = CanvasBitmap.CreateFromBytes(device, pixels, 24, 16, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        var viewport = new EditorViewport();
        using var target = new CanvasRenderTarget(device, 240, 80, 96);
        var parameters = new EditParameters { RotationAngle = 90 };
        using (var session = target.CreateDrawingSession())
            EditorComparisonRenderer.Draw(session, source, source, parameters,
                EditorComparisonMode.SideBySide, viewport, new Vector2(240, 80), .5f);
        Assert.Equal(new Vector2(24, 24), viewport.ContentSize);
        Assert.Equal(new Vector2(120, 80), viewport.CanvasSize);
        viewport.ZoomTo(6);
        viewport.PanBy(new Vector2(7, -5));
        var origin = viewport.ContentOrigin;
        using (var session = target.CreateDrawingSession())
            EditorComparisonRenderer.Draw(session, source, source, parameters,
                EditorComparisonMode.SideBySide, viewport, new Vector2(240, 80), .5f);
        Assert.Equal(origin, viewport.ContentOrigin);
        Assert.Equal(6, viewport.Scale);
        Assert.Equal(pixels, source.GetPixelBytes());
    }

    private static byte[] Render(CanvasDevice device, CanvasBitmap before, CanvasBitmap after,
        EditParameters parameters, EditorComparisonMode mode, uint width, uint height, float split = .5f)
    {
        using var target = new CanvasRenderTarget(device, width, height, 96);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            EditorComparisonRenderer.Draw(session, before, after, parameters, mode, new EditorViewport(),
                new Vector2(width, height), split);
        }
        return target.GetPixelBytes();
    }
}
