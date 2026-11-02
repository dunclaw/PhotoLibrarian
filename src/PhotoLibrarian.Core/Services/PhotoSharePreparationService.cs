// Copyright (c) Duncan Lawler. All Rights Reserved.

using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

/// <summary>Owns only generated JPEG copies, never the selected source files.</summary>
public sealed class PreparedPhotoShare : IDisposable
{
    private readonly FileStream _lease;
    private bool _retain;
    private bool _disposed;

    internal PreparedPhotoShare(string folder, FileStream lease, ImageExportResult result)
    {
        Folder = folder;
        _lease = lease;
        Result = result;
    }

    public string Folder { get; }
    public ImageExportResult Result { get; }

    // Targets may read lazily, after the share sheet or mail draft has closed.
    public void RetainForHandoff()
    {
        _retain = true;
        File.SetLastWriteTimeUtc(_lease.SafeFileHandle, DateTime.UtcNow);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lease.Dispose();
        if (!_retain)
            PhotoSharePreparationService.DeleteBatch(Folder);
    }
}

public sealed class PhotoSharePreparationService
{
    public const int MaximumPhotos = 20;
    public const long MaximumBytes = 20 * 1024 * 1024;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoLibrarian", "ShareCopies");

    private readonly string _root;

    public PhotoSharePreparationService(string? root = null) =>
        _root = Path.GetFullPath(root ?? DefaultRoot);

    public async Task<PreparedPhotoShare> PrepareAsync(
        IReadOnlyList<string> sourcePaths,
        IProgress<ImageExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count is < 1 or > MaximumPhotos)
            throw new ArgumentOutOfRangeException(nameof(sourcePaths), $"Select 1 to {MaximumPhotos} photos.");

        // Snapshot, deduplicate and preserve selection order before the first await.
        var paths = sourcePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        CleanupExpired();
        Directory.CreateDirectory(_root);
        var folder = Path.Combine(_root, $"share-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        FileStream? lease = null;
        try
        {
            lease = new FileStream(Path.Combine(folder, ".lease"), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None);
            var result = await ImageExportService.ExportAsync(paths, folder,
                new ImageExportOptions { Size = 1600, Quality = 85 }, progress, cancellationToken);
            var bytes = result.ExportedPaths.Sum(path => new FileInfo(path).Length);
            if (!result.IsCancelled && !cancellationToken.IsCancellationRequested)
                ValidatePayloadSize(bytes);
            return new PreparedPhotoShare(folder, lease, result);
        }
        catch
        {
            lease?.Dispose();
            DeleteBatch(folder);
            throw;
        }
    }

    internal static void ValidatePayloadSize(long bytes)
    {
        if (bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes > MaximumBytes)
            throw new IOException("Resized copies exceed the 20 MiB sharing limit. Select fewer photos.");
    }

    public void CleanupExpired()
    {
        if (!Directory.Exists(_root)) return;
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(_root, "share-*"))
            {
                var name = Path.GetFileName(folder);
                if (!Guid.TryParseExact(name["share-".Length..], "N", out _) ||
                    File.GetLastWriteTimeUtc(Path.Combine(folder, ".lease")) > DateTime.UtcNow - Retention ||
                    (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    continue;

                try
                {
                    // A preparation in another window/process must not be collected.
                    using (new FileStream(Path.Combine(folder, ".lease"), FileMode.Open,
                        FileAccess.ReadWrite, FileShare.None))
                    {
                    }
                    DeleteBatch(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Diagnostics.DebugLog.WriteLine($"[SHARE] Cleanup deferred for {folder}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.DebugLog.WriteLine($"[SHARE] Could not clean temporary copies: {ex}");
        }
    }

    internal static void DeleteBatch(string folder)
    {
        try
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 ||
                Directory.EnumerateDirectories(folder).Any())
                throw new IOException("Unexpected directory in a share batch; refusing cleanup.");
            var files = Directory.GetFiles(folder);
            if (files.Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                !IsGeneratedFile(path)))
                throw new IOException("Unexpected file in a share batch; refusing cleanup.");
            foreach (var file in files)
            {
                if (Path.GetFileName(file) != ".lease")
                    File.Delete(file);
            }
            // Keep the ownership marker and its expiry if a target still has a JPEG
            // locked, so the next cleanup can retry rather than orphaning the batch.
            File.Delete(Path.Combine(folder, ".lease"));
            Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.DebugLog.WriteLine($"[SHARE] Could not remove temporary copies in {folder}: {ex}");
        }
    }

    private static bool IsGeneratedFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name == ".lease" || Path.GetExtension(path) == ".jpg") return true;
        const string prefix = ".photolibrarian-export-";
        return name.StartsWith(prefix, StringComparison.Ordinal) &&
            name.EndsWith(".tmp", StringComparison.Ordinal) &&
            Guid.TryParseExact(name[prefix.Length..^4], "N", out _);
    }
}
