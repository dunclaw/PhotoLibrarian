// Copyright (c) Duncan Lawler. All Rights Reserved.
using MetadataExtractor;
using MetadataExtractor.Formats.Xmp;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Diagnostics;
using PhotoLibrarian.Core.Models;
using XmpCore;

namespace PhotoLibrarian.Core.Services;

public sealed record HierarchicalTagCleanupResult(
    int ScannedPhotos, int UpdatedPhotos, int RemovedTags,
    string BackupDirectory, IReadOnlyList<string> Failures, int ReconciledPhotos,
    bool Cancelled = false, IReadOnlyList<string>? ReindexedPaths = null);

/// <summary>Running totals for a hierarchy tag cleanup; <see cref="Processed"/> counts every library entry visited.</summary>
public sealed record HierarchicalTagCleanupProgress(
    int Processed, int Total, int UpdatedPhotos, int ReconciledPhotos,
    int FailedPhotos, string CurrentPath);

/// <summary>
/// Explicit, user-approved removal of portable and legacy cached prefix tags. Never runs during import.
/// </summary>
public sealed class HierarchicalTagCleanupService(
    ImageRepository images,
    TagRepository tagRepository,
    LibraryIndexingService indexer,
    string backupDirectory)
{
    public static IReadOnlyList<ImageTag> CollapsePrefixes(IReadOnlyList<ImageTag> tags) =>
        tags.Where(tag => !tags.Any(other =>
            other.Tag.Length > tag.Tag.Length &&
            other.Tag.StartsWith(tag.Tag + "/", StringComparison.OrdinalIgnoreCase)))
        .ToArray();

    public async Task<HierarchicalTagCleanupResult> CleanupAsync(
        CancellationToken cancellationToken = default) =>
        await CleanupAsync(null, cancellationToken);

    /// <summary>
    /// Scans watched photos and collapses redundant prefixes. Cancellation is honored between photos
    /// and before any write; a photo whose metadata was already written is always reindexed first.
    /// </summary>
    public async Task<HierarchicalTagCleanupResult> CleanupAsync(
        IProgress<HierarchicalTagCleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var runBackup = Path.Combine(backupDirectory, Guid.NewGuid().ToString("N"));
        if (cancellationToken.IsCancellationRequested)
            return new(0, 0, 0, runBackup, [], 0, Cancelled: true, ReindexedPaths: []);
        IReadOnlyList<ImageEntry> allEntries;
        try
        {
            allEntries = await images.GetAllAsync(
                cancellationToken: cancellationToken, watchedOnly: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(0, 0, 0, runBackup, [], 0, Cancelled: true, ReindexedPaths: []);
        }
        var entries = allEntries.Where(image => image.MediaType == MediaType.Image).ToArray();
        var state = new CleanupState(runBackup);
        var reportTimer = System.Diagnostics.Stopwatch.StartNew();
        const long ReportIntervalMilliseconds = 250;
        var lastReport = -ReportIntervalMilliseconds;
        var cancelled = false;
        foreach (var image in entries)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            state.Scanned++;
            try
            {
                if (!await CleanupPhotoAsync(image, state, cancellationToken))
                {
                    cancelled = true;
                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            catch (Exception exception)
            {
                state.Failures.Add($"{image.FilePath}: {DescribeFailure(exception)}");
                DebugLog.WriteLine($"Hierarchy tag cleanup failed for {image.FilePath}: {exception}");
            }

            // Throttled so a quick pass over clean photos does not flood the UI thread.
            if (progress is not null &&
                (state.Scanned == entries.Length || reportTimer.ElapsedMilliseconds - lastReport >= ReportIntervalMilliseconds))
            {
                lastReport = reportTimer.ElapsedMilliseconds;
                progress.Report(new(state.Scanned, entries.Length, state.Updated,
                    state.Reconciled, state.Failures.Count, image.FilePath));
            }
        }
        progress?.Report(new(state.Scanned, entries.Length, state.Updated,
            state.Reconciled, state.Failures.Count, ""));
        return new(state.Scanned, state.Updated, state.Removed, state.RunBackup,
            state.Failures, state.Reconciled, cancelled, state.ReindexedPaths);
    }

    /// <summary>Returns false only when cancellation was observed before this photo was written.</summary>
    private async Task<bool> CleanupPhotoAsync(
        ImageEntry image, CleanupState state, CancellationToken cancellationToken)
    {
        if (image.FaceMetadataExportRequired)
        {
            state.Failures.Add($"{image.FilePath}: Pending metadata export; skipped.");
            return true;
        }
        var path = image.FilePath;
        var sidecar = TagWriterService.GetTagSidecarTargetPath(path);
        if (IsReparsePoint(path) || (File.Exists(sidecar) && IsReparsePoint(sidecar)))
        {
            state.Failures.Add($"{path}: Reparse-point files are not modified by tag cleanup.");
            return true;
        }
        // A cheap stamp guards the read; full content hashing is reserved for photos that need changes.
        var stamp = LibraryMetadataIdentity.CaptureStamp(path);
        var snapshot = TagWriterService.CaptureSidecarSnapshot(path);
        var reader = new MetadataReaderService();
        // Parse the sidecar strictly before any write; the ordinary reader tolerates failures.
        IXmpMeta? sidecarXmp = snapshot.Exists
            ? XmpMetaFactory.ParseFromString(await File.ReadAllTextAsync(sidecar, cancellationToken))
            : null;
        var portable = reader.ReadPortableTagMetadata(path);
        var cachedTags = await tagRepository.GetTagsAsync(image.Id, cancellationToken);
        // Portable provenance wins; older builds may have left the only tag copy in SQLite.
        var tags = portable is not null
            ? portable.Tags.Select(tag => new ImageTag
            {
                ImageId = image.Id, Tag = tag.Tag,
                Source = Enum.Parse<TagSource>(tag.Source), Confidence = tag.Confidence
            }).ToArray()
            : MergeLegacyTags(image.Id, cachedTags, ReadKeywords(path, sidecarXmp));
        var collapsed = CollapsePrefixes(tags);
        var needsFileWrite = collapsed.Count != tags.Length;
        var needsCacheReconciliation =
            (portable is not null || needsFileWrite) && !HaveSameTags(cachedTags, collapsed);
        if (!needsFileWrite && !needsCacheReconciliation)
            return true;

        var before = await LibraryMetadataIdentity.ComputeAsync(path, cancellationToken);
        if (stamp != LibraryMetadataIdentity.CaptureStamp(path))
        {
            state.Failures.Add($"{path}: The photo or metadata changed during cleanup; skipped.");
            return true;
        }
        var photoBackup = Path.Combine(state.RunBackup, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(photoBackup);
        await CopyBackupAsync(path, Path.Combine(photoBackup, Path.GetFileName(path)), cancellationToken);
        if (snapshot.Exists)
            await CopyBackupAsync(sidecar, Path.Combine(photoBackup, Path.GetFileName(sidecar)), cancellationToken);
        // Cache-only recognition from older builds has no recovery copy in the photo.
        await File.WriteAllTextAsync(
            Path.Combine(photoBackup, "tag-cache.json"),
            PortableImageTagMetadataSerializer.Serialize(
                cachedTags, image.AutoTagScanVersion, image.SourceIdentity),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(photoBackup, "original-path.txt"), path, cancellationToken);
        if (before != await LibraryMetadataIdentity.ComputeAsync(path, cancellationToken))
        {
            state.Failures.Add($"{path}: The photo or metadata changed during cleanup; skipped.");
            return true;
        }
        if (!HaveSameTags(cachedTags,
            await tagRepository.GetTagsAsync(image.Id, cancellationToken)))
        {
            state.Failures.Add($"{path}: Cached tags changed during cleanup; skipped.");
            return true;
        }

        // Last cancellation point: once metadata is written it is always reindexed.
        if (cancellationToken.IsCancellationRequested)
            return false;
        if (needsFileWrite)
        {
            await TagWriterService.WriteTagsToSidecarAsync(
                path, collapsed.Select(tag => tag.Tag), collapsed,
                portable is not null ? portable.AutoTagScanVersion : image.AutoTagScanVersion,
                snapshot, portable is not null ? portable.SourceIdentity : image.SourceIdentity);
            state.Updated++;
        }
        await indexer.IndexFileAsync(path, CancellationToken.None);
        state.ReindexedPaths.Add(path);
        if (!HaveSameTags(collapsed,
            await tagRepository.GetTagsAsync(image.Id, CancellationToken.None)))
        {
            state.Failures.Add($"{path}: The cleaned tag set was not retained in the index; refresh or retry cleanup.");
            return true;
        }
        if (needsCacheReconciliation)
            state.Reconciled++;
        var previous = cachedTags.Concat(tags)
            .GroupBy(tag => tag.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
        state.Removed += previous.Count(tag =>
            collapsed.Any(leaf => leaf.Tag.StartsWith(tag.Tag + "/", StringComparison.OrdinalIgnoreCase)) &&
            !collapsed.Any(leaf => string.Equals(leaf.Tag, tag.Tag, StringComparison.OrdinalIgnoreCase)));
        return true;
    }

    /// <summary>Some Windows imaging failures carry no message; always include the error type and code.</summary>
    public static string DescribeFailure(Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? "No error description was provided by Windows."
            : exception.Message;
        return $"{message} ({exception.GetType().Name} 0x{exception.HResult:X8})";
    }

    private sealed class CleanupState(string runBackup)
    {
        public string RunBackup { get; } = runBackup;
        public List<string> Failures { get; } = [];
        public List<string> ReindexedPaths { get; } = [];
        public int Scanned { get; set; }
        public int Updated { get; set; }
        public int Reconciled { get; set; }
        public int Removed { get; set; }
    }

    private static ImageTag[] MergeLegacyTags(
        long imageId, IReadOnlyList<ImageTag> cachedTags, IEnumerable<string> keywords) =>
        cachedTags.Concat(keywords.Select(tag => new ImageTag
        {
            ImageId = imageId, Tag = tag, Source = TagSource.Metadata
        }))
        .GroupBy(tag => tag.Tag, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First()).ToArray();

    private static bool HaveSameTags(IReadOnlyList<ImageTag> left, IReadOnlyList<ImageTag> right)
    {
        if (left.Count != right.Count) return false;
        return left.All(tag => right.Any(other =>
            string.Equals(tag.Tag, other.Tag, StringComparison.OrdinalIgnoreCase) &&
            tag.Source == other.Source && tag.Confidence.Equals(other.Confidence)));
    }

    private static IEnumerable<string> ReadKeywords(string path, IXmpMeta? sidecar)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (EmbeddedMetadataWriter.IsSupported(path))
        {
            var embedded = ImageMetadataReader.ReadMetadata(path)
                .OfType<XmpDirectory>().FirstOrDefault()?.XmpMeta;
            AddKeywords(embedded, keywords);
        }
        AddKeywords(sidecar, keywords);
        return keywords;
    }

    private static void AddKeywords(IXmpMeta? xmp, HashSet<string> keywords)
    {
        if (xmp is null) return;
        const string dc = "http://purl.org/dc/elements/1.1/";
        var count = xmp.CountArrayItems(dc, "dc:subject");
        for (var index = 1; index <= count; index++)
        {
            var value = xmp.GetArrayItem(dc, "dc:subject", index)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
                keywords.Add(value);
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static async Task CopyBackupAsync(string source, string destination, CancellationToken token)
    {
        await using var input = File.OpenRead(source);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
        await input.CopyToAsync(output, token);
    }
}
