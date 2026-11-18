// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using System.ComponentModel;
using XmpCore;

namespace PhotoLibrarian.Core.Services;

/// <summary>Transactional access to recipes already stored in a portable family.</summary>
internal sealed class PhotoVersionRecipeStore(
    string historyDirectory,
    EditHistoryService history,
    Func<Task> refresh) : IPhotoVersionRecipeStore
{
    internal async Task<PhotoVersionRecipeResult> CreateFamilyAsync(
        string sourcePath, string mainName, string copyName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath))
            return new(PhotoVersionRecipeStatus.SourceMissing);
        if (!RawToneMappingService.IsRaw(sourcePath))
            return new(PhotoVersionRecipeStatus.Unsupported,
                Message: "A durable raster pixel identity is not yet available.");
        if (history.IsBusy)
            return new(PhotoVersionRecipeStatus.Busy);

        FileEditTransaction prepared;
        PhotoVersionRecipeStatus status = PhotoVersionRecipeStatus.Success;
        PhotoVersionTarget? target = null;
        string inputIdentity;
        try
        {
            if (HasUnsafeRecoveryPath(sourcePath))
                return new(PhotoVersionRecipeStatus.Conflict,
                    Message: "Photo-version recovery cannot safely use a path containing a reparse point.");
            var recoveryRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(historyDirectory)));
            if (recoveryRoot is null)
                return new(PhotoVersionRecipeStatus.Conflict, Message: "The edit-history directory has no recovery root.");
            inputIdentity = await FileEditTransaction.ReadIdentityAsync(sourcePath, cancellationToken);
            prepared = await FileEditTransaction.PrepareAsync(
                historyDirectory, [sourcePath], async copies =>
                {
                    var result = await MetadataWriterService.CreateRawVersionFamilyOnStagedFileAsync(
                        copies[sourcePath], mainName, copyName, cancellationToken);
                    status = result.Status;
                    target = result.Copy is null ? null : result.Copy with { SourcePath = sourcePath };
                }, cancellationToken, durableRecovery: true, recoveryRoot: recoveryRoot);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new(PhotoVersionRecipeStatus.ReadOnly, Message: exception.Message);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 5)
        {
            return new(PhotoVersionRecipeStatus.ReadOnly, Message: exception.Message);
        }
        catch (FileNotFoundException)
        {
            return new(File.Exists(sourcePath)
                ? PhotoVersionRecipeStatus.Conflict : PhotoVersionRecipeStatus.SourceMissing);
        }
        catch (DirectoryNotFoundException)
        {
            return new(PhotoVersionRecipeStatus.SourceMissing);
        }
        catch (XmpException exception)
        {
            return new(PhotoVersionRecipeStatus.InvalidMetadata, Message: exception.Message);
        }
        catch (IOException exception) when (exception.InnerException is XmpException)
        {
            return new(PhotoVersionRecipeStatus.InvalidMetadata, Message: exception.Message);
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
        {
            return new(PhotoVersionRecipeStatus.Busy, Message: exception.Message);
        }

        var retained = false;
        var publishing = false;
        try
        {
            if (status != PhotoVersionRecipeStatus.Success)
                return new(status);
            if (!string.Equals(prepared.InputIdentity(sourcePath), inputIdentity, StringComparison.Ordinal))
                return new(PhotoVersionRecipeStatus.Conflict);
            cancellationToken.ThrowIfCancellationRequested();
            publishing = true;
            var historyResult = await history.ExecuteAsync(
                "Create photo version", () => prepared.MoveAsync(false, refresh),
                undo => prepared.MoveAsync(undo, refresh), prepared.Dispose);
            publishing = false;
            if (historyResult.Status != EditHistoryStatus.Applied)
                return new(historyResult.Status == EditHistoryStatus.Busy
                    ? PhotoVersionRecipeStatus.Busy : PhotoVersionRecipeStatus.Conflict,
                    Message: historyResult.Message);
            retained = true;
            var result = await ReadAsync(target!, CancellationToken.None);
            return result.Status == PhotoVersionRecipeStatus.Success
                ? result with { Message = historyResult.Message }
                : result with
                {
                    Message = ("The version was created, but its latest state could not be reloaded. " +
                        historyResult.Message).TrimEnd()
                };
        }
        finally
        {
            if (!retained && !publishing)
                prepared.Dispose();
        }
    }

    public async Task<PhotoVersionRecipeResult> ReadAsync(
        PhotoVersionTarget target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(target.SourcePath))
            return new(PhotoVersionRecipeStatus.SourceMissing);
        if (!RawToneMappingService.IsRaw(target.SourcePath))
            return new(PhotoVersionRecipeStatus.Unsupported,
                Message: "A durable raster pixel identity is not yet available.");

        try
        {
            var before = await FileEditTransaction.ReadIdentityAsync(target.SourcePath, cancellationToken);
            var result = await ReadFamilyAsync(target, cancellationToken);
            var after = await FileEditTransaction.ReadIdentityAsync(target.SourcePath, cancellationToken);
            if (!string.Equals(before, after, StringComparison.Ordinal))
                return new(PhotoVersionRecipeStatus.Conflict, Message: "The source changed while reading its version.");
            if (result.Status != PhotoVersionRecipeStatus.Success)
                return new(result.Status);
            return new(PhotoVersionRecipeStatus.Success,
                new PhotoVersionSnapshot(target, result.Document!.Revision, before, result.Recipe!));
        }
        catch (UnauthorizedAccessException exception)
        {
            return new(PhotoVersionRecipeStatus.ReadOnly, Message: exception.Message);
        }
        catch (FileNotFoundException)
        {
            return new(File.Exists(target.SourcePath)
                ? PhotoVersionRecipeStatus.Conflict : PhotoVersionRecipeStatus.SourceMissing);
        }
        catch (DirectoryNotFoundException)
        {
            return new(PhotoVersionRecipeStatus.SourceMissing);
        }
        catch (XmpException exception)
        {
            return new(PhotoVersionRecipeStatus.InvalidMetadata, Message: exception.Message);
        }
        catch (IOException exception) when (exception.InnerException is XmpException)
        {
            return new(PhotoVersionRecipeStatus.InvalidMetadata, Message: exception.Message);
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
        {
            return new(PhotoVersionRecipeStatus.Busy, Message: exception.Message);
        }
    }

    internal Task<PhotoVersionFamilyResult> ListAsync(
        string sourcePath, CancellationToken cancellationToken = default) =>
        ListFamilyAsync(sourcePath, cancellationToken);

    internal static async Task<PhotoVersionFamilyResult> ListFamilyAsync(
        string sourcePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(sourcePath))
            return new(PhotoVersionRecipeStatus.SourceMissing);
        var isRaw = RawToneMappingService.IsRaw(sourcePath);
        var supportedFamilyTransport = MetadataWriterService.SupportsEmbeddedWrite(
            sourcePath, MetadataWriteProperty.PhotoVersionFamily) ||
            Path.GetExtension(sourcePath).Equals(".bmp", StringComparison.OrdinalIgnoreCase);
        if (!isRaw && !supportedFamilyTransport)
            return new(PhotoVersionRecipeStatus.Unsupported,
                Message: "A durable raster pixel identity is not yet available.");
        try
        {
            var before = await FileEditTransaction.ReadIdentityAsync(sourcePath, cancellationToken);
            var (status, value) = MetadataWriterService.ReadVersionFamily(sourcePath, cancellationToken);
            PhotoVersionDocument? document = null;
            if (status == PhotoVersionRecipeStatus.Success)
                status = PhotoVersionDocument.TryRead(value!, out document);
            if (status == PhotoVersionRecipeStatus.Success && !isRaw)
                status = PhotoVersionRecipeStatus.Unsupported;
            if (status == PhotoVersionRecipeStatus.Success)
            {
                var identity = await RecognitionSourceIdentity.ComputeAsync(sourcePath, cancellationToken);
                if (!string.Equals(identity, document!.PixelBase, StringComparison.Ordinal))
                    status = PhotoVersionRecipeStatus.Conflict;
            }
            var after = await FileEditTransaction.ReadIdentityAsync(sourcePath, cancellationToken);
            if (!string.Equals(before, after, StringComparison.Ordinal))
                return new(PhotoVersionRecipeStatus.Conflict, Message: "The source changed while reading its versions.");
            if (status != PhotoVersionRecipeStatus.Success)
                return new(status);

            var records = new List<PhotoVersionFamilyRecord>();
            foreach (var record in document!.GetRecords())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = new PhotoVersionTarget(sourcePath, document.SourceId, record.Id);
                var recordStatus = PhotoVersionRecipePacket.TryRead(record.RecipeXmp, isRaw: true, out _);
                var snapshot = recordStatus == PhotoVersionRecipeStatus.Success
                    ? new PhotoVersionSnapshot(target, document.Revision, before, record.RecipeXmp) : null;
                records.Add(new(target, record.Name, record.IsMain, recordStatus, snapshot));
            }
            return new(PhotoVersionRecipeStatus.Success, records,
                DocumentRevision: document.Revision, InputIdentity: before);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new(PhotoVersionRecipeStatus.ReadOnly, Message: exception.Message);
        }
        catch (FileNotFoundException)
        {
            return new(File.Exists(sourcePath)
                ? PhotoVersionRecipeStatus.Conflict : PhotoVersionRecipeStatus.SourceMissing);
        }
        catch (DirectoryNotFoundException)
        {
            return new(PhotoVersionRecipeStatus.SourceMissing);
        }
        catch (XmpException exception)
        {
            return new(PhotoVersionRecipeStatus.InvalidMetadata, Message: exception.Message);
        }
        catch (IOException exception) when (exception.InnerException is XmpException)
        {
            return new(PhotoVersionRecipeStatus.InvalidMetadata, Message: exception.Message);
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
        {
            return new(PhotoVersionRecipeStatus.Busy, Message: exception.Message);
        }
    }

    public Task<PhotoVersionRecipeResult> WriteRecipeAsync(
        PhotoVersionSnapshot expected, string updatedRecipeXmp,
        CancellationToken cancellationToken = default) =>
        MutateAsync(expected, expected.Target, "Save photo version", (document, recipe) =>
        {
            var status = PhotoVersionRecipePacket.ValidateReplacement(recipe, updatedRecipeXmp, isRaw: true);
            if (status != PhotoVersionRecipeStatus.Success)
                return (status, null);
            status = document.TryUpdateRecipe(
                expected.Target.VariantId, expected.DocumentRevision, updatedRecipeXmp, out var updated);
            return (status, updated);
        }, cancellationToken);

    internal Task<PhotoVersionRecipeResult> AddCopyAsync(
        PhotoVersionSnapshot expected, Guid copyId, string name, CancellationToken cancellationToken = default) =>
        MutateAsync(expected, expected.Target with { VariantId = copyId }, "Create photo version",
            (document, _) =>
            {
                var status = document.TryAddCopy(
                    expected.Target.VariantId, copyId, name, expected.DocumentRevision, out var updated);
                return (status, updated);
            }, cancellationToken);

    internal Task<PhotoVersionRecipeResult> RenameCopyAsync(
        PhotoVersionSnapshot expected, string name, CancellationToken cancellationToken = default) =>
        MutateAsync(expected, expected.Target, "Rename photo version",
            (document, _) =>
            {
                var status = document.TryRenameCopy(
                    expected.Target.VariantId, name, expected.DocumentRevision, out var updated);
                return (status, updated);
            }, cancellationToken);

    internal Task<PhotoVersionRecipeResult> RemoveCopyAsync(
        PhotoVersionSnapshot expected, CancellationToken cancellationToken = default) =>
        MutateAsync(expected, null, "Remove photo version",
            (document, _) =>
            {
                var status = document.TryRemoveCopy(
                    expected.Target.VariantId, expected.DocumentRevision, out var updated);
                return (status, updated);
            }, cancellationToken);

    private async Task<PhotoVersionRecipeResult> MutateAsync(
        PhotoVersionSnapshot expected, PhotoVersionTarget? resultTarget, string description,
        Func<PhotoVersionDocument, string, (PhotoVersionRecipeStatus Status, string? Updated)> mutate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = expected.Target;
        if (!File.Exists(target.SourcePath))
            return new(PhotoVersionRecipeStatus.SourceMissing);
        if (!RawToneMappingService.IsRaw(target.SourcePath))
            return new(PhotoVersionRecipeStatus.Unsupported,
                Message: "A durable raster pixel identity is not yet available.");
        if (history.IsBusy)
            return new(PhotoVersionRecipeStatus.Busy);

        var status = PhotoVersionRecipeStatus.Success;
        var sourcePath = Path.GetFullPath(target.SourcePath);
        FileEditTransaction prepared;
        try
        {
            if (HasUnsafeRecoveryPath(sourcePath))
                return new(PhotoVersionRecipeStatus.Conflict,
                    Message: "Photo-version recovery cannot safely use a path containing a reparse point.");
            var recoveryRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(historyDirectory)));
            if (recoveryRoot is null)
                return new(PhotoVersionRecipeStatus.Conflict, Message: "The edit-history directory has no recovery root.");
            prepared = await FileEditTransaction.PrepareAsync(
                historyDirectory, [sourcePath], async copies =>
                {
                    var stagedTarget = target with { SourcePath = copies[sourcePath] };
                    var current = await ReadFamilyAsync(stagedTarget, cancellationToken);
                    status = current.Status;
                    if (status != PhotoVersionRecipeStatus.Success)
                        return;
                    if (current.Document!.Revision != expected.DocumentRevision ||
                        !string.Equals(current.Recipe, expected.RecipeXmp, StringComparison.Ordinal))
                    {
                        status = PhotoVersionRecipeStatus.Conflict;
                        return;
                    }
                    var (mutationStatus, updatedFamily) = mutate(current.Document, current.Recipe!);
                    status = mutationStatus;
                    if (status != PhotoVersionRecipeStatus.Success)
                        return;
                    status = await MetadataWriterService.WriteVersionFamilyOnStagedFileAsync(
                        stagedTarget.SourcePath, updatedFamily!, current.Value, cancellationToken);
                }, cancellationToken, durableRecovery: true, recoveryRoot: recoveryRoot);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new(PhotoVersionRecipeStatus.ReadOnly, Message: exception.Message);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 5)
        {
            return new(PhotoVersionRecipeStatus.ReadOnly, Message: exception.Message);
        }
        catch (FileNotFoundException)
        {
            return new(File.Exists(sourcePath)
                ? PhotoVersionRecipeStatus.Conflict : PhotoVersionRecipeStatus.SourceMissing);
        }
        catch (DirectoryNotFoundException)
        {
            return new(PhotoVersionRecipeStatus.SourceMissing);
        }
        catch (XmpException exception)
        {
            return new(PhotoVersionRecipeStatus.InvalidMetadata, Message: exception.Message);
        }
        catch (IOException exception) when (exception.InnerException is XmpException)
        {
            return new(PhotoVersionRecipeStatus.InvalidMetadata, Message: exception.Message);
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
        {
            return new(PhotoVersionRecipeStatus.Busy, Message: exception.Message);
        }
        var retained = false;
        var publishing = false;
        try
        {
            if (status != PhotoVersionRecipeStatus.Success)
                return new(status);
            if (!string.Equals(prepared.InputIdentity(sourcePath), expected.InputIdentity, StringComparison.Ordinal))
                return new(PhotoVersionRecipeStatus.Conflict);

            cancellationToken.ThrowIfCancellationRequested();
            publishing = true;
            var historyResult = await history.ExecuteAsync(
                description,
                () => prepared.MoveAsync(false, refresh),
                undo => prepared.MoveAsync(undo, refresh),
                prepared.Dispose);
            publishing = false;
            if (historyResult.Status != EditHistoryStatus.Applied)
                return new(historyResult.Status == EditHistoryStatus.Busy
                    ? PhotoVersionRecipeStatus.Busy : PhotoVersionRecipeStatus.Conflict,
                    Message: historyResult.Message);
            retained = true;
            if (resultTarget is null)
                return new(PhotoVersionRecipeStatus.Success, Message: historyResult.Message);
            var result = await ReadAsync(resultTarget, CancellationToken.None);
            return result.Status == PhotoVersionRecipeStatus.Success
                ? result with { Message = historyResult.Message }
                : result with
                {
                    Message = ("The version change was applied, but its latest state could not be reloaded. " +
                        historyResult.Message).TrimEnd()
                };
        }
        finally
        {
            // A publication exception may have left recovery checkpoints on disk.
            if (!retained && !publishing)
                prepared.Dispose();
        }
    }

    private bool HasUnsafeRecoveryPath(string sourcePath) =>
        FileEditRecovery.HasReparsePoint(historyDirectory) ||
        FileEditRecovery.HasReparsePoint(sourcePath) ||
        FileEditRecovery.HasReparsePoint(Path.ChangeExtension(sourcePath, ".xmp")) ||
        FileEditRecovery.HasReparsePoint(sourcePath + ".xmp");

    private static async Task<(PhotoVersionRecipeStatus Status, PhotoVersionDocument? Document,
        string? Value, string? Recipe)> ReadFamilyAsync(
        PhotoVersionTarget target, CancellationToken cancellationToken)
    {
        var (status, value) = MetadataWriterService.ReadVersionFamily(target.SourcePath, cancellationToken);
        if (status != PhotoVersionRecipeStatus.Success)
            return (status, null, null, null);
        status = PhotoVersionDocument.TryRead(value!, out var document);
        if (status != PhotoVersionRecipeStatus.Success)
            return (status, null, null, null);
        if (document!.SourceId != target.SourceId)
            return (PhotoVersionRecipeStatus.Conflict, null, null, null);
        status = document.TryGetRecipe(target.VariantId, out var recipe);
        if (status != PhotoVersionRecipeStatus.Success)
            return (status, null, null, null);
        status = PhotoVersionRecipePacket.TryRead(recipe!, isRaw: true, out _);
        if (status != PhotoVersionRecipeStatus.Success)
            return (status, null, null, null);
        var identity = await RecognitionSourceIdentity.ComputeAsync(target.SourcePath, cancellationToken);
        if (!string.Equals(document.PixelBase, identity, StringComparison.Ordinal))
            return (PhotoVersionRecipeStatus.Conflict, null, null, null);
        return (PhotoVersionRecipeStatus.Success, document, value, recipe);
    }
}
