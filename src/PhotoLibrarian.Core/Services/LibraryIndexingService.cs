// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Diagnostics;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Orchestrates folder scanning and metadata reading into a unified indexing pipeline.
/// Note: Thumbnail generation removed - we now use Windows thumbnail cache on-demand for better performance.
/// </summary>
public sealed class LibraryIndexingService
{
    private readonly CacheDatabase _db;
    private readonly ImageRepository _imageRepo;
    private readonly TagRepository _tagRepo;
    private readonly FaceRepository _faceRepo;
    private readonly FolderScannerService _scanner;
    private readonly MetadataReaderService _metadataReader;
    private readonly IFaceMetadataStore _faceMetadataStore;

    public event EventHandler<IndexingProgressEventArgs>? Progress;

    public LibraryIndexingService(
        CacheDatabase db,
        ImageRepository imageRepo,
        TagRepository tagRepo,
        FaceRepository faceRepo,
        FolderScannerService scanner,
        MetadataReaderService metadataReader,
        IFaceMetadataStore faceMetadataStore)
    {
        _db = db;
        _imageRepo = imageRepo;
        _tagRepo = tagRepo;
        _faceRepo = faceRepo;
        _scanner = scanner;
        _metadataReader = metadataReader;
        _faceMetadataStore = faceMetadataStore;
    }

