// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class EditPresetWorkflowTests
{
    [Fact]
    public async Task AmountPreviewUsesOpeningBaselineAndCancelDoesNotWriteOrEnterHistory()
    {
        await using var library = await StabilizedVideoLibraryTests.LibraryFixture.CreateAsync();
        var photo = await library.AddPhotoAsync("amount-preview.png");
        var main = library.Main;
        var baseline = ImageEditService.ReadEditParameters(photo.FilePath);
        var originalBytes = await File.ReadAllBytesAsync(photo.FilePath);
        var originalIdentity = await FileEditTransaction.ReadIdentityAsync(photo.FilePath);
        var preset = new EditParameters { Exposure = 0.4 };
        using var preview = new EditRecipePreviewSession();

        foreach (var (amount, expectedExposure) in new[] { (150, 0.6), (50, 0.2), (100, 0.4) })
        {
            var composition = EditRecipeComposer.Compose(
                baseline, preset, EditSettingGroup.Tone, amount,
                isRawTarget: false, sameSource: false);
            Assert.Equal(expectedExposure, composition.Parameters.Exposure, 12);
            Assert.NotNull(await preview.RequestAsync(photo.FilePath, composition.Parameters));
        }

        var cancelled = preview.RequestAsync(photo.FilePath,
            EditRecipeComposer.Compose(baseline, preset, EditSettingGroup.Tone, 125,
                isRawTarget: false, sameSource: false).Parameters);
        preview.Invalidate();
        Assert.Null(await cancelled);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(photo.FilePath));
        Assert.Equal(originalIdentity, await FileEditTransaction.ReadIdentityAsync(photo.FilePath));
        Assert.False(main.EditHistory.CanUndo);
    }

    [Fact]
    public async Task PresetBatchReportsUnsupportedAndMissingPhotosAndUndoOnlyChangesSuccessfulTargets()
    {
        await using var library = await StabilizedVideoLibraryTests.LibraryFixture.CreateAsync();
        var supported = await library.AddPhotoAsync("supported.png");
        var unsupported = await library.AddPhotoAsync("unsupported-geometry.png");
        var missing = await library.AddPhotoAsync("missing.png");
        Assert.True(FaceMetadataStore.TryWriteEmbedded(unsupported.FilePath, CreateUnsupportedGeometryPacket()));
        var unsupportedBytes = await File.ReadAllBytesAsync(unsupported.FilePath);
        File.Delete(missing.FilePath);

        var preset = new PhotoPreset(
            Guid.NewGuid().ToString("N"),
            "Exposure",
            EditSettingGroup.Tone,
            new EditParameters { Exposure = 0.5 },
            null,
            false);

        Assert.True(await library.Main.ApplyPresetAsync(
            preset, [supported, unsupported, missing], 100), library.Main.StatusText);
        Assert.Equal(0.5, ImageEditService.ReadEditParameters(supported.FilePath).Exposure);
        Assert.Equal(unsupportedBytes, await File.ReadAllBytesAsync(unsupported.FilePath));
        Assert.False(File.Exists(missing.FilePath));
        Assert.Contains(unsupported.FileName, library.Main.StatusText);
        Assert.Contains(missing.FileName, library.Main.StatusText);
        Assert.True(library.Main.EditHistory.CanUndo);

        Assert.Equal(EditHistoryStatus.Applied, (await library.Main.EditHistory.UndoAsync()).Status);
        Assert.False(ImageEditService.ReadEditParameters(supported.FilePath).HasAdjustments);
        Assert.Equal(unsupportedBytes, await File.ReadAllBytesAsync(unsupported.FilePath));
        Assert.False(library.Main.EditHistory.CanUndo);
    }

    [Fact]
    public async Task RasterPresetSkipsRawDevelopmentWithoutChangingPhotoOrHistory()
    {
        await using var library = await StabilizedVideoLibraryTests.LibraryFixture.CreateAsync();
        var photo = await library.AddPhotoAsync("raster.png");
        var bytes = await File.ReadAllBytesAsync(photo.FilePath);
        var preset = new PhotoPreset(
            Guid.NewGuid().ToString("N"),
            "RAW exposure",
            EditSettingGroup.RawDevelopment,
            new EditParameters { RawExposureEv = 1.5, RawWhitePoint = 8 },
            null,
            false);

        Assert.False(await library.Main.ApplyPresetAsync(preset, [photo], 100));
        Assert.Contains(PhotoContextMenuStrings.Get("EditGroupRawDevelopment"), library.Main.StatusText);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(photo.FilePath));
        Assert.False(library.Main.EditHistory.CanUndo);
    }

    [Fact]
    public async Task CopyPastePreservesUnselectedGroupsAndRetouchRequiresMatchingSourcePixels()
    {
        await using var library = await StabilizedVideoLibraryTests.LibraryFixture.CreateAsync();
        var source = await library.AddPhotoAsync("source.png");
        var samePixels = await library.AddPhotoAsync("same-pixels.png");
        var differentPixels = await library.AddPhotoAsync("different-pixels.png");
        File.Copy(source.FilePath, samePixels.FilePath, overwrite: true);
        await RenderQualityFixtures.WriteImageAsync(differentPixels.FilePath, 32, 24,
            RenderQualityFixtures.CreatePixels(RenderQualityFixtureKind.Edge, 32, 24));

        var sourceRecipe = ImageEditService.ReadEditParameters(source.FilePath);
        sourceRecipe.Exposure = 0.6;
        sourceRecipe.Effect = PhotoEffect.Sepia;
        sourceRecipe.RetouchStrokes =
        [
            new RetouchStroke(32, 24, 4, [new RetouchPoint(10, 10)])
        ];
        await ImageEditService.WritePendingRecipeAsync(source.FilePath, sourceRecipe);

        var sameIdentity = await RecognitionSourceIdentity.ComputeAsync(samePixels.FilePath);
        var differentIdentity = await RecognitionSourceIdentity.ComputeAsync(differentPixels.FilePath);
        var sourceIdentity = await RecognitionSourceIdentity.ComputeAsync(source.FilePath);
        Assert.Equal(sourceIdentity, sameIdentity);
        Assert.NotEqual(sourceIdentity, differentIdentity);

        var destinationRecipe = ImageEditService.ReadEditParameters(samePixels.FilePath);
        destinationRecipe.Effect = PhotoEffect.Cyanotype;
        await ImageEditService.WritePendingRecipeAsync(samePixels.FilePath, destinationRecipe);
        await library.Main.CopyEditGroupsAsync(source, EditSettingGroup.Tone | EditSettingGroup.Retouch);

        Assert.True(await library.Main.PasteEditGroupsAsync(
            [samePixels, differentPixels], EditSettingGroup.Tone | EditSettingGroup.Retouch),
            library.Main.StatusText);
        var pasted = ImageEditService.ReadEditParameters(samePixels.FilePath);
        Assert.Equal(0.6, pasted.Exposure);
        Assert.Equal(PhotoEffect.Cyanotype, pasted.Effect);
        Assert.Single(pasted.RetouchStrokes);
        Assert.Empty(ImageEditService.ReadEditParameters(differentPixels.FilePath).RetouchStrokes);
        Assert.Contains(PhotoContextMenuStrings.Get("EditGroupRetouch"), library.Main.StatusText);

        Assert.Equal(EditHistoryStatus.Applied, (await library.Main.EditHistory.UndoAsync()).Status);
        Assert.Empty(ImageEditService.ReadEditParameters(samePixels.FilePath).RetouchStrokes);
        Assert.Equal(PhotoEffect.Cyanotype, ImageEditService.ReadEditParameters(samePixels.FilePath).Effect);

        Assert.True(await library.Main.SyncEditGroupsAsync(
            source, [source, samePixels, differentPixels], EditSettingGroup.Retouch),
            library.Main.StatusText);
        Assert.Single(ImageEditService.ReadEditParameters(samePixels.FilePath).RetouchStrokes);
        Assert.Empty(ImageEditService.ReadEditParameters(differentPixels.FilePath).RetouchStrokes);
        Assert.Equal(EditHistoryStatus.Applied, (await library.Main.EditHistory.UndoAsync()).Status);
        Assert.Empty(ImageEditService.ReadEditParameters(samePixels.FilePath).RetouchStrokes);
    }

    private static string CreateUnsupportedGeometryPacket()
    {
        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters());
        xmp.SetProperty(EditParameters.XmpNamespace, $"{EditParameters.XmpPrefix}:GeometryVersion", "1");
        xmp.SetProperty(EditParameters.XmpNamespace, $"{EditParameters.XmpPrefix}:GeometryV1", "{}");
        return XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions())
            .TrimStart('\uFEFF');
    }
}
