// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using Windows.Graphics.Imaging;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class UnifiedEditPersistenceTests
{
    [Fact]
    public async Task CurveSaveAndResetUseSharedHistoryWithoutChangingPixels()
    {
        await using var library = await StabilizedVideoLibraryTests.LibraryFixture.CreateAsync();
        var photo = await library.AddPhotoAsync("curve.png");
        var originalPixels = await RenderQualityFixtures.ReadPngPixelsAsync(photo.FilePath);
        var main = library.Main;
        await main.ImageEditor.OpenForEditAsync(photo);
        var curves = new ToneCurves { Blue = [new(0, 0), new(.5, .25), new(1, 1)] };
        main.ImageEditor.SetToneCurves(curves);
        Assert.True(await main.ImageEditor.SaveAsync((_, _) =>
            throw new InvalidOperationException("Curve save must not bake pixels.")), main.StatusText);
        Assert.Equal(originalPixels, await RenderQualityFixtures.ReadPngPixelsAsync(photo.FilePath));
        Assert.True(curves.ContentEquals(ImageEditService.ReadEditParameters(photo.FilePath).ToneCurves));
        Assert.Equal(EditHistoryStatus.Applied, (await main.EditHistory.UndoAsync()).Status);
        Assert.True(ImageEditService.ReadEditParameters(photo.FilePath).ToneCurves.IsIdentity);
        Assert.Equal(EditHistoryStatus.Applied, (await main.EditHistory.RedoAsync()).Status);
        Assert.True(curves.ContentEquals(ImageEditService.ReadEditParameters(photo.FilePath).ToneCurves));
    }

    [Fact]
    public async Task ViewerEffectsAndEditorSaveShareOnePersistedHistoryWithoutDuplicateSelections()
    {
        await using var library = await StabilizedVideoLibraryTests.LibraryFixture.CreateAsync();
        var photo = await library.AddPhotoAsync("effects.png");
        var main = library.Main;
        await main.ImageGrid.FilterByFolderAsync(library.Photos);
        main.ImageViewer.OpenImage(photo, main.ImageGrid.GetViewerImages());
        await main.ImageViewer.ApplyEffectAsync(PhotoEffect.Sepia);
        Assert.True(main.EditHistory.CanUndo, main.StatusText);
        await main.ImageViewer.ApplyEffectAsync(PhotoEffect.Sepia);
        Assert.Equal(EditHistoryStatus.Applied, (await main.EditHistory.UndoAsync()).Status);
        Assert.False(main.EditHistory.CanUndo);
        Assert.Equal(PhotoEffect.None, ImageEditService.ReadEditParameters(photo.FilePath).Effect);
        await main.EditHistory.RedoAsync();

        await main.ImageEditor.OpenForEditAsync(photo);
        main.ImageEditor.BeginEdit("Exposure drag");
        main.ImageEditor.Exposure = 0.1;
        main.ImageEditor.Exposure = 0.3;
        main.ImageEditor.EndEdit();
        Assert.Single(main.ImageEditor.UndoHistory);
        Assert.True(await main.ImageEditor.SaveAsync(async (copy, _) =>
        {
            var crop = await CropService.CropImageAsync(copy,
                new BitmapBounds { Width = 16, Height = 12 });
            return (crop.Width, crop.Height);
        }), main.StatusText);
        Assert.Empty(main.ImageEditor.UndoHistory);
        Assert.Contains("adjustments", main.EditHistory.UndoLabel);
        Assert.Equal(16, new MetadataReaderService().ReadMetadata(photo.FilePath).Width);
        await main.EditHistory.UndoAsync();
        Assert.Equal(32, new MetadataReaderService().ReadMetadata(photo.FilePath).Width);
        Assert.Equal(PhotoEffect.Sepia, ImageEditService.ReadEditParameters(photo.FilePath).Effect);
        await main.EditHistory.UndoAsync();
        Assert.False(main.EditHistory.CanUndo);
        Assert.Equal(PhotoEffect.None, ImageEditService.ReadEditParameters(photo.FilePath).Effect);
    }

    [Fact]
    public async Task GpsBatchAndDateShiftReplayPortableMetadataAndCache()
    {
        await using var library = await StabilizedVideoLibraryTests.LibraryFixture.CreateAsync();
        var photo = await library.AddPhotoAsync("location.png");
        var main = library.Main;
        main.MetadataPanel.ShowMetadata(photo);
        var coordinate = new GeoCoordinate(47.6, -122.3);
        var result = await main.MetadataPanel.SetGeotagAsync(coordinate);
        Assert.Equal(1, result.AppliedCount);
        Assert.Equal(coordinate.Latitude, (await library.Images.GetByPathAsync(photo.FilePath))!.GpsLatitude);
        Assert.Equal(EditHistoryStatus.Applied, (await main.EditHistory.UndoAsync()).Status);
        Assert.Null(new MetadataReaderService().ReadMetadata(photo.FilePath).GpsLatitude);
        await main.EditHistory.RedoAsync();
        Assert.Equal(coordinate.Longitude, new MetadataReaderService().ReadMetadata(photo.FilePath).GpsLongitude);

        main.MetadataPanel.ShowMetadata((await library.Images.GetByPathAsync(photo.FilePath))!);
        var date = new DateTime(2021, 5, 6, 9, 0, 0);
        Assert.True(await main.MetadataPanel.SetDateTakenAsync(date));
        main.MetadataPanel.ShowMetadata((await library.Images.GetByPathAsync(photo.FilePath))!);
        Assert.True(await main.MetadataPanel.OffsetDateTakenAsync(TimeSpan.FromHours(2)));
        Assert.Equal(date.AddHours(2), new MetadataReaderService().ReadMetadata(photo.FilePath).DateTaken);
        await main.EditHistory.UndoAsync();
        Assert.Equal(date, (await library.Images.GetByPathAsync(photo.FilePath))!.DateTaken);
    }

    [Fact]
    public async Task MetadataBatchesAndCropShareHistoryAndRestorePortableFilesAndCache()
    {
        await using var library = await StabilizedVideoLibraryTests.LibraryFixture.CreateAsync();
        var first = await library.AddPhotoAsync("first.png");
        var second = await library.AddPhotoAsync("second.png");
        var paths = new[] { first.FilePath, second.FilePath };
        var original = File.ReadAllBytes(first.FilePath);
        var main = library.Main;
        await main.ImageGrid.FilterByFolderAsync(library.Photos);
        main.MetadataPanel.ShowMetadata([first, second]);
        await main.MetadataPanel.SetRatingAsync(4);
        Assert.True(main.EditHistory.CanUndo, main.StatusText);
        await main.ApplyTagToImagePathsAsync("Trips/Spring", paths);
        Assert.Contains("add tag", main.EditHistory.UndoLabel);
        Assert.True(await main.RunPhotoEditAsync("crop", [first.FilePath], async copies =>
            _ = await CropService.CropImageAsync(copies[first.FilePath],
                new BitmapBounds { X = 0, Y = 0, Width = 16, Height = 12 })), main.StatusText);
        main.MetadataPanel.ShowMetadata((await library.Images.GetByPathAsync(second.FilePath))!);
        Assert.True(await main.MetadataPanel.SetDateTakenAsync(new DateTime(2020, 3, 5, 10, 0, 0)), main.StatusText);
        Assert.Contains("capture date", main.EditHistory.UndoLabel);

        await main.EditHistory.UndoAsync();
        Assert.Null((await library.Images.GetByPathAsync(second.FilePath))!.DateTaken);
        await main.EditHistory.UndoAsync();
        Assert.Equal(32, (await library.Images.GetByPathAsync(first.FilePath))!.Width);
        Assert.Contains("Trips/Spring", new MetadataReaderService().ReadTags(first.FilePath));
        await main.EditHistory.UndoAsync();
        Assert.Empty(new MetadataReaderService().ReadTags(first.FilePath));
        Assert.Equal(4, (await library.Images.GetByPathAsync(first.FilePath))!.Rating);
        await main.EditHistory.UndoAsync();
        Assert.Null((await library.Images.GetByPathAsync(first.FilePath))!.Rating);
        Assert.Null((await library.Images.GetByPathAsync(second.FilePath))!.Rating);
        Assert.Equal(original, File.ReadAllBytes(first.FilePath));
        Assert.True(await main.BackupService.HasBackupAsync(first.FilePath));
        Assert.False(main.EditHistory.CanUndo);

        for (var index = 0; index < 4; index++)
            Assert.Equal(EditHistoryStatus.Applied, (await main.EditHistory.RedoAsync()).Status);
        Assert.Equal(16, (await library.Images.GetByPathAsync(first.FilePath))!.Width);
        Assert.Equal(4, new MetadataReaderService().ReadMetadata(first.FilePath).Rating);
        Assert.Contains("Trips/Spring", new MetadataReaderService().ReadTags(second.FilePath));
        Assert.Equal(new DateTime(2020, 3, 5, 10, 0, 0),
            new MetadataReaderService().ReadMetadata(second.FilePath).DateTaken);
    }

    [Fact]
    public async Task ExternalChangesBlockUndoAfterNavigationWithoutConsumingIt()
    {
        await using var library = await StabilizedVideoLibraryTests.LibraryFixture.CreateAsync();
        var first = await library.AddPhotoAsync("first.png");
        var second = await library.AddPhotoAsync("second.png");
        var main = library.Main;
        await main.ImageGrid.FilterByFolderAsync(library.Photos);
        await main.SetFlagAsync([first], true);
        Assert.True(main.EditHistory.CanUndo, main.StatusText);
        main.ImageViewer.OpenImage(second, main.ImageGrid.GetViewerImages());
        var expected = File.ReadAllBytes(first.FilePath);
        var external = expected.Concat(new byte[] { 9 }).ToArray();
        File.WriteAllBytes(first.FilePath, external);
        var label = main.EditHistory.UndoLabel;

        Assert.Equal(EditHistoryStatus.Conflict, (await main.EditHistory.UndoAsync()).Status);
        Assert.Equal(label, main.EditHistory.UndoLabel);
        Assert.Equal(external, File.ReadAllBytes(first.FilePath));
        Assert.True((await library.Images.GetByPathAsync(first.FilePath))!.IsFlagged);
        Assert.Equal(second.FilePath, main.ImageViewer.CurrentEntry!.FilePath);

        File.WriteAllBytes(first.FilePath, expected);
        Assert.Equal(EditHistoryStatus.Applied, (await main.EditHistory.UndoAsync()).Status);
        Assert.False((await library.Images.GetByPathAsync(first.FilePath))!.IsFlagged);
        main.MetadataPanel.ShowMetadata((await library.Images.GetByPathAsync(second.FilePath))!);
        main.MetadataPanel.Caption = "Portable caption";
        Assert.True(await main.MetadataPanel.SaveCaptionAsync(), main.StatusText);
        Assert.False(main.EditHistory.CanRedo);
        Assert.Equal("Portable caption", new MetadataReaderService().ReadMetadata(second.FilePath).Caption);
    }

    [Fact]
    public async Task FailedBatchAndStaleEditorSaveKeepFilesAndHistoryIntact()
    {
        await using var library = await StabilizedVideoLibraryTests.LibraryFixture.CreateAsync();
        var first = await library.AddPhotoAsync("first.png");
        var second = await library.AddPhotoAsync("second.png");
        var main = library.Main;
        await main.ImageGrid.FilterByFolderAsync(library.Photos);
        await main.SetFlagAsync([first], true);
        var label = main.EditHistory.UndoLabel;
        var expected = File.ReadAllBytes(first.FilePath);
        Assert.False(await main.RunPhotoEditAsync("batch", [first.FilePath, second.FilePath], async copies =>
        {
            await MetadataWriterService.WriteRatingAsync(copies[first.FilePath], 5);
            throw new IOException("Second file failed");
        }));
        Assert.Contains("Second file failed", main.StatusText);
        Assert.Equal(expected, File.ReadAllBytes(first.FilePath));
        Assert.Equal(label, main.EditHistory.UndoLabel);

        await main.ImageEditor.OpenForEditAsync(first);
        main.ImageEditor.Exposure = 0.3;
        await MetadataWriterService.WriteCaptionAsync(first.FilePath, "External caption");
        var external = File.ReadAllBytes(first.FilePath);
        Assert.False(await main.ImageEditor.SaveAsync(async (copy, _) =>
        {
            await File.WriteAllBytesAsync(copy, [7, 8, 9]);
            return (32, 24);
        }));
        Assert.Contains("changed since the editor opened", main.StatusText);
        Assert.True(main.ImageEditor.HasChanges);
        Assert.Single(main.ImageEditor.UndoHistory);
        Assert.Equal(external, File.ReadAllBytes(first.FilePath));
        Assert.Equal(label, main.EditHistory.UndoLabel);
    }

    [Fact]
    public async Task ComposedPresetUsesOneUndoableWriteAndRejectsNoOpsAndStaleTargets()
    {
        await using var library = await StabilizedVideoLibraryTests.LibraryFixture.CreateAsync();
        var photo = await library.AddPhotoAsync("preset.png");
        var main = library.Main;
        var baseline = ImageEditService.ReadEditParameters(photo.FilePath);
        var expectedIdentity = await FileEditTransaction.ReadIdentityAsync(photo.FilePath);
        var preset = new EditParameters { Exposure = 0.4 };
        var composition = EditRecipeComposer.Compose(
            baseline, preset, EditSettingGroup.Tone, 100, isRawTarget: false, sameSource: false);

        Assert.True(await main.ApplyComposedRecipeAsync(
            photo, composition.Parameters, expectedIdentity, "apply preset"), main.StatusText);
        Assert.Equal(0.4, ImageEditService.ReadEditParameters(photo.FilePath).Exposure);
        var undoLabel = main.EditHistory.UndoLabel;
        var current = ImageEditService.ReadEditParameters(photo.FilePath);
        var noOpIdentity = await FileEditTransaction.ReadIdentityAsync(photo.FilePath);
        Assert.False(await main.ApplyComposedRecipeAsync(photo, current, noOpIdentity, "apply preset"));
        Assert.Equal(undoLabel, main.EditHistory.UndoLabel);
        Assert.Equal(noOpIdentity, await FileEditTransaction.ReadIdentityAsync(photo.FilePath));

        Assert.Equal(EditHistoryStatus.Applied, (await main.EditHistory.UndoAsync()).Status);
        Assert.True(EditRecipeComposer.ContentEquals(
            baseline, ImageEditService.ReadEditParameters(photo.FilePath)));
        Assert.Equal(EditHistoryStatus.Applied, (await main.EditHistory.RedoAsync()).Status);
        Assert.Equal(0.4, ImageEditService.ReadEditParameters(photo.FilePath).Exposure);

        var staleIdentity = await FileEditTransaction.ReadIdentityAsync(photo.FilePath);
        await MetadataWriterService.WriteCaptionAsync(photo.FilePath, "External caption");
        var externalBytes = await File.ReadAllBytesAsync(photo.FilePath);
        var changedRecipe = ImageEditService.ReadEditParameters(photo.FilePath);
        changedRecipe.Exposure = 0.8;
        Assert.False(await main.ApplyComposedRecipeAsync(
            photo, changedRecipe, staleIdentity, "apply preset"));
        Assert.Contains("changed before the batch edit", main.StatusText);
        Assert.Equal(externalBytes, await File.ReadAllBytesAsync(photo.FilePath));
        Assert.Equal(0.4, ImageEditService.ReadEditParameters(photo.FilePath).Exposure);
    }

    [Fact]
    public async Task SyncCopiesOnlySelectedGroupsAcrossTheSelectionAsOneUndoableEdit()
    {
        await using var library = await StabilizedVideoLibraryTests.LibraryFixture.CreateAsync();
        var source = await library.AddPhotoAsync("sync-source.png");
        var destination = await library.AddPhotoAsync("sync-destination.png");
        var main = library.Main;

        var sourceRecipe = ImageEditService.ReadEditParameters(source.FilePath);
        sourceRecipe.Exposure = 0.6;
        var sourceIdentity = await FileEditTransaction.ReadIdentityAsync(source.FilePath);
        Assert.True(await main.ApplyComposedRecipeAsync(
            source, sourceRecipe, sourceIdentity, "source edit"), main.StatusText);

        var destinationRecipe = ImageEditService.ReadEditParameters(destination.FilePath);
        destinationRecipe.Effect = PhotoEffect.Sepia;
        var destinationIdentity = await FileEditTransaction.ReadIdentityAsync(destination.FilePath);
        Assert.True(await main.ApplyComposedRecipeAsync(
            destination, destinationRecipe, destinationIdentity, "destination effect"), main.StatusText);
        var destinationBeforeSync = ImageEditService.ReadEditParameters(destination.FilePath);

        Assert.True(await main.SyncEditGroupsAsync(
            source, [source, destination], EditSettingGroup.Tone), main.StatusText);
        var synchronized = ImageEditService.ReadEditParameters(destination.FilePath);
        Assert.Equal(0.6, synchronized.Exposure);
        Assert.Equal(PhotoEffect.Sepia, synchronized.Effect);

        Assert.Equal(EditHistoryStatus.Applied, (await main.EditHistory.UndoAsync()).Status);
        var undone = ImageEditService.ReadEditParameters(destination.FilePath);
        Assert.Equal(destinationBeforeSync.Exposure, undone.Exposure);
        Assert.Equal(PhotoEffect.Sepia, undone.Effect);
    }
}
