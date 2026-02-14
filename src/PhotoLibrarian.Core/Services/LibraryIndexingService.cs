using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Diagnostics;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Orchestrates folder scanning, metadata reading, and thumbnail generation
/// into a unified indexing pipeline.
/// </summary>
public sealed class LibraryIndexingService
{
    private readonly CacheDatabase _db;
    private readonly ImageRepository _imageRepo;
    private readonly ThumbnailRepository _thumbRepo;
    private readonly FolderScannerService _scanner;
    private readonly MetadataReaderService _metadataReader;
    private readonly ThumbnailService _thumbnailService;

    public event EventHandler<IndexingProgressEventArgs>? Progress;

    public LibraryIndexingService(
        CacheDatabase db,
        ImageRepository imageRepo,
        ThumbnailRepository thumbRepo,
        FolderScannerService scanner,
        MetadataReaderService metadataReader,
        ThumbnailService thumbnailService)
    {
        _db = db;
        _imageRepo = imageRepo;
        _thumbRepo = thumbRepo;
        _scanner = scanner;
        _metadataReader = metadataReader;
        _thumbnailService = thumbnailService;
    }

    /// <summary>
    /// Indexes a folder: scans files, reads metadata, generates thumbnails.
    /// </summary>
    public async Task IndexFolderAsync(string folderPath, bool includeSubfolders = true, CancellationToken ct = default)
    {
        int processed = 0;
        int skipped = 0;
        int errors = 0;

        DebugLog.WriteLine($"IndexFolderAsync: Starting scan of '{folderPath}' (includeSubfolders={includeSubfolders})");

        await foreach (var filePath in _scanner.ScanFolderAsync(folderPath, includeSubfolders, ct))
        {
            DebugLog.WriteLine($"IndexFolderAsync: Found file '{filePath}'");
            
            try
            {
                // Check if already indexed and unchanged
                var existing = await _imageRepo.GetByPathAsync(filePath);
                var fileInfo = new FileInfo(filePath);

                if (existing is not null && existing.DateModified >= fileInfo.LastWriteTimeUtc)
                {
                    skipped++;
                    DebugLog.WriteLine($"  Skipped (already indexed and unchanged)");
                    continue;
                }

                // Read metadata
                var entry = _metadataReader.ReadMetadata(filePath);
                var imageId = await _imageRepo.UpsertImageAsync(entry);

                // Generate thumbnail (small size for grid)
                await _thumbnailService.GetOrCreateThumbnailAsync(imageId, filePath, ThumbnailSize.Small);

                processed++;
                DebugLog.WriteLine($"  Processed successfully (id={imageId})");

                if (processed % 25 == 0)
                {
                    Progress?.Invoke(this, new IndexingProgressEventArgs(processed, skipped, folderPath));
                }
            }
            catch (Exception ex)
            {
                errors++;
                DebugLog.WriteLine($"  ERROR: {ex.Message}");
            }
        }

        DebugLog.WriteLine($"IndexFolderAsync: Complete - processed={processed}, skipped={skipped}, errors={errors}");
        Progress?.Invoke(this, new IndexingProgressEventArgs(processed, skipped, folderPath, isComplete: true));
    }
}

public sealed class IndexingProgressEventArgs(int processed, int skipped, string folder, bool isComplete = false) : EventArgs
{
    public int Processed { get; } = processed;
    public int Skipped { get; } = skipped;
    public string Folder { get; } = folder;
    public bool IsComplete { get; } = isComplete;
}
