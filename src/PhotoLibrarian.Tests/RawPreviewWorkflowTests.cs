// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class RawPreviewWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-RawPreview-{Guid.NewGuid():N}");
    public RawPreviewWorkflowTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private async Task<string> CreateRawAsync(string name = "fixture.dng", bool xTrans = false, ushort orientation = 1)
    {
        var path = Path.Combine(_directory, name);
        await File.WriteAllBytesAsync(path, RawDevelopmentTests.SyntheticDngFixture.Create(xTrans, orientation));
        return path;
    }

    private ImageEditorViewModel CreateViewModel() =>
        new(new OriginalBackupService(Path.Combine(_directory, "backups")));

    private static ImageEntry Entry(string path) => new() { FilePath = path, FileName = Path.GetFileName(path) };

    [Fact]
    public async Task DefaultsCloneResetHistoryAndPortableRecipePreserveOriginalAndOtherXmp()
    {
        var path = await CreateRawAsync();
        var original = await File.ReadAllBytesAsync(path);
        var sidecar = Path.ChangeExtension(path, ".xmp");
        var xmp = XmpMetaFactory.Create();
        xmp.SetProperty("http://ns.adobe.com/xap/1.0/", "xmp:Label", "Keep me");
        File.WriteAllText(sidecar, XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()));
        var originalXmp = File.ReadAllText(sidecar);
        var vm = CreateViewModel();
        await vm.OpenForEditAsync(Entry(path));
        Assert.True(vm.IsRawPreview);
        Assert.False(vm.CanSavePixels);
        Assert.Contains("derivative", vm.SaveDisabledReason);
        Assert.Equal(0, vm.RawExposureEv);
        Assert.Equal(4, vm.RawWhitePoint);
        vm.BeginEdit("RAW preview");
        vm.RawExposureEv = -1.5;
        vm.RawWhitePoint = 8;
        vm.EndEdit();
        var clone = vm.GetCurrentParameters().Clone();
        Assert.Equal(-1.5, clone.RawExposureEv);
        Assert.Equal(8, clone.RawWhitePoint);
        Assert.True(clone.HasAdjustments);
        clone.Reset();
        Assert.False(clone.HasAdjustments);
        vm.UndoSteps(1);
        Assert.Equal(0, vm.RawExposureEv);
        Assert.Equal(4, vm.RawWhitePoint);
        vm.RedoSteps(1);
        await vm.KeepRawPreviewRecipeAsync();
        Assert.False(vm.HasChanges);
        var backup = Assert.Single(Directory.GetFiles(_directory, "fixture.xmp.*.bak"));
        Assert.Equal(originalXmp, File.ReadAllText(backup));
        vm.ResetAllCommand.Execute(null);
        Assert.Equal(0, vm.RawExposureEv);
        Assert.True(vm.HasChanges);
        vm.UndoSteps(1);
        Assert.Equal(-1.5, vm.RawExposureEv);
        Assert.False(vm.HasChanges);
        vm.CloseCommand.Execute(null);
        await vm.OpenForEditAsync(Entry(path));
        Assert.Equal(-1.5, vm.RawExposureEv);
        Assert.Equal(8, vm.RawWhitePoint);
        Assert.Empty(vm.UndoHistory);
        using var stream = File.OpenRead(sidecar);
        var kept = XmpMetaFactory.Parse(stream);
        Assert.Equal("Keep me", kept.GetPropertyString("http://ns.adobe.com/xap/1.0/", "xmp:Label"));
        Assert.Equal(RawToneMappingService.RecipeVersion, kept.GetPropertyString(EditParameters.XmpNamespace, "pledit:RawToneMapping"));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(".dng")]
    [InlineData(".CR3")]
    [InlineData(".nef")]
    [InlineData(".arw")]
    [InlineData(".cr2")]
    [InlineData(".orf")]
    [InlineData(".rw2")]
    public async Task RawCannotReachAnyOverwriteRendererOrRevert(string extension)
    {
        var path = Path.Combine(_directory, "original" + extension);
        byte[] original = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(path, original);
        var vm = CreateViewModel();
        await vm.OpenForEditAsync(Entry(path));
        vm.RawExposureEv = 1;
        var invoked = false;
        Task<(uint, uint)> Renderer(string _, EditParameters __)
        {
            invoked = true;
            return Task.FromResult((1u, 1u));
        }
        await Assert.ThrowsAsync<NotSupportedException>(() => vm.SaveAsync(Renderer));
        var edits = new ImageEditService(new OriginalBackupService(Path.Combine(_directory, "other-backups")));
        await Assert.ThrowsAsync<NotSupportedException>(() => edits.ApplyEditsAsync(path, vm.GetCurrentParameters(), Renderer));
        await Assert.ThrowsAsync<NotSupportedException>(() => ImageEditRenderer.RenderToFileAsync(path, vm.GetCurrentParameters()));
        await Assert.ThrowsAsync<NotSupportedException>(() => vm.RevertToOriginalCommand.ExecuteAsync(null));
        Assert.False(invoked);
        Assert.False(ImageEditRenderer.IsSupported(path));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task InvalidRecipeAndUnreadableSidecarAreNotReplaced()
    {
        var path = await CreateRawAsync();
        var sidecar = Path.ChangeExtension(path, ".xmp");
        const string invalid = "<invalid xmp";
        await File.WriteAllTextAsync(sidecar, invalid);
        await Assert.ThrowsAnyAsync<Exception>(() => ImageEditService.SaveRawPreviewRecipeAsync(path, new EditParameters()));
        Assert.ThrowsAny<Exception>(() => ImageEditService.ReadEditParameters(path));
        Assert.Equal(invalid, await File.ReadAllTextAsync(sidecar));
        File.Delete(sidecar);
        await ImageEditService.SaveRawPreviewRecipeAsync(path, new EditParameters());
        var xmp = XmpMetaFactory.Create();
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:RawToneMapping", "FutureV2");
        await File.WriteAllTextAsync(sidecar, XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()));
        Assert.Throws<NotSupportedException>(() => ImageEditService.ReadEditParameters(path));
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:RawToneMapping", RawToneMappingService.RecipeVersion);
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:RawWhitePoint", "not-a-number");
        await File.WriteAllTextAsync(sidecar, XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()));
        Assert.ThrowsAny<Exception>(() => ImageEditService.ReadEditParameters(path));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ImageEditService.SaveRawPreviewRecipeAsync(path, new EditParameters { RawWhitePoint = double.NaN }));
    }

    [Fact]
    public async Task RawResetAndKeepDoNotDiscardDormantRasterRecipeOrCrop()
    {
        var path = await CreateRawAsync();
        await ImageEditService.SaveRawPreviewRecipeAsync(path, new EditParameters
        {
            Exposure = 0.5, RotationAngle = 90, RawExposureEv = -2, RawWhitePoint = 8,
            Crop = new CropRect { Left = 0.1, Top = 0.2, Width = 0.7, Height = 0.6 }
        });
        var vm = CreateViewModel();
        await vm.OpenForEditAsync(Entry(path));
        vm.ResetAllCommand.Execute(null);
        Assert.Equal(0, vm.RawExposureEv);
        Assert.Equal(4, vm.RawWhitePoint);
        Assert.Equal(0.5, vm.Exposure);
        Assert.Equal(90, vm.RotationAngle);
        await vm.KeepRawPreviewRecipeAsync();
        var reopened = ImageEditService.ReadEditParameters(path);
        Assert.Equal(0.1, reopened.Crop!.Left);
        Assert.Equal(0.6, reopened.Crop.Height);
        Assert.Equal(0.5, reopened.Exposure);
    }

    [Fact]
    public async Task RasterBakeKeepsRawFieldsInSharedSameStemSidecar()
    {
        var raw = await CreateRawAsync("shared.dng");
        var original = await File.ReadAllBytesAsync(raw);
        await ImageEditService.SaveRawPreviewRecipeAsync(raw,
            new EditParameters { RawExposureEv = -1, RawWhitePoint = 8, Brightness = 0.1 });
        var raster = Path.ChangeExtension(raw, ".jpg");
        await File.WriteAllBytesAsync(raster, [1, 2, 3]);
        var edits = new ImageEditService(new OriginalBackupService(Path.Combine(_directory, "backups")));
        await edits.ApplyEditsAsync(raster, new EditParameters { Brightness = 0.1 },
            (_, _) => Task.FromResult((1u, 1u)));
        var kept = ImageEditService.ReadEditParameters(raw);
        Assert.Equal(-1, kept.RawExposureEv);
        Assert.Equal(8, kept.RawWhitePoint);
        Assert.Equal(0, kept.Brightness);
        Assert.Equal(original, await File.ReadAllBytesAsync(raw));
    }

    [Theory]
    [InlineData(false, 6, 40, 56)]
    [InlineData(true, 1, 520, 520)]
    public async Task PreviewIsSourceResolutionAndOriginalBytesRemainIdentical(bool xTrans, ushort orientation, int width, int height)
    {
        var path = await CreateRawAsync(xTrans: xTrans, orientation: orientation);
        var original = await File.ReadAllBytesAsync(path);
        using var session = new RawPreviewSession();
        var first = await session.RenderAsync(path, new EditParameters(), CancellationToken.None);
        var second = await session.RenderAsync(path, new EditParameters { RawExposureEv = -1 }, CancellationToken.None);
        Assert.Equal(width, first.Width);
        Assert.Equal(height, first.Height);
        Assert.Equal(width * height * 3, first.LinearRgb.Length);
        Assert.Equal(width * height * 4, first.ToPreviewBgra8().Length);
        Assert.NotEqual(first.ToPreviewBgra8(), second.ToPreviewBgra8());
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task SupersededDecodeCannotPublishEvenWhenDecoderIgnoresCancellationAndSourceIsReused()
    {
        var path = await CreateRawAsync();
        var other = await CreateRawAsync("other.dng", orientation: 6);
        var source = await RawDevelopmentService.DecodeAsync(path);
        var otherSource = await RawDevelopmentService.DecodeAsync(other);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<RawDevelopedImage> Decode(string requested, CancellationToken _)
        {
            calls++;
            if (calls == 1)
            {
                started.SetResult();
                await release.Task;
            }
            return requested == path ? source : otherSource;
        }
        using var session = new RawPreviewSession(Decode);
        var old = session.RenderAsync(path, new EditParameters(), CancellationToken.None);
        await started.Task;
        var newest = session.RenderAsync(other, new EditParameters { RawExposureEv = -1 }, CancellationToken.None);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        var frame = await newest;
        Assert.Equal(40, frame.Width);
        Assert.Equal(56, frame.Height);
        var reused = await session.RenderAsync(other, new EditParameters(), CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Equal(frame.Width, reused.Width);
        session.Invalidate();
        await session.RenderAsync(other, new EditParameters(), CancellationToken.None);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ParameterSnapshotAndCloseCancellationDoNotLeakStaleResults()
    {
        var path = await CreateRawAsync();
        var source = await RawDevelopmentService.DecodeAsync(path);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<RawDevelopedImage> Decode(string _, CancellationToken __)
        {
            started.TrySetResult();
            await release.Task;
            return source;
        }
        using var session = new RawPreviewSession(Decode);
        var parameters = new EditParameters { RawExposureEv = -1 };
        var pending = session.RenderAsync(path, parameters, CancellationToken.None);
        await started.Task;
        parameters.RawExposureEv = 8;
        release.SetResult();
        var result = await pending;
        var expected = await RawToneMappingService.RenderAsync(source, new EditParameters { RawExposureEv = -1 });
        Assert.Equal(expected.LinearRgb.ToArray(), result.LinearRgb.ToArray());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.RenderAsync(path, parameters, cancelled.Token));
        session.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            session.RenderAsync(path, parameters, CancellationToken.None));
    }
}
