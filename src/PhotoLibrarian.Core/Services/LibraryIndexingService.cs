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
    private readonly FolderScannerService _scanner;
    private readonly MetadataReaderService _metadataReader;

    public event EventHandler<IndexingProgressEventArgs>? Progress;

    public LibraryIndexingService(
        CacheDatabase db,
        ImageRepository imageRepo,
        FolderScannerService scanner,
        MetadataReaderService metadataReader)
    {
        _db = db;
        _imageRepo = imageRepo;
        _scanner = scanner;
        _metadataReader = metadataReader;
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
            // Check cancellation at start of loop
            ct.ThrowIfCancellationRequested();
            
            if (processed % 10 == 0) // Log progress every 10 files
                DebugLog.WriteLine($"IndexFolderAsync: Found file '{filePath}'");
            
            try
            {
                // Check if already indexed and unchanged
                var existing = await _imageRepo.GetByPathAsync(filePath);
                var fileInfo = new FileInfo(filePath);

                if (existing is not null && existing.DateModified >= fileInfo.LastWriteTimeUtc)
                {
                    skipped++;
                    if (processed % 10 == 0)
                        DebugLog.WriteLine($"  Skipped (already indexed and unchanged)");
                    continue;
                }

                // Read and store metadata only
                // Thumbnails are generated on-demand using Windows thumbnail cache (instant for cached images)
                var entry = _metadataReader.ReadMetadata(filePath);
                var imageId = await _imageRepo.UpsertImageAsync(entry);

                processed++;
                if (processed % 10 == 0)
                    DebugLog.WriteLine($"  Processed successfully (id={imageId})");

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
}

public sealed class IndexingProgressEventArgs(int processed, int skipped, string folder, bool isComplete = false) : EventArgs
{
    public int Processed { get; } = processed;
    public int Skipped { get; } = skipped;
    public string Folder { get; } = folder;
    public bool IsComplete { get; } = isComplete;
}
