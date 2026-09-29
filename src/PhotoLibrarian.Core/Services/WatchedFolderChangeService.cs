using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Diagnostics;

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

    public event EventHandler? LibraryChanged;
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

    private async Task ProcessPendingAsync()
    {
        lock (_sync)
        {
            if (_disposed || _processing) return;
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
            foreach (var directory in directories)
            {
                try
                {
                    if (Directory.Exists(directory))
                    {
                        await _indexer.IndexFolderAsync(directory, true, _shutdown.Token);
                        changed = true;
                    }
                    else
                    {
                        changed |= await _images.DeleteMissingInDirectoryAsync(directory) > 0;
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
                        changed |= await ProcessFileAsync(imagePath, _shutdown.Token);
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
                LibraryChanged?.Invoke(this, EventArgs.Empty);
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
            if (await _images.GetByPathAsync(path) is null) return false;
            cancellationToken.ThrowIfCancellationRequested();
            await _images.DeleteByPathAsync(path);
            return true;
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
}
