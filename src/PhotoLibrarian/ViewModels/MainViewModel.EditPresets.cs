// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using System.Globalization;

namespace PhotoLibrarian.ViewModels;

public sealed record PhotoEditClipboard(EditParameters Parameters, EditSettingGroup Groups, string? SourceIdentity);

public partial class MainViewModel
{
    public PhotoPresetStore PresetStore { get; } = new();
    public PhotoEditClipboard? EditClipboard { get; private set; }

    public async Task<PhotoPreset?> CreatePresetAsync(
        string name,
        string sourcePath,
        EditParameters parameters,
        EditSettingGroup groups,
        string? expectedFileIdentity = null,
        string? sourceIdentity = null)
    {
        if (expectedFileIdentity is not null &&
            await FileEditTransaction.ReadIdentityAsync(sourcePath) != expectedFileIdentity)
        {
            StatusText = PhotoContextMenuStrings.Get("EditStatusSourceChanged");
            return null;
        }
        var sourceId = groups.HasFlag(EditSettingGroup.Retouch)
            ? sourceIdentity ?? await RecognitionSourceIdentity.ComputeAsync(sourcePath)
            : null;
        if (expectedFileIdentity is not null &&
            await FileEditTransaction.ReadIdentityAsync(sourcePath) != expectedFileIdentity)
        {
            StatusText = PhotoContextMenuStrings.Get("EditStatusSourceChanged");
            return null;
        }
        return new PhotoPreset(
            Guid.NewGuid().ToString("N"),
            name.Trim(),
            groups,
            EditRecipeComposer.SelectGroups(parameters, groups),
            sourceId,
            false);
    }

    public async Task CopyEditGroupsAsync(ImageEntry source, EditSettingGroup groups)
    {
        if (source.MediaType != MediaType.Image)
        {
            StatusText = PhotoContextMenuStrings.Get("EditStatusCopyUnsupported");
            return;
        }
        var sourceVersion = await FileEditTransaction.ReadIdentityAsync(source.FilePath);
        var parameters = ImageEditService.ReadEditParameters(source.FilePath);
        var identity = groups.HasFlag(EditSettingGroup.Retouch)
            ? await RecognitionSourceIdentity.ComputeAsync(source.FilePath)
            : null;
        if (await FileEditTransaction.ReadIdentityAsync(source.FilePath) != sourceVersion)
        {
            StatusText = PhotoContextMenuStrings.Get("EditStatusSourceChanged");
            return;
        }
        EditClipboard = new PhotoEditClipboard(
            EditRecipeComposer.SelectGroups(parameters, groups), groups, identity);
        StatusText = string.Format(CultureInfo.CurrentCulture,
            PhotoContextMenuStrings.Get("EditStatusCopied"), DescribeGroups(groups), source.FileName);
    }

    public Task<bool> ApplyPresetAsync(
        PhotoPreset preset,
        IReadOnlyCollection<ImageEntry> targets,
        int amountPercent,
        EditSettingGroup? groups = null) =>
        ApplyEditGroupsAsync(
            preset.Parameters,
            preset.SourceIdentity,
            groups ?? preset.Groups,
            targets,
            amountPercent,
            $"apply preset {preset.Name}");

