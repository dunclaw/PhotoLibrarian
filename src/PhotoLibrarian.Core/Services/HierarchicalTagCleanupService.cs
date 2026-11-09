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
    string BackupDirectory, IReadOnlyList<string> Failures);

/// <summary>
/// Explicit, user-approved removal of serialized prefix keywords. Never runs during import.
/// </summary>
public sealed class HierarchicalTagCleanupService(
    ImageRepository images,
    LibraryIndexingService indexer,
    string backupDirectory)
{
    public static IReadOnlyList<ImageTag> CollapsePrefixes(IReadOnlyList<ImageTag> tags) =>
        tags.Where(tag => !tags.Any(other =>
            other.Tag.Length > tag.Tag.Length &&
            other.Tag.StartsWith(tag.Tag + "/", StringComparison.OrdinalIgnoreCase)))
        .ToArray();

    public async Task<HierarchicalTagCleanupResult> CleanupAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = await images.GetAllAsync(
            cancellationToken: cancellationToken, watchedOnly: true);
        var failures = new List<string>();
        var updated = 0;
        var removed = 0;
        var scanned = 0;
        var runBackup = Path.Combine(backupDirectory, Guid.NewGuid().ToString("N"));
        foreach (var image in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (image.MediaType != MediaType.Image)
                continue;
            scanned++;
            try
            {
                if (image.FaceMetadataExportRequired)
                {
                    failures.Add($"{image.FilePath}: Pending metadata export; skipped.");
                    continue;
                }
                var path = image.FilePath;
                var sidecar = Path.ChangeExtension(path, ".xmp");
                if (IsReparsePoint(path) || (File.Exists(sidecar) && IsReparsePoint(sidecar)))
                {
                    failures.Add($"{path}: Reparse-point files are not modified by tag cleanup.");
                    continue;
                }
                var before = await LibraryMetadataIdentity.ComputeAsync(path, cancellationToken);
                var snapshot = TagWriterService.CaptureSidecarSnapshot(path);
                var reader = new MetadataReaderService();
                // Parse the sidecar strictly before any write; the ordinary reader tolerates failures.
                IXmpMeta? sidecarXmp = snapshot.Exists
                    ? XmpMetaFactory.ParseFromString(await File.ReadAllTextAsync(sidecar, cancellationToken))
                    : null;
                var portable = reader.ReadPortableTagMetadata(path);
                var tags = portable is not null
                    ? portable.Tags.Select(tag => new ImageTag
                    {
                        ImageId = image.Id, Tag = tag.Tag,
                        Source = Enum.Parse<TagSource>(tag.Source), Confidence = tag.Confidence
                    }).ToArray()
                    : ReadKeywords(path, sidecarXmp).Select(tag => new ImageTag
                    {
                        ImageId = image.Id, Tag = tag, Source = TagSource.Metadata
                    }).ToArray();
                var collapsed = CollapsePrefixes(tags);
                if (collapsed.Count == tags.Length)
                    continue;

                var photoBackup = Path.Combine(runBackup, Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(photoBackup);
                await CopyBackupAsync(path, Path.Combine(photoBackup, Path.GetFileName(path)), cancellationToken);
                if (snapshot.Exists)
                    await CopyBackupAsync(sidecar, Path.Combine(photoBackup, Path.GetFileName(sidecar)), cancellationToken);
                if (before != await LibraryMetadataIdentity.ComputeAsync(path, cancellationToken))
                {
                    failures.Add($"{path}: The photo or metadata changed during cleanup; skipped.");
                    continue;
                }

                // Reconcile a committed file write before honoring cancellation.
                await TagWriterService.WriteTagsToSidecarAsync(
                    path, collapsed.Select(tag => tag.Tag), collapsed,
                    portable?.AutoTagScanVersion, snapshot, portable?.SourceIdentity);
                updated++;
                removed += tags.Length - collapsed.Count;
                await indexer.IndexFileAsync(path, CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add($"{image.FilePath}: {exception.Message}");
                DebugLog.WriteLine($"Hierarchy tag cleanup failed for {image.FilePath}: {exception}");
            }
        }
        return new(scanned, updated, removed, runBackup, failures);
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
