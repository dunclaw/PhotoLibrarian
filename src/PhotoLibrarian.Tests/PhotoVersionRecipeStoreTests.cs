// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class PhotoVersionRecipeStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"PhotoLibrarian-version-store-{Guid.NewGuid():N}");

    public PhotoVersionRecipeStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task RawVersionSaveParticipatesInHistoryAndRejectsStaleSnapshots()
    {
        var path = Path.Combine(_directory, "source.cr3");
        byte[] raw = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(path, raw, TestContext.Current.CancellationToken);
        var identity = await RecognitionSourceIdentity.ComputeAsync(path);
        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters { RawExposureEv = 0.5 });
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        var sourceId = Guid.NewGuid();
        var mainId = Guid.NewGuid();
        var copyId = Guid.NewGuid();
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            sourceId, identity, mainId, "Original", packet, copyId, "Copy", out var family));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, family!, null));
        var history = new EditHistoryService();
        var store = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history"), history, () => Task.CompletedTask);
        var target = new PhotoVersionTarget(path, sourceId, copyId);
        var read = await store.ReadAsync(target);
        Assert.Equal(PhotoVersionRecipeStatus.Success, read.Status);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => store.WriteRecipeAsync(read.Snapshot!, packet, cancellation.Token));
            Assert.False(history.CanUndo);
        }
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionRecipePacket.TryRead(packet, true, out var recipe));
        recipe!.RawExposureEv = 1.0;
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionRecipePacket.TryUpdate(packet, true, recipe, out var updated));
        var sidecar = Path.ChangeExtension(path, ".xmp");
        var attributes = File.GetAttributes(sidecar);
        try
        {
            File.SetAttributes(sidecar, attributes | FileAttributes.ReadOnly);
            Assert.Equal(PhotoVersionRecipeStatus.ReadOnly,
                (await store.WriteRecipeAsync(read.Snapshot!, updated!)).Status);
            Assert.False(history.CanUndo);
            Assert.Equal(family, MetadataWriterService.ReadVersionFamily(path).Value);
        }
        finally
        {
            File.SetAttributes(sidecar, attributes);
        }

        var saved = await store.WriteRecipeAsync(read.Snapshot!, updated!);
        Assert.Equal(PhotoVersionRecipeStatus.Success, saved.Status);
        Assert.Equal(updated, saved.Snapshot!.RecipeXmp);
        var main = await store.ReadAsync(new PhotoVersionTarget(path, sourceId, mainId));
        Assert.Equal(packet, main.Snapshot!.RecipeXmp);
        Assert.Equal(PhotoVersionRecipeStatus.Conflict,
            (await store.WriteRecipeAsync(read.Snapshot!, updated!)).Status);
        Assert.Equal(EditHistoryStatus.Applied, (await history.UndoAsync()).Status);
        Assert.Equal(packet, (await store.ReadAsync(target)).Snapshot!.RecipeXmp);
        Assert.Equal(EditHistoryStatus.Applied, (await history.RedoAsync()).Status);
        Assert.Equal(updated, (await store.ReadAsync(target)).Snapshot!.RecipeXmp);
        Assert.Equal(raw, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(EditHistoryStatus.Applied, history.Clear().Status);
    }

    [Fact]
    public async Task ConcurrentVersionSavesDoNotOverwriteTheWinningRecipe()
    {
        var path = Path.Combine(_directory, "source.cr3");
        byte[] raw = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(path, raw);
        var firstHistory = new EditHistoryService();
        var secondHistory = new EditHistoryService();
        var first = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history", "first"), firstHistory, () => Task.CompletedTask);
        var second = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history", "second"), secondHistory, () => Task.CompletedTask);
        var created = await first.CreateFamilyAsync(path, "Original", "Copy");
        Assert.Equal(PhotoVersionRecipeStatus.Success, created.Status);
        var snapshot = created.Snapshot!;
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionRecipePacket.TryRead(snapshot.RecipeXmp, true, out var firstRecipe));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionRecipePacket.TryRead(snapshot.RecipeXmp, true, out var secondRecipe));
        firstRecipe!.RawExposureEv = 0.5;
        secondRecipe!.RawExposureEv = 1.0;
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionRecipePacket.TryUpdate(snapshot.RecipeXmp, true, firstRecipe, out var firstPacket));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionRecipePacket.TryUpdate(snapshot.RecipeXmp, true, secondRecipe, out var secondPacket));

        var results = await Task.WhenAll(
            first.WriteRecipeAsync(snapshot, firstPacket!),
            second.WriteRecipeAsync(snapshot, secondPacket!));
        var applied = results.Select((result, index) => (result, index))
            .Where(item => item.result.Status == PhotoVersionRecipeStatus.Success ||
                item.result.Message?.StartsWith("The version change was applied", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.True(applied.Length > 0, string.Join("; ", results.Select(result => $"{result.Status}: {result.Message}")));
        var winner = Assert.Single(applied);
        var loser = results[1 - winner.index];
        Assert.True(loser.Status is PhotoVersionRecipeStatus.Conflict or PhotoVersionRecipeStatus.Busy);
        var selected = await first.ReadAsync(snapshot.Target);
        Assert.Equal(PhotoVersionRecipeStatus.Success, selected.Status);
        Assert.Equal(winner.index == 0 ? firstPacket : secondPacket, selected.Snapshot!.RecipeXmp);
        Assert.Equal(raw, await File.ReadAllBytesAsync(path));
        Assert.Equal(EditHistoryStatus.Applied, firstHistory.Clear().Status);
        Assert.Equal(EditHistoryStatus.Applied, secondHistory.Clear().Status);
    }

    [Fact]
    public async Task PublishedCreationReportsWhenItsSnapshotCannotBeReloaded()
    {
        var source = Path.Combine(_directory, "source.cr3");
        var moved = Path.Combine(_directory, "moved.cr3");
        byte[] raw = [1, 2, 3];
        await File.WriteAllBytesAsync(source, raw);
        var history = new EditHistoryService();
        var store = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history"), history, () =>
            {
                File.Move(source, moved);
                return Task.CompletedTask;
            });

        var result = await store.CreateFamilyAsync(source, "Original", "Copy");
        Assert.Equal(PhotoVersionRecipeStatus.SourceMissing, result.Status);
        Assert.Contains("was created", result.Message);
        Assert.True(history.CanUndo);
        Assert.Equal(raw, await File.ReadAllBytesAsync(moved));
        Assert.Equal(EditHistoryStatus.Conflict, (await history.UndoAsync()).Status);
        Assert.Equal(EditHistoryStatus.Applied, history.Clear().Status);
    }

    [Fact]
    public async Task MissingVersionAndChangedRawAreNeverSubstituted()
    {
        var path = Path.Combine(_directory, "source.cr3");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        var identity = await RecognitionSourceIdentity.ComputeAsync(path);
        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters());
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        var sourceId = Guid.NewGuid();
        var copyId = Guid.NewGuid();
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            sourceId, identity, Guid.NewGuid(), "Original", packet, copyId, "Copy", out var family));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, family!, null));
        var store = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history"), new EditHistoryService(), () => Task.CompletedTask);

        Assert.Equal(PhotoVersionRecipeStatus.VersionMissing,
            (await store.ReadAsync(new PhotoVersionTarget(path, sourceId, Guid.NewGuid()))).Status);
        var target = new PhotoVersionTarget(path, sourceId, copyId);
        var snapshot = (await store.ReadAsync(target)).Snapshot!;
        await MetadataWriterService.WriteColorLabelAsync(path, "Blue");
        Assert.Equal(PhotoVersionRecipeStatus.Conflict,
            (await store.WriteRecipeAsync(snapshot, packet)).Status);
        Assert.Equal(family, MetadataWriterService.ReadVersionFamily(path).Value);
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryRead(family!, out var document));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            document!.TryRemoveCopy(copyId, document.Revision, out var withoutCopy));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, withoutCopy!, family));
        Assert.Equal(PhotoVersionRecipeStatus.VersionMissing,
            (await store.WriteRecipeAsync(snapshot, packet)).Status);
        Assert.Equal(withoutCopy, MetadataWriterService.ReadVersionFamily(path).Value);
        await File.WriteAllBytesAsync(path, [1, 2, 4], TestContext.Current.CancellationToken);
        Assert.Equal(PhotoVersionRecipeStatus.Conflict,
            (await store.ReadAsync(new PhotoVersionTarget(path, sourceId, document.MainId))).Status);
    }

    [Fact]
    public async Task UnintegratedMonochromeCopyIsUnavailableWithoutSubstitutingMain()
    {
        var path = Path.Combine(_directory, "source.cr3");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        var identity = await RecognitionSourceIdentity.ComputeAsync(path);
        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters());
        var mainPacket = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:MonochromeMixVersion", "1");
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:MonochromeMixV1",
            """{"LuminanceBands":[0.5,0,0,0,0,0,0,0]}""");
        var copyPacket = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        var sourceId = Guid.NewGuid();
        var mainId = Guid.NewGuid();
        var copyId = Guid.NewGuid();
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            sourceId, identity, mainId, "Original", mainPacket, copyId, "Copy", out var initial));
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryRead(initial!, out var document));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            document!.TryUpdateRecipe(copyId, document.Revision, copyPacket, out var family));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, family!, null));
        var sidecar = Path.ChangeExtension(path, ".xmp");
        var before = await File.ReadAllBytesAsync(sidecar, TestContext.Current.CancellationToken);
        var store = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history"), new EditHistoryService(), () => Task.CompletedTask);

        var listed = await store.ListAsync(path);
        Assert.Equal(PhotoVersionRecipeStatus.Success, listed.Status);
        Assert.Equal(PhotoVersionRecipeStatus.Success, listed.Records![0].Status);
        Assert.Equal(PhotoVersionRecipeStatus.Unsupported, listed.Records[1].Status);
        Assert.Null(listed.Records[1].Snapshot);
        Assert.Equal(mainPacket, (await store.ReadAsync(new(path, sourceId, mainId))).Snapshot!.RecipeXmp);
        var copy = await store.ReadAsync(new(path, sourceId, copyId));
        Assert.Equal(PhotoVersionRecipeStatus.Unsupported, copy.Status);
        Assert.Null(copy.Snapshot);
        Assert.Equal(before, await File.ReadAllBytesAsync(sidecar, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RasterVersionRemainsUnavailableWithoutDurablePixelIdentity()
    {
        var path = Path.Combine(_directory, "source.png");
        await WicTestImage.CreateAsync(path, 16, 16);
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var history = new EditHistoryService();
        var store = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history"), history, () => Task.CompletedTask);
        var target = new PhotoVersionTarget(path, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(PhotoVersionRecipeStatus.Unsupported, (await store.ReadAsync(target)).Status);
        var snapshot = new PhotoVersionSnapshot(target, Guid.NewGuid(), "stale", "<recipe/>");
        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            (await store.WriteRecipeAsync(snapshot, "<recipe/>")).Status);
        Assert.False(history.CanUndo);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FirstCopyDoesNotMigrateARecipeWithUnknownQualifiers()
    {
        const string futureNamespace = "https://photolibrarian.test/store-qualifiers/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(futureNamespace, "futurestore");
        var path = Path.Combine(_directory, "qualified.cr3");
        byte[] raw = [1, 2, 3];
        await File.WriteAllBytesAsync(path, raw, TestContext.Current.CancellationToken);
        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters { RawExposureEv = 0.5 });
        xmp.SetQualifier(EditParameters.XmpNamespace, "pledit:RawExposureEv",
            futureNamespace, "futurestore:meaning", "keep");
        var sidecar = Path.ChangeExtension(path, ".xmp");
        var original = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        await File.WriteAllTextAsync(sidecar, original, TestContext.Current.CancellationToken);
        var originalBytes = await File.ReadAllBytesAsync(sidecar, TestContext.Current.CancellationToken);
        var history = new EditHistoryService();
        var store = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history"), history, () => Task.CompletedTask);

        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            (await store.CreateFamilyAsync(path, "Original", "Copy")).Status);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sidecar, TestContext.Current.CancellationToken));
        Assert.Equal(raw, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.False(history.CanUndo);
    }

    [Fact]
    public async Task LockedSourceReportsBusyWithoutChangingIt()
    {
        var path = Path.Combine(_directory, "source.cr3");
        byte[] raw = [1, 2, 3];
        await File.WriteAllBytesAsync(path, raw, TestContext.Current.CancellationToken);
        var store = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history"), new EditHistoryService(), () => Task.CompletedTask);
        var target = new PhotoVersionTarget(path, Guid.NewGuid(), Guid.NewGuid());

        await using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(PhotoVersionRecipeStatus.Busy, (await store.ReadAsync(target)).Status);

        Assert.Equal(raw, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NewSameStemCompanionPreventsPublishingAStagedFamilyEdit()
    {
        var path = Path.Combine(_directory, "source.cr3");
        byte[] raw = [1, 2, 3];
        await File.WriteAllBytesAsync(path, raw);
        var sidecar = Path.ChangeExtension(path, ".xmp");
        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters());
        var original = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        await File.WriteAllTextAsync(sidecar, original);
        var before = await File.ReadAllBytesAsync(sidecar);
        using var transaction = await FileEditTransaction.PrepareAsync(
            Path.Combine(_directory, "history"), [path], copies =>
            {
                var stagedSidecar = Path.ChangeExtension(copies[path], ".xmp");
                var staged = XmpMetaFactory.ParseFromString(File.ReadAllText(stagedSidecar));
                staged.SetProperty(EditParameters.XmpNamespace, "pledit:Exposure", 0.5);
                File.WriteAllText(stagedSidecar,
                    XmpMetaFactory.SerializeToString(staged, new XmpCore.Options.SerializeOptions()));
                return Task.CompletedTask;
            }, durableRecovery: true);
        var companion = Path.ChangeExtension(path, ".jpg");
        await File.WriteAllBytesAsync(companion, [4, 5]);

        Assert.Equal(EditHistoryStatus.Conflict,
            (await transaction.MoveAsync(false, () => Task.CompletedTask)).Status);
        Assert.Equal(before, await File.ReadAllBytesAsync(sidecar));
        Assert.Equal(raw, await File.ReadAllBytesAsync(path));
        Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(companion));
    }

    [Fact]
    public async Task CopyMutationsUseExactRevisionAndUndoRestoresTheSameVersion()
    {
        var path = Path.Combine(_directory, "source.cr3");
        byte[] raw = [4, 5, 6];
        await File.WriteAllBytesAsync(path, raw, TestContext.Current.CancellationToken);
        var identity = await RecognitionSourceIdentity.ComputeAsync(path);
        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters { RawExposureEv = 0.5 });
        var packet = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        var sourceId = Guid.NewGuid();
        var mainId = Guid.NewGuid();
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryCreate(
            sourceId, identity, mainId, "Original", packet, Guid.NewGuid(), "First", out var family));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(path, family!, null));
        var history = new EditHistoryService();
        var store = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history"), history, () => Task.CompletedTask);
        var mainTarget = new PhotoVersionTarget(path, sourceId, mainId);
        var main = (await store.ReadAsync(mainTarget)).Snapshot!;
        var copyId = Guid.NewGuid();
        var added = await store.AddCopyAsync(main, copyId, "Second");
        Assert.Equal(PhotoVersionRecipeStatus.Success, added.Status);
        Assert.Equal(packet, added.Snapshot!.RecipeXmp);
        Assert.Equal(PhotoVersionRecipeStatus.Conflict,
            (await store.AddCopyAsync(main, Guid.NewGuid(), "Stale")).Status);
        Assert.Equal(PhotoVersionRecipeStatus.VersionMissing,
            (await store.RemoveCopyAsync((await store.ReadAsync(mainTarget)).Snapshot!)).Status);

        var renamed = await store.RenameCopyAsync(added.Snapshot, "Renamed");
        Assert.Equal(PhotoVersionRecipeStatus.Success, renamed.Status);
        Assert.Equal(PhotoVersionRecipeStatus.Conflict,
            (await store.RemoveCopyAsync(added.Snapshot)).Status);
        Assert.Equal(PhotoVersionRecipeStatus.Success, (await store.RemoveCopyAsync(renamed.Snapshot!)).Status);
        Assert.Equal(PhotoVersionRecipeStatus.VersionMissing,
            (await store.ReadAsync(renamed.Snapshot!.Target)).Status);
        Assert.Equal(EditHistoryStatus.Applied, (await history.UndoAsync()).Status);
        var restored = await store.ReadAsync(renamed.Snapshot.Target);
        Assert.Equal(PhotoVersionRecipeStatus.Success, restored.Status);
        Assert.Equal(packet, restored.Snapshot!.RecipeXmp);
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionDocument.TryRead(MetadataWriterService.ReadVersionFamily(path).Value!, out var restoredFamily));
        Assert.Equal("Renamed", restoredFamily!.GetRecords().Single(record => record.Id == copyId).Name);
        Assert.Equal(EditHistoryStatus.Applied, (await history.RedoAsync()).Status);
        Assert.Equal(PhotoVersionRecipeStatus.VersionMissing, (await store.ReadAsync(renamed.Snapshot.Target)).Status);
        await MetadataWriterService.WriteColorLabelAsync(path, "Blue");
        var afterExternalChange = await File.ReadAllBytesAsync(
            Path.ChangeExtension(path, ".xmp"), TestContext.Current.CancellationToken);
        Assert.Equal(EditHistoryStatus.Conflict, (await history.UndoAsync()).Status);
        Assert.True(history.CanUndo);
        Assert.Equal(afterExternalChange, await File.ReadAllBytesAsync(
            Path.ChangeExtension(path, ".xmp"), TestContext.Current.CancellationToken));
        Assert.Equal(raw, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(EditHistoryStatus.Applied, history.Clear().Status);
    }

    [Fact]
    public async Task FirstCopyMigratesRawRecipeWithoutLosingUnknownOrSharedMetadata()
    {
        const string futureNamespace = "https://photolibrarian.test/migration-qualifier/";
        XmpMetaFactory.SchemaRegistry.RegisterNamespace(futureNamespace, "futuremigration");
        var path = Path.Combine(_directory, "source.cr3");
        byte[] raw = [7, 8, 9];
        await File.WriteAllBytesAsync(path, raw, TestContext.Current.CancellationToken);
        var sidecarPath = Path.ChangeExtension(path, ".xmp");
        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters { RawExposureEv = 0.75 });
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:FutureField", "opaque");
        xmp.SetQualifier(EditParameters.XmpNamespace, "pledit:FutureField",
            futureNamespace, "futuremigration:meaning", "retain");
        xmp.AppendArrayItem(EditParameters.XmpNamespace, "pledit:FutureArray",
            new XmpCore.Options.PropertyOptions { IsArray = true }, "future value", null);
        xmp.SetLocalizedText("http://purl.org/dc/elements/1.1/", "dc:description", "", "x-default", "caption");
        await File.WriteAllTextAsync(sidecarPath,
            XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()),
            TestContext.Current.CancellationToken);
        var originalSidecar = await File.ReadAllBytesAsync(sidecarPath, TestContext.Current.CancellationToken);
        var history = new EditHistoryService();
        var store = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history"), history, () => Task.CompletedTask);

        var created = await store.CreateFamilyAsync(path, "Original", "Copy 1");
        Assert.Equal(PhotoVersionRecipeStatus.Success, created.Status);
        var storedPacket = XmpMetaFactory.ParseFromString(created.Snapshot!.RecipeXmp);
        Assert.Equal("opaque", storedPacket.GetPropertyString(EditParameters.XmpNamespace, "pledit:FutureField"));
        Assert.Equal("retain", storedPacket.GetQualifier(EditParameters.XmpNamespace,
            "pledit:FutureField", futureNamespace, "futuremigration:meaning").Value);
        Assert.Equal("future value", storedPacket.GetArrayItem(
            EditParameters.XmpNamespace, "pledit:FutureArray", 1)!.Value);
        Assert.False(storedPacket.DoesPropertyExist("http://purl.org/dc/elements/1.1/", "dc:description"));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionRecipePacket.TryRead(created.Snapshot.RecipeXmp, true, out var parameters));
        Assert.Equal(0.75, parameters!.RawExposureEv);
        parameters.RawExposureEv = 1.25;
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionRecipePacket.TryUpdate(created.Snapshot.RecipeXmp, true, parameters, out var updatedPacket));
        var updatedXmp = XmpMetaFactory.ParseFromString(updatedPacket!);
        Assert.Equal("retain", updatedXmp.GetQualifier(EditParameters.XmpNamespace,
            "pledit:FutureField", futureNamespace, "futuremigration:meaning").Value);
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionRecipePacket.ValidateReplacement(created.Snapshot.RecipeXmp, updatedPacket!, true));
        var sidecar = XmpMetaFactory.ParseFromString(await File.ReadAllTextAsync(
            sidecarPath, TestContext.Current.CancellationToken));
        Assert.Equal("caption", sidecar.GetLocalizedText(
            "http://purl.org/dc/elements/1.1/", "dc:description", "", "x-default")!.Value);
        Assert.False(sidecar.DoesPropertyExist(EditParameters.XmpNamespace, "pledit:FutureField"));
        Assert.False(sidecar.DoesPropertyExist(EditParameters.XmpNamespace, "pledit:FutureArray"));
        Assert.False(MetadataSidecarLocator.OwnsPendingEditRecipe(sidecar));
        Assert.Equal(PhotoVersionRecipeStatus.Conflict, (await store.CreateFamilyAsync(path, "Original", "Again")).Status);
        Assert.Equal(EditHistoryStatus.Applied, (await history.UndoAsync()).Status);
        Assert.Equal(originalSidecar, await File.ReadAllBytesAsync(sidecarPath, TestContext.Current.CancellationToken));
        Assert.Equal(EditHistoryStatus.Applied, (await history.RedoAsync()).Status);
        Assert.Equal(PhotoVersionRecipeStatus.Success, (await store.ReadAsync(created.Snapshot.Target)).Status);
        Assert.Equal(raw, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(EditHistoryStatus.Applied, history.Clear().Status);
    }

    [Fact]
    public async Task FirstCopyRejectsAmbiguousLegacySidecarWithoutChanges()
    {
        var path = Path.Combine(_directory, "source.cr3");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        await WicTestImage.CreateAsync(Path.Combine(_directory, "source.jpg"), 8, 8, jpeg: true);
        var sidecarPath = Path.ChangeExtension(path, ".xmp");
        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters { RawExposureEv = 0.5 });
        await File.WriteAllTextAsync(sidecarPath,
            XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions()),
            TestContext.Current.CancellationToken);
        var before = await File.ReadAllBytesAsync(sidecarPath, TestContext.Current.CancellationToken);
        var history = new EditHistoryService();
        var store = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history"), history, () => Task.CompletedTask);

        Assert.Equal(PhotoVersionRecipeStatus.Conflict,
            (await store.CreateFamilyAsync(path, "Original", "Copy")).Status);
        Assert.False(history.CanUndo);
        Assert.Equal(before, await File.ReadAllBytesAsync(sidecarPath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path + ".xmp"));
    }

    [Fact]
    public async Task FirstCopyRejectsConflictingAndUnsupportedLegacyRecipes()
    {
        var path = Path.Combine(_directory, "source.cr3");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        var sidecarPath = Path.ChangeExtension(path, ".xmp");
        var qualifiedPath = path + ".xmp";
        var xmp = XmpMetaFactory.Create();
        EditParametersSerializer.WriteToXmp(xmp, new EditParameters());
        var original = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        await File.WriteAllTextAsync(sidecarPath, original, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(qualifiedPath, original, TestContext.Current.CancellationToken);
        var beforeLegacy = await File.ReadAllBytesAsync(sidecarPath, TestContext.Current.CancellationToken);
        var beforeQualified = await File.ReadAllBytesAsync(qualifiedPath, TestContext.Current.CancellationToken);
        var history = new EditHistoryService();
        var store = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history"), history, () => Task.CompletedTask);
        Assert.Equal(PhotoVersionRecipeStatus.Conflict,
            (await store.CreateFamilyAsync(path, "Original", "Copy")).Status);
        Assert.Equal(beforeLegacy, await File.ReadAllBytesAsync(sidecarPath, TestContext.Current.CancellationToken));
        Assert.Equal(beforeQualified, await File.ReadAllBytesAsync(qualifiedPath, TestContext.Current.CancellationToken));

        File.Delete(qualifiedPath);
        xmp.SetProperty(EditParameters.XmpNamespace, "pledit:RawDevelopment", "future");
        var unsupported = XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
        await File.WriteAllTextAsync(sidecarPath, unsupported, TestContext.Current.CancellationToken);
        var beforeUnsupported = await File.ReadAllBytesAsync(sidecarPath, TestContext.Current.CancellationToken);
        Assert.Equal(PhotoVersionRecipeStatus.Unsupported,
            (await store.CreateFamilyAsync(path, "Original", "Copy")).Status);
        Assert.Equal(beforeUnsupported, await File.ReadAllBytesAsync(sidecarPath, TestContext.Current.CancellationToken));
        Assert.False(history.CanUndo);
    }

    [Fact]
    public async Task FirstCopyWithoutExistingRecipeCreatesOwnedFamilyAndCanBeUndone()
    {
        var path = Path.Combine(_directory, "source.cr3");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        await WicTestImage.CreateAsync(Path.Combine(_directory, "source.jpg"), 8, 8, jpeg: true);
        var history = new EditHistoryService();
        var store = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history"), history, () => Task.CompletedTask);
        var created = await store.CreateFamilyAsync(path, "Original", "Copy");
        Assert.Equal(PhotoVersionRecipeStatus.Success, created.Status);
        Assert.True(File.Exists(path + ".xmp"));
        Assert.False(File.Exists(Path.ChangeExtension(path, ".xmp")));
        Assert.Equal(EditHistoryStatus.Applied, (await history.UndoAsync()).Status);
        Assert.False(File.Exists(path + ".xmp"));
        Assert.Equal(EditHistoryStatus.Applied, (await history.RedoAsync()).Status);
        Assert.Equal(PhotoVersionRecipeStatus.Success, (await store.ReadAsync(created.Snapshot!.Target)).Status);
        Assert.Equal(EditHistoryStatus.Applied, history.Clear().Status);
    }

    [Fact]
    public async Task MalformedSidecarIsReportedAndPreserved()
    {
        var path = Path.Combine(_directory, "source.cr3");
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
        var sidecarPath = Path.ChangeExtension(path, ".xmp");
        await File.WriteAllTextAsync(sidecarPath, "<broken", TestContext.Current.CancellationToken);
        var history = new EditHistoryService();
        var store = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history"), history, () => Task.CompletedTask);
        var target = new PhotoVersionTarget(path, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata, (await store.ReadAsync(target)).Status);
        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            (await store.CreateFamilyAsync(path, "Original", "Copy")).Status);
        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata,
            (await store.WriteRecipeAsync(new PhotoVersionSnapshot(target, Guid.NewGuid(), "", ""), "")).Status);
        Assert.Equal("<broken", await File.ReadAllTextAsync(sidecarPath, TestContext.Current.CancellationToken));
        Assert.False(history.CanUndo);
    }

    [Fact]
    public async Task MalformedRasterIsReportedDuringFamilyEnumeration()
    {
        var path = Path.Combine(_directory, "malformed.png");
        byte[] bytes = [1, 2, 3];
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

        var result = await PhotoVersionRecipeStore.ListFamilyAsync(path);

        Assert.Equal(PhotoVersionRecipeStatus.InvalidMetadata, result.Status);
        Assert.Null(result.Records);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RelocatedAndDuplicatedRawFamilyRemainsPortableAndIndependent()
    {
        var source = Path.Combine(_directory, "source.cr3");
        await File.WriteAllBytesAsync(source, [2, 4, 6], TestContext.Current.CancellationToken);
        var history = new EditHistoryService();
        var store = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history"), history, () => Task.CompletedTask);
        var original = (await store.CreateFamilyAsync(source, "Original", "Copy")).Snapshot!;
        var relocatedDirectory = Path.Combine(_directory, "relocated");
        Directory.CreateDirectory(relocatedDirectory);
        var relocated = Path.Combine(relocatedDirectory, "renamed.cr3");
        File.Copy(source, relocated);
        File.Copy(Path.ChangeExtension(source, ".xmp"), Path.ChangeExtension(relocated, ".xmp"));

        var relocatedTarget = original.Target with { SourcePath = relocated };
        var copy = await store.ReadAsync(relocatedTarget);
        Assert.Equal(PhotoVersionRecipeStatus.Success, copy.Status);
        Assert.Equal(original.RecipeXmp, copy.Snapshot!.RecipeXmp);
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionRecipePacket.TryRead(copy.Snapshot.RecipeXmp, true, out var parameters));
        parameters!.RawExposureEv = 1.0;
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            PhotoVersionRecipePacket.TryUpdate(copy.Snapshot.RecipeXmp, true, parameters, out var updated));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            (await store.WriteRecipeAsync(copy.Snapshot, updated!)).Status);
        Assert.Equal(original.RecipeXmp, (await store.ReadAsync(original.Target)).Snapshot!.RecipeXmp);
        Assert.Equal(updated, (await store.ReadAsync(relocatedTarget)).Snapshot!.RecipeXmp);
        Assert.Equal(EditHistoryStatus.Applied, history.Clear().Status);
    }

    [Fact]
    public async Task StagedPhysicalCopyRekeysFamilyWithoutChangingTheOriginal()
    {
        var source = Path.Combine(_directory, "source.cr3");
        await File.WriteAllBytesAsync(source, [2, 4, 6], TestContext.Current.CancellationToken);
        var history = new EditHistoryService();
        var store = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history"), history, () => Task.CompletedTask);
        var original = (await store.CreateFamilyAsync(source, "Original", "Copy")).Snapshot!;
        var family = MetadataWriterService.ReadVersionFamily(source).Value!;
        var originalSidecar = await File.ReadAllBytesAsync(
            Path.ChangeExtension(source, ".xmp"), TestContext.Current.CancellationToken);
        var destination = Path.Combine(_directory, "destination.cr3");
        File.Copy(source, destination);
        File.Copy(Path.ChangeExtension(source, ".xmp"), Path.ChangeExtension(destination, ".xmp"));
        Guid newSource = Guid.Empty;
        IReadOnlyDictionary<Guid, Guid>? newIds = null;
        using (var transaction = await FileEditTransaction.PrepareAsync(
            Path.Combine(_directory, "history"), [destination], async copies =>
            {
                var (status, sourceId, ids) = await MetadataWriterService.RekeyCopiedRawFamilyOnStagedFileAsync(
                    copies[destination], family, TestContext.Current.CancellationToken);
                Assert.Equal(PhotoVersionRecipeStatus.Success, status);
                newSource = sourceId;
                newIds = ids;
            }, TestContext.Current.CancellationToken))
        {
            Assert.Equal(EditHistoryStatus.Applied, (await transaction.MoveAsync(false, () => Task.CompletedTask)).Status);
        }
        Assert.NotEqual(original.Target.SourceId, newSource);
        Assert.NotEqual(original.Target.VariantId, newIds![original.Target.VariantId]);
        var newTarget = new PhotoVersionTarget(destination, newSource, newIds[original.Target.VariantId]);
        Assert.Equal(original.RecipeXmp, (await store.ReadAsync(newTarget)).Snapshot!.RecipeXmp);
        Assert.Equal(PhotoVersionRecipeStatus.Conflict,
            (await store.ReadAsync(original.Target with { SourcePath = destination })).Status);
        Assert.Equal(family, MetadataWriterService.ReadVersionFamily(source).Value);
        Assert.Equal(originalSidecar, await File.ReadAllBytesAsync(
            Path.ChangeExtension(source, ".xmp"), TestContext.Current.CancellationToken));
        Assert.Equal(EditHistoryStatus.Applied, history.Clear().Status);
    }

    [Fact]
    public async Task FamilyEnumerationKeepsUnsupportedVersionsDistinctAndUnavailable()
    {
        var source = Path.Combine(_directory, "source.cr3");
        await File.WriteAllBytesAsync(source, [2, 4, 6], TestContext.Current.CancellationToken);
        var history = new EditHistoryService();
        var store = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history"), history, () => Task.CompletedTask);
        var created = (await store.CreateFamilyAsync(source, "Original", "Copy")).Snapshot!;
        var family = MetadataWriterService.ReadVersionFamily(source).Value!;
        Assert.Equal(PhotoVersionRecipeStatus.Success, PhotoVersionDocument.TryRead(family, out var document));
        var packet = XmpMetaFactory.ParseFromString(created.RecipeXmp);
        packet.SetProperty(EditParameters.XmpNamespace, "pledit:RawDevelopment", "future");
        var unsupported = XmpMetaFactory.SerializeToString(packet, new XmpCore.Options.SerializeOptions());
        Assert.Equal(PhotoVersionRecipeStatus.Success, document!.TryUpdateRecipe(
            created.Target.VariantId, document.Revision, unsupported, out var updated));
        Assert.Equal(PhotoVersionRecipeStatus.Success,
            await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(source, updated!, family));

        var result = await store.ListAsync(source);
        Assert.Equal(PhotoVersionRecipeStatus.Success, result.Status);
        Assert.Equal(2, result.Records!.Count);
        Assert.True(result.Records[0].IsMain);
        Assert.Equal(PhotoVersionRecipeStatus.Success, result.Records[0].Status);
        Assert.NotNull(result.Records[0].Snapshot);
        Assert.Equal(created.Target.VariantId, result.Records[1].Target.VariantId);
        Assert.Equal("Copy", result.Records[1].Name);
        Assert.Equal(PhotoVersionRecipeStatus.Unsupported, result.Records[1].Status);
        Assert.Null(result.Records[1].Snapshot);
        Assert.Equal(PhotoVersionRecipeStatus.Unsupported, (await store.ReadAsync(created.Target)).Status);
        File.Delete(Path.ChangeExtension(source, ".xmp"));
        var missing = await store.ListAsync(source);
        Assert.Equal(PhotoVersionRecipeStatus.VersionMissing, missing.Status);
        Assert.Null(missing.Records);
        Assert.Equal(EditHistoryStatus.Applied, history.Clear().Status);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