    public async Task ExportPendingFaceMetadataAsync(
        CancellationToken cancellationToken = default)
    {
        var pending = await _faceRepo
            .GetImagesRequiringFaceMetadataExportAsync(cancellationToken);
        foreach (var image in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(image.FilePath))
            {
                throw new FileNotFoundException(
                    "A photo with cache-only people metadata could not be found.",
                    image.FilePath);
            }

            var metadata = await _faceRepo.GetPhotoFaceMetadataAsync(
                image.Id,
                image.Width,
                image.Height,
                cancellationToken);
            await _faceMetadataStore.WriteAsync(
                image.FilePath,
                metadata,
                cancellationToken);
            await _faceRepo.SetFaceMetadataExportRequiredAsync(
                image.Id,
                false,
                cancellationToken);
        }
    }

    /// <summary>
    /// Indexes a folder: scans files, reads metadata, generates thumbnails.
    /// </summary>
    public async Task IndexFolderAsync(
        string folderPath,
        bool includeSubfolders = true,
        CancellationToken ct = default,
        bool verifyContent = false)
    {
        int processed = 0;
        int skipped = 0;
        int errors = 0;

        DebugLog.WriteLine($"IndexFolderAsync: Starting scan of '{folderPath}' (includeSubfolders={includeSubfolders})");

        await foreach (var filePath in _scanner.ScanFolderAsync(folderPath, includeSubfolders, ct))
        {
            // Check cancellation at start of loop
            ct.ThrowIfCancellationRequested();
            
            if (processed % 10 == 0) // Log progress every 10 files
                DebugLog.WriteLine($"IndexFolderAsync: Found file '{filePath}'");
            
            try
            {
                if (!await IndexFileAsync(filePath, ct, verifyContent))
                {
                    skipped++;
                    if (processed % 10 == 0)
                        DebugLog.WriteLine($"  Skipped (already indexed and unchanged)");
                    continue;
                }

                processed++;
                if (processed % 10 == 0)
                    DebugLog.WriteLine($"  Processed successfully: {filePath}");

                if (processed % 25 == 0)
                {
                    Progress?.Invoke(this, new IndexingProgressEventArgs(processed, skipped, folderPath));
                }
            }
            catch (OperationCanceledException)
            {
                DebugLog.WriteLine($"IndexFolderAsync: Cancelled after {processed} processed");
                throw; // Re-throw to stop indexing
            }
            catch (Exception ex)
            {
                errors++;
                if (errors <= 5) // Only log first 5 errors
                    DebugLog.WriteLine($"  ERROR: {ex.Message}");
            }
        }

        DebugLog.WriteLine($"IndexFolderAsync: Complete - processed={processed}, skipped={skipped}, errors={errors}");
        Progress?.Invoke(this, new IndexingProgressEventArgs(processed, skipped, folderPath, isComplete: true));
    }

    /// <summary>Indexes a single changed file without rescanning its containing folder.</summary>
    public async Task<bool> IndexFileAsync(
        string filePath,
        CancellationToken ct = default,
        bool verifyContent = true)
    {
        ct.ThrowIfCancellationRequested();
        var existing = await _imageRepo.GetByPathAsync(filePath);
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("The photo to index could not be found.", filePath);

        if (existing?.FaceMetadataExportRequired == true)
        {
            var cachedMetadata = await _faceRepo.GetPhotoFaceMetadataAsync(
                existing.Id, existing.Width, existing.Height, ct);
            await _faceMetadataStore.WriteAsync(filePath, cachedMetadata, ct);
            fileInfo.Refresh();
        }

        var sidecarPath = FaceMetadataStore.GetSidecarPathForImage(filePath);
        var sidecar = File.Exists(sidecarPath) ? new FileInfo(sidecarPath) : null;
        var sidecarChanged = existing is not null &&
            (!string.Equals(existing.FaceSidecarPath, sidecar?.FullName, StringComparison.OrdinalIgnoreCase) ||
             existing.FaceSidecarSize != sidecar?.Length ||
             existing.FaceSidecarModified != sidecar?.LastWriteTimeUtc);
        var metadataStamp = LibraryMetadataIdentity.CaptureStamp(filePath);
        // Folder reconciliation uses stat data only as a fast-path hint. Explicit
        // refreshes, repair, and watcher notifications verify bytes even when an
        // external editor preserved the timestamp and file length.
        if (!verifyContent &&
            existing is not null &&
            existing.SourceIdentity is not null &&
            existing.FileSize == fileInfo.Length &&
            existing.DateModified == fileInfo.LastWriteTimeUtc &&
            existing.SearchMetadataIndexed &&
            existing.FaceMetadataImported &&
            !existing.FaceMetadataExportRequired &&
            existing.MetadataStamp == metadataStamp &&
            !sidecarChanged)
            return false;

        var metadataHash = await LibraryMetadataIdentity.ComputeAsync(filePath, ct);
        var sourceIdentity = existing?.SourceIdentity is not null &&
            existing.MetadataHash == metadataHash
            ? existing.SourceIdentity
            : await RecognitionSourceIdentity.ComputeAsync(filePath, ct);
        var contentChanged = existing is not null &&
            existing.SourceIdentity != sourceIdentity;
        var portableTagMetadata = _metadataReader.ReadPortableTagMetadata(filePath);
        var portableScanVersion = portableTagMetadata?.SourceIdentity == sourceIdentity
            ? portableTagMetadata.AutoTagScanVersion
            : null;
        var portableTagsChanged = false;
        if (existing is not null && portableTagMetadata is not null)
        {
            var portableTagRows = CreatePortableTagRows(
                existing.Id,
                portableTagMetadata);
            var cachedTagRows = await _tagRepo.GetTagsAsync(existing.Id, ct);
            portableTagsChanged =
                existing.AutoTagScanVersion != portableScanVersion ||
                !HaveSameTagRows(cachedTagRows, portableTagRows);
        }

        if (existing is not null &&
            existing.DateModified == fileInfo.LastWriteTimeUtc &&
            existing.FileSize == fileInfo.Length &&
            !contentChanged &&
            existing.MetadataHash == metadataHash &&
            existing.SearchMetadataIndexed &&
            existing.FaceMetadataImported &&
            !sidecarChanged &&
            !portableTagsChanged)
            return false;

        var entry = _metadataReader.ReadMetadata(filePath);
        entry.SourceIdentity = sourceIdentity;
        entry.MetadataStamp = metadataStamp;
        entry.MetadataHash = metadataHash;
        var imageId = await _imageRepo.UpsertImageAsync(entry);
        await _faceRepo.SetFaceMetadataImportedAsync(imageId, false, cancellationToken: ct);
        var faceMetadata = _faceMetadataStore.Read(filePath);
        await _faceRepo.ImportFaceMetadataAsync(imageId, faceMetadata, ct);
        await _faceRepo.SetFaceMetadataExportRequiredAsync(imageId, false, ct);

        var tags = _metadataReader.ReadTags(filePath);
        if (portableTagMetadata is not null)
        {
            await _tagRepo.ReplaceTagsFromPortableMetadataAsync(
                imageId,
                CreatePortableTagRows(imageId, portableTagMetadata),
                ct);
        }
        else
        {
            foreach (var tag in tags)
            {
                await _tagRepo.AddMetadataTagAsync(imageId, tag, ct);
            }
        }

        await _tagRepo.RestoreAutoTagScanVersionAsync(
            imageId,
            portableScanVersion,
            ct);
        if (metadataStamp != LibraryMetadataIdentity.CaptureStamp(filePath))
            throw new IOException("The photo or its metadata changed during indexing; it will be retried.");
        await _faceRepo.SetFaceMetadataImportedAsync(
            imageId, true, sidecar?.FullName, sidecar?.Length, sidecar?.LastWriteTimeUtc, ct);
        return true;
    }

    private static IReadOnlyCollection<ImageTag> CreatePortableTagRows(
        long imageId,
        PortableImageTagMetadata metadata)
    {
        var rows = new List<ImageTag>();
        foreach (var tag in metadata.Tags)
        {
            if (!Enum.TryParse<TagSource>(
                    tag.Source,
                    ignoreCase: false,
                    out var source) ||
                !Enum.IsDefined(source))
            {
                continue;
            }

            rows.Add(new ImageTag
            {
                ImageId = imageId,
                Tag = tag.Tag,
                Source = source,
                Confidence = tag.Confidence
            });
        }

        return rows;
    }

    private static bool HaveSameTagRows(
        IReadOnlyCollection<ImageTag> left,
        IReadOnlyCollection<ImageTag> right)
    {
        if (left.Count != right.Count)
            return false;

        var expected = right.ToDictionary(
            tag => tag.Tag,
            StringComparer.OrdinalIgnoreCase);
        return left.All(tag =>
            expected.TryGetValue(tag.Tag, out var expectedTag) &&
            tag.Source == expectedTag.Source &&
            tag.Confidence.Equals(expectedTag.Confidence));
    }
}

public sealed class IndexingProgressEventArgs(int processed, int skipped, string folder, bool isComplete = false) : EventArgs
{
    public int Processed { get; } = processed;
    public int Skipped { get; } = skipped;
    public string Folder { get; } = folder;
    public bool IsComplete { get; } = isComplete;
}
