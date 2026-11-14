// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;

namespace PhotoLibrarian.ViewModels;

public partial class MainViewModel
{
    private sealed record CachedEditState(ImageEntry Image, IReadOnlyCollection<ImageTag> Tags);

    private readonly string _editHistoryDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoLibrarian", "EditHistory", Guid.NewGuid().ToString("N"));

    public EditHistoryService EditHistory { get; } = new();

    /// <summary>
    /// Prepare all members of a batch on private files before publishing any of them.
    /// The cache is derived state; exact before/after cache snapshots also preserve legacy
    /// tags/flags that have not yet been migrated into portable metadata.
    /// </summary>
    public async Task<bool> RunPhotoEditAsync(string label, IReadOnlyCollection<string> paths,
        Func<IReadOnlyDictionary<string, string>, Task> prepare,
        string? expectedIdentity = null)
    {
        if (paths.Count == 0) return false;
        var originals = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        label += originals.Length == 1
            ? $" - {Path.GetFileName(originals[0])}"
            : $" ({originals.Length} photos)";
        FileEditTransaction? transaction = null;
        IReadOnlyList<CachedEditState> before = [];
        IReadOnlyList<CachedEditState> after = [];
        var resume = false;
        try
        {
            var result = await EditHistory.ExecuteAsync(label, ApplyAsync, MoveAsync, Release);
            if (result.Message is not null) StatusText = result.Message;
            return result.Status is EditHistoryStatus.Applied or EditHistoryStatus.Empty;
        }
        catch (Exception exception)
        {
            StatusText = $"{label} failed: {exception.Message}";
            Diagnostics.DebugLog.WriteLine($"[HISTORY] {StatusText}\n{exception}");
            return false;
        }
        finally
        {
            ResumeFaceDetection(resume);
        }

        async Task<EditHistoryResult> ApplyAsync()
        {
            resume = await PauseFaceDetectionAsync();
            await EnsureFaceMetadataPersistedAsync();
            if (expectedIdentity is not null &&
                await FileEditTransaction.ReadIdentityAsync(originals.Single()) != expectedIdentity)
            {
                return new(EditHistoryStatus.Conflict,
                    "The photo or its metadata changed since the editor opened. Close and reopen it before saving.");
            }
            before = await CaptureCacheAsync(originals);
            foreach (var path in originals)
                if (!RawToneMappingService.IsRaw(path))
                    await _backupService.BackupOriginalAsync(path);
            transaction = await FileEditTransaction.PrepareAsync(_editHistoryDirectory, originals, prepare);
            if (expectedIdentity is not null && transaction.InputIdentity(originals.Single()) != expectedIdentity)
            {
                transaction.Dispose();
                return new(EditHistoryStatus.Conflict,
                    "The photo or its metadata changed since the editor opened. Close and reopen it before saving.");
            }
            if (!transaction.HasChanges)
            {
                transaction.Dispose();
                return new(EditHistoryStatus.Empty);
            }
            EditHistoryResult result;
            try
            {
                result = await transaction.MoveAsync(undo: false, async () =>
                {
                    foreach (var path in originals)
                    {
                        await _indexingService.IndexFileAsync(path);
                        var image = await _imageRepo.GetByPathAsync(path);
                        if (image is not null)
                            await _imageRepo.UpdateFlagAsync(image.Id, _metadataReader.ReadMetadata(path).IsFlagged);
                    }
                    after = await CaptureCacheAsync(originals);
                    await RefreshHistorySurfacesAsync(originals);
                });
            }
            catch
            {
                await RestoreCacheAsync(before);
                throw;
            }
            if (result.Status != EditHistoryStatus.Applied)
                transaction.Dispose();
            return result;
        }

        async Task<EditHistoryResult> MoveAsync(bool undo)
        {
            var wasRunning = await PauseFaceDetectionAsync();
            try
            {
                await EnsureFaceMetadataPersistedAsync();
                return await transaction!.MoveAsync(undo, async () =>
                {
                    await RestoreCacheAsync(undo ? before : after);
                    await RefreshHistorySurfacesAsync(originals);
                });
            }
            catch
            {
                await RestoreCacheAsync(undo ? after : before);
                throw;
            }
            finally { ResumeFaceDetection(wasRunning); }
        }

        void Release() => transaction?.Dispose();
    }

    private async Task<IReadOnlyList<CachedEditState>> CaptureCacheAsync(IEnumerable<string> paths)
    {
        var states = new List<CachedEditState>();
        foreach (var path in paths)
        {
            var image = await _imageRepo.GetByPathAsync(path);
            if (image is not null)
                states.Add(new(image, await _tagRepo.GetTagsAsync(image.Id)));
        }
        return states;
    }

    private async Task RestoreCacheAsync(IReadOnlyList<CachedEditState> states)
    {
        foreach (var state in states)
        {
            var file = new FileInfo(state.Image.FilePath);
            state.Image.DateModified = file.LastWriteTimeUtc;
            state.Image.FileSize = file.Length;
            await _imageRepo.UpsertImageAsync(state.Image);
            await _imageRepo.UpdateFlagAsync(state.Image.Id, state.Image.IsFlagged);
            await _tagRepo.ReplaceTagsFromPortableMetadataAsync(state.Image.Id, state.Tags);
            await _faceRepo.ImportFaceMetadataAsync(state.Image.Id, new FaceMetadataStore().Read(state.Image.FilePath));
        }
    }

    private async Task RefreshHistorySurfacesAsync(IReadOnlyCollection<string> paths)
    {
        await ImageGrid.RefreshWatchedItemsAsync(paths);
        ImageViewer.UpdateLibraryImages(ImageGrid.GetViewerImages());
        ImageEditor.UpdateLibraryImages(ImageGrid.GetViewerImages());
        await ImageViewer.ReloadCurrentImageAsync();
        await RefreshTagsTreeAsync();
        await RefreshFlagNavAsync();
        await RefreshAfterDateChangeAsync();
        if (ImageViewer.CurrentEntry is { } current)
        {
            var fresh = await _imageRepo.GetByPathAsync(current.FilePath);
            if (fresh is not null) MetadataPanel.ShowMetadata(fresh);
        }
        else ImageGrid.RefreshMetadataFromSelection();
    }
}