    public async Task<bool> ApplyComposedRecipeAsync(
        ImageEntry target,
        EditParameters recipe,
        string expectedIdentity,
        string label)
    {
        if (target.MediaType != MediaType.Image)
        {
            StatusText = PhotoContextMenuStrings.Get("EditStatusCopyUnsupported");
            return false;
        }

        var current = ImageEditService.ReadEditParameters(target.FilePath);
        if (EditRecipeComposer.ContentEquals(current, recipe))
        {
            StatusText = PhotoContextMenuStrings.Get("EditStatusNoChanges");
            return false;
        }

        var path = Path.GetFullPath(target.FilePath);
        var expectedIdentities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [path] = expectedIdentity
        };
        var applied = await RunPhotoEditAsync(label, [path], async copies =>
        {
            await ImageEditService.WritePendingRecipeAsync(copies[path], recipe);
        }, expectedIdentities: expectedIdentities);
        if (applied)
            StatusText = string.Format(CultureInfo.CurrentCulture,
                PhotoContextMenuStrings.Get("EditStatusApplied"), 1);
        return applied;
    }

    public Task<bool> PasteEditGroupsAsync(
        IReadOnlyCollection<ImageEntry> targets,
        EditSettingGroup groups)
    {
        if (EditClipboard is not { } clipboard)
        {
            StatusText = PhotoContextMenuStrings.Get("EditStatusPasteMissing");
            return Task.FromResult(false);
        }
        return ApplyEditGroupsAsync(
            clipboard.Parameters,
            clipboard.SourceIdentity,
            groups,
            targets,
            100,
            "paste edits");
    }

    public async Task<bool> SyncEditGroupsAsync(
        ImageEntry source,
        IReadOnlyCollection<ImageEntry> targets,
        EditSettingGroup groups)
    {
        if (source.MediaType != MediaType.Image)
        {
            StatusText = PhotoContextMenuStrings.Get("EditStatusSyncUnsupported");
            return false;
        }

        var sourceVersion = await FileEditTransaction.ReadIdentityAsync(source.FilePath);
        var parameters = ImageEditService.ReadEditParameters(source.FilePath);
        var identity = groups.HasFlag(EditSettingGroup.Retouch)
            ? await RecognitionSourceIdentity.ComputeAsync(source.FilePath)
            : null;
        if (await FileEditTransaction.ReadIdentityAsync(source.FilePath) != sourceVersion)
        {
            StatusText = PhotoContextMenuStrings.Get("EditStatusSourceChanged");
            return false;
        }
        var destinations = targets
            .Where(item => !string.Equals(item.FilePath, source.FilePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return await ApplyEditGroupsAsync(
            parameters, identity, groups, destinations, 100, "sync edits",
            sourceSnapshot: (source.FilePath, sourceVersion));
    }

    private async Task<bool> ApplyEditGroupsAsync(
        EditParameters source,
        string? sourceIdentity,
        EditSettingGroup groups,
        IReadOnlyCollection<ImageEntry> targets,
        int amountPercent,
        string label,
        (string Path, string Identity)? sourceSnapshot = null)
    {
        var candidates = new List<(ImageEntry Entry, EditParameters Parameters, string Identity)>();
        var details = new List<string>();
        foreach (var target in targets
            .Where(item => item.MediaType == MediaType.Image)
            .DistinctBy(item => Path.GetFullPath(item.FilePath), StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var identity = await FileEditTransaction.ReadIdentityAsync(target.FilePath);
                var targetIdentity = groups.HasFlag(EditSettingGroup.Retouch)
                    ? await RecognitionSourceIdentity.ComputeAsync(target.FilePath)
                    : null;
                var current = ImageEditService.ReadEditParameters(target.FilePath);
                var composed = EditRecipeComposer.Compose(
                    current,
                    source,
                    groups,
                    amountPercent,
                    RawToneMappingService.IsRaw(target.FilePath),
                    sourceIdentity is not null && sourceIdentity == targetIdentity);
                if (composed.SkippedGroups.Count > 0)
                    details.Add(string.Format(CultureInfo.CurrentCulture,
                        PhotoContextMenuStrings.Get("EditStatusSkipped"),
                        target.FileName, DescribeGroups(composed.SkippedGroups)));
                if (EditRecipeComposer.ContentEquals(current, composed.Parameters))
                    continue;

                candidates.Add((target, composed.Parameters, identity));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                details.Add($"{target.FileName}: {exception.Message}");
            }
        }

        if (candidates.Count == 0)
        {
            StatusText = details.Count == 0
                ? PhotoContextMenuStrings.Get("EditStatusNoChanges")
                : string.Join(Environment.NewLine, details);
            return false;
        }

        if (sourceSnapshot is { } sourceVersion &&
            await FileEditTransaction.ReadIdentityAsync(sourceVersion.Path) != sourceVersion.Identity)
        {
            StatusText = PhotoContextMenuStrings.Get("EditStatusSourceChanged");
            return false;
        }

        var applied = false;
        while (candidates.Count > 0)
        {
            var stale = new List<(ImageEntry Entry, EditParameters Parameters, string Identity)>();
            foreach (var candidate in candidates)
            {
                try
                {
                    if (!File.Exists(candidate.Entry.FilePath) ||
                        await FileEditTransaction.ReadIdentityAsync(candidate.Entry.FilePath) != candidate.Identity)
                        stale.Add(candidate);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    stale.Add(candidate);
                    details.Add($"{candidate.Entry.FileName}: {exception.Message}");
                }
            }

            foreach (var candidate in stale)
            {
                candidates.Remove(candidate);
                if (!details.Any(detail => detail.StartsWith(
                    $"{candidate.Entry.FileName}:", StringComparison.Ordinal)))
                {
                    details.Add($"{candidate.Entry.FileName}: " +
                        PhotoContextMenuStrings.Get("EditStatusSourceChanged"));
                }
            }
            if (candidates.Count == 0) break;

            var paths = candidates.Select(candidate => candidate.Entry.FilePath).ToArray();
            var expectedIdentities = candidates.ToDictionary(
                candidate => Path.GetFullPath(candidate.Entry.FilePath),
                candidate => candidate.Identity,
                StringComparer.OrdinalIgnoreCase);
            applied = await RunPhotoEditAsync(label, paths, async copies =>
            {
                foreach (var candidate in candidates)
                {
                    var targetPath = Path.GetFullPath(candidate.Entry.FilePath);
                    await ImageEditService.WritePendingRecipeAsync(copies[targetPath], candidate.Parameters);
                }
            }, expectedIdentities: expectedIdentities);
            if (applied) break;

            var failure = StatusText;
            var changedDuringPreparation = new List<(ImageEntry Entry, EditParameters Parameters, string Identity)>();
            foreach (var candidate in candidates)
            {
                try
                {
                    if (!File.Exists(candidate.Entry.FilePath) ||
                        await FileEditTransaction.ReadIdentityAsync(candidate.Entry.FilePath) != candidate.Identity)
                        changedDuringPreparation.Add(candidate);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    changedDuringPreparation.Add(candidate);
                    details.Add($"{candidate.Entry.FileName}: {exception.Message}");
                }
            }
            if (changedDuringPreparation.Count == 0)
            {
                details.Add(string.Format(CultureInfo.CurrentCulture,
                    PhotoContextMenuStrings.Get("EditStatusApplyFailed"), failure));
                break;
            }

            foreach (var candidate in changedDuringPreparation)
            {
                candidates.Remove(candidate);
                if (!details.Any(detail => detail.StartsWith(
                    $"{candidate.Entry.FileName}:", StringComparison.Ordinal)))
                {
                    details.Add($"{candidate.Entry.FileName}: " +
                        PhotoContextMenuStrings.Get("EditStatusSourceChanged"));
                }
            }
        }

        if (!applied && candidates.Count == 0 && details.Count == 0)
        {
            StatusText = PhotoContextMenuStrings.Get("EditStatusNoChanges");
            return false;
        }
        if (!applied && candidates.Count == 0 && details.Count > 0)
        {
            StatusText = string.Join(Environment.NewLine, details);
            return false;
        }
        if (applied)
        {
            details.Insert(0, string.Format(CultureInfo.CurrentCulture,
                PhotoContextMenuStrings.Get("EditStatusApplied"), candidates.Count));
        }
        StatusText = string.Join(Environment.NewLine, details);
        return applied;
    }

    private static string DescribeGroups(EditSettingGroup groups) =>
        DescribeGroups(Enum.GetValues<EditSettingGroup>()
            .Where(group => group is not (EditSettingGroup.None or EditSettingGroup.All) &&
                groups.HasFlag(group))
            .ToArray());

    private static string DescribeGroups(IEnumerable<EditSettingGroup> groups) =>
        string.Join(", ", groups.Select(group => PhotoContextMenuStrings.Get($"EditGroup{group}")));
}
