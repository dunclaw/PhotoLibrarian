// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Diagnostics;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

/// <summary>Coalesces filesystem notifications and updates only the affected library records.</summary>
public sealed class WatchedFolderChangeService : IDisposable
{
    private readonly FolderScannerService _scanner;
    private readonly LibraryIndexingService _indexer;
    private readonly ImageRepository _images;
    private readonly TimeSpan _quietPeriod;
    private readonly object _sync = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Timer _timer;
    private bool _processing;
    private bool _disposed;
    private int _suspendCount;

    public event EventHandler<WatchedLibraryChangedEventArgs>? LibraryChanged;
    public event EventHandler<Exception>? SyncFailed;

    public WatchedFolderChangeService(
        FolderScannerService scanner,
        LibraryIndexingService indexer,
        ImageRepository images,
        TimeSpan? quietPeriod = null)
    {
        _scanner = scanner;
        _indexer = indexer;
        _images = images;
        _quietPeriod = quietPeriod ?? TimeSpan.FromSeconds(1.5);
        _timer = new Timer(_ => _ = ProcessPendingAsync(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _scanner.FileChanged += OnFileChanged;
        _scanner.DirectoryChanged += OnDirectoryChanged;
    }

    private void OnFileChanged(object? sender, FileChangedEventArgs e)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _pending.Add(e.FilePath);
            _timer.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDirectoryChanged(object? sender, FileChangedEventArgs e)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _pendingDirectories.Add(e.FilePath);
            _timer.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Holds notifications without indexing them until the returned scope is disposed, so a bulk
    /// metadata operation is not slowed by, or blocked on, reindexing its own writes.
    /// </summary>
    public IDisposable SuspendProcessing()
    {
        lock (_sync)
        {
            _suspendCount++;
        }
        return new ProcessingSuspension(this);
    }

    /// <summary>
    /// Drops held notifications for photos the caller already reindexed, provided each photo and
    /// its sidecars still match the indexed metadata stamp. Anything else is still processed.
    /// </summary>
    public async Task DiscardReindexedAsync(IReadOnlyCollection<string> reindexedImagePaths)
    {
        var reindexed = new HashSet<string>(reindexedImagePaths, StringComparer.OrdinalIgnoreCase);
        List<(string Path, string[] Images)> candidates = [];
        lock (_sync)
        {
            foreach (var path in _pending)
            {
                var resolved = ResolveImagePaths(path).ToArray();
                if (resolved.Length > 0 && resolved.All(reindexed.Contains))
                    candidates.Add((path, resolved));
            }
            foreach (var candidate in candidates)
                _pending.Remove(candidate.Path);
        }

        // Removal happens first so a notification arriving during verification is never lost.
        var restore = new List<string>();
        foreach (var candidate in candidates)
        {
            foreach (var imagePath in candidate.Images)
            {
                var indexed = await _images.GetByPathAsync(imagePath);
                if (indexed?.MetadataStamp is null ||
                    !File.Exists(imagePath) ||
                    indexed.MetadataStamp != LibraryMetadataIdentity.CaptureStamp(imagePath))
                {
                    restore.Add(candidate.Path);
                    break;
                }
            }
        }

        if (restore.Count == 0) return;
        lock (_sync)
        {
            if (_disposed) return;
            foreach (var path in restore)
                _pending.Add(path);
        }
    }

    private void ResumeProcessing()
    {
        lock (_sync)
        {
            _suspendCount--;
            if (_suspendCount == 0 && !_disposed &&
                (_pending.Count > 0 || _pendingDirectories.Count > 0))
                _timer.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class ProcessingSuspension(WatchedFolderChangeService owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.ResumeProcessing();
        }
    }

    private async Task ProcessPendingAsync()
    {
        lock (_sync)
        {
            if (_disposed || _processing || _suspendCount > 0) return;
            _processing = true;
        }

        try
        {
            string[] paths;
            string[] directories;
            lock (_sync)
            {
                if (_disposed) return;
                paths = _pending.ToArray();
                _pending.Clear();
                directories = _pendingDirectories.ToArray();
                _pendingDirectories.Clear();
            }

            var changed = false;
            var requiresGridReload = false;
            var changedImagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<ImageEntry>? indexedImages = null;
            foreach (var directory in directories)
            {
                try
                {
                    if (Directory.Exists(directory))
                    {
                        var directoryChanged = await _indexer.IndexFolderAsync(
                            directory,
                            true,
                            _shutdown.Token,
                            verifyContent: true);
                        changed |= directoryChanged;
                        requiresGridReload |= directoryChanged;
                    }
                    else
                    {
                        indexedImages ??= await _images.GetAllAsync(
                            cancellationToken: _shutdown.Token);
                        var prefix = Path.TrimEndingDirectorySeparator(directory);
                        if (!Path.EndsInDirectorySeparator(prefix))
                            prefix += Path.DirectorySeparatorChar;
                        if (indexedImages.Any(image =>
                            image.FilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        {
                            changed = true;
                            requiresGridReload = true;
                        }
                    }
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    DebugLog.WriteLine($"Watched directory sync failed for '{directory}': {ex}");
                    SyncFailed?.Invoke(this, ex);
                }
            }

            foreach (var path in paths)
            {
                foreach (var imagePath in ResolveImagePaths(path))
                {
                    try
                    {
                        var wasIndexed = await _images.GetByPathAsync(imagePath) is not null;
                        var fileChanged = await ProcessFileAsync(imagePath, _shutdown.Token);
                        var isIndexed = File.Exists(imagePath) &&
                                        await _images.GetByPathAsync(imagePath) is not null;
                        if (wasIndexed != isIndexed)
                            requiresGridReload = true;
                        if (fileChanged)
                        {
                            changed = true;
                            changedImagePaths.Add(imagePath);
                        }
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        DebugLog.WriteLine($"Watched file sync failed for '{imagePath}': {ex}");
                        SyncFailed?.Invoke(this, ex);
                    }
                }
            }

            if (changed && !_disposed)
                LibraryChanged?.Invoke(this, new WatchedLibraryChangedEventArgs(
                    requiresGridReload,
                    changedImagePaths.ToArray()));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            DebugLog.WriteLine($"Watched folder sync failed: {ex}");
            SyncFailed?.Invoke(this, ex);
        }
        finally
        {
            lock (_sync)
            {
                _processing = false;
                if (!_disposed && (_pending.Count > 0 || _pendingDirectories.Count > 0))
                    _timer.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
            }
        }

    }

    private async Task<bool> ProcessFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            return await _images.GetByPathAsync(path) is not null;
        }

        // A large Explorer copy may still be in progress when its first notification arrives.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                }
                return await _indexer.IndexFileAsync(path, cancellationToken);
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private static IEnumerable<string> ResolveImagePaths(string path)
    {
        if (FolderScannerService.IsSupportedFile(path))
        {
            yield return path;
            yield break;
        }

        if (!Path.GetExtension(path).Equals(".xmp", StringComparison.OrdinalIgnoreCase))
            yield break;

        var appendedImage = path[..^4];
        if (FolderScannerService.IsSupportedFile(appendedImage) && File.Exists(appendedImage))
        {
            yield return appendedImage;
            yield break;
        }

        var directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory)) yield break;
        foreach (var candidate in Directory.EnumerateFiles(directory,
                     $"{Path.GetFileNameWithoutExtension(path)}.*", SearchOption.TopDirectoryOnly))
        {
            if (FolderScannerService.IsSupportedFile(candidate) &&
                string.Equals(FaceMetadataStore.GetSidecarPathForImage(candidate),
                    path, StringComparison.OrdinalIgnoreCase))
                yield return candidate;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _scanner.FileChanged -= OnFileChanged;
            _scanner.DirectoryChanged -= OnDirectoryChanged;
            _shutdown.Cancel();
            _timer.Dispose();
            _pending.Clear();
            _pendingDirectories.Clear();
        }
    }

    public sealed class WatchedLibraryChangedEventArgs(
        bool requiresGridReload,
        IReadOnlyList<string> changedImagePaths) : EventArgs
    {
        public bool RequiresGridReload { get; } = requiresGridReload;
        public IReadOnlyList<string> ChangedImagePaths { get; } = changedImagePaths;
    }
}
