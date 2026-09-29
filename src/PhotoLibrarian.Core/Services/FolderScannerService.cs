namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Scans watched folders for supported image and video files.
/// Uses FileSystemWatcher for live change detection.
/// </summary>
public sealed class FolderScannerService : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];

    // Supported extensions
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".tiff", ".tif", ".bmp", ".gif", ".webp",
        ".heic", ".heif", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".m4v", ".webm"
    };

    public event EventHandler<FileDiscoveredEventArgs>? FileDiscovered;
    public event EventHandler<FileChangedEventArgs>? FileChanged;
    public event EventHandler<FileChangedEventArgs>? DirectoryChanged;
    public event EventHandler<ScanProgressEventArgs>? ScanProgress;

    /// <summary>
    /// Scans a folder recursively, yielding discovered media files.
    /// </summary>
    public async IAsyncEnumerable<string> ScanFolderAsync(
        string folderPath,
        bool includeSubfolders = true,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false
        };

        int count = 0;
        await Task.Yield(); // Release UI thread

        foreach (var file in Directory.EnumerateFiles(folderPath, "*.*", options))
        {
            ct.ThrowIfCancellationRequested();

            var ext = Path.GetExtension(file);
            if (ImageExtensions.Contains(ext) || VideoExtensions.Contains(ext))
            {
                count++;
                if (count % 100 == 0)
                    ScanProgress?.Invoke(this, new ScanProgressEventArgs(count, folderPath));

                yield return file;
            }
        }

        ScanProgress?.Invoke(this, new ScanProgressEventArgs(count, folderPath, isComplete: true));
    }

    /// <summary>
    /// Starts watching a folder for file changes.
    /// </summary>
    public void StartWatching(string folderPath, bool includeSubfolders = true)
    {
        var watcher = new FileSystemWatcher(folderPath)
        {
            IncludeSubdirectories = includeSubfolders,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            InternalBufferSize = 32 * 1024
        };

        watcher.Created += OnFileCreated;
        watcher.Changed += OnFileModified;
        watcher.Deleted += OnFileDeleted;
        watcher.Renamed += OnFileRenamed;
        watcher.Error += (_, e) => WatcherError?.Invoke(this, e.GetException());
        try
        {
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
    }

    public event EventHandler<Exception>? WatcherError;

    public void SyncWatchedFolders(IEnumerable<(string Path, bool IncludeSubfolders)> folders)
    {
        var wanted = folders.ToDictionary(
            folder => folder.Path,
            folder => folder.IncludeSubfolders,
            StringComparer.OrdinalIgnoreCase);
        var errors = new List<Exception>();

        foreach (var watcher in _watchers.ToArray())
        {
            if (!wanted.TryGetValue(watcher.Path, out var includeSubfolders) ||
                watcher.IncludeSubdirectories != includeSubfolders ||
                !Directory.Exists(watcher.Path))
                StopWatching(watcher.Path);
        }

        foreach (var folder in wanted)
        {
            if (Directory.Exists(folder.Key) &&
                !_watchers.Any(w => w.Path.Equals(folder.Key, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    StartWatching(folder.Key, folder.Value);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add(new IOException($"Could not monitor '{folder.Key}'.", ex));
                }
            }
        }
        if (errors.Count > 0)
            throw new AggregateException(errors);
    }

    public void StopWatching(string folderPath)
    {
        var watcher = _watchers.FirstOrDefault(w => w.Path.Equals(folderPath, StringComparison.OrdinalIgnoreCase));
        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            _watchers.Remove(watcher);
        }
    }

    public static bool IsSupportedFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ImageExtensions.Contains(ext) || VideoExtensions.Contains(ext);
    }

    public static bool IsVideoFile(string filePath)
    {
        return VideoExtensions.Contains(Path.GetExtension(filePath));
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (Directory.Exists(e.FullPath))
        {
            DirectoryChanged?.Invoke(this, new FileChangedEventArgs(e.FullPath, FileChangeType.Created));
            return;
        }
        if (IsSupportedFile(e.FullPath) || IsSidecar(e.FullPath))
            FileChanged?.Invoke(this, new FileChangedEventArgs(e.FullPath, FileChangeType.Created));
    }

    private void OnFileModified(object sender, FileSystemEventArgs e)
    {
        if (IsSupportedFile(e.FullPath) || IsSidecar(e.FullPath))
            FileChanged?.Invoke(this, new FileChangedEventArgs(e.FullPath, FileChangeType.Modified));
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (IsSupportedFile(e.FullPath) || IsSidecar(e.FullPath))
            FileChanged?.Invoke(this, new FileChangedEventArgs(e.FullPath, FileChangeType.Deleted));
        else
            DirectoryChanged?.Invoke(this, new FileChangedEventArgs(e.FullPath, FileChangeType.Deleted));
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (Directory.Exists(e.FullPath))
        {
            DirectoryChanged?.Invoke(this, new FileChangedEventArgs(e.OldFullPath, FileChangeType.Deleted));
            DirectoryChanged?.Invoke(this, new FileChangedEventArgs(e.FullPath, FileChangeType.Created));
            return;
        }
        if (IsSupportedFile(e.OldFullPath) || IsSidecar(e.OldFullPath))
            FileChanged?.Invoke(this, new FileChangedEventArgs(e.OldFullPath, FileChangeType.Deleted));
        if (IsSupportedFile(e.FullPath) || IsSidecar(e.FullPath))
            FileChanged?.Invoke(this, new FileChangedEventArgs(e.FullPath, FileChangeType.Created));
    }

    private static bool IsSidecar(string path) =>
        Path.GetExtension(path).Equals(".xmp", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}

public sealed class FileDiscoveredEventArgs(string filePath) : EventArgs
{
    public string FilePath { get; } = filePath;
}

public sealed class FileChangedEventArgs(string filePath, FileChangeType changeType) : EventArgs
{
    public string FilePath { get; } = filePath;
    public FileChangeType ChangeType { get; } = changeType;
}

public enum FileChangeType
{
    Created,
    Modified,
    Deleted
}

public sealed class ScanProgressEventArgs(int filesFound, string folder, bool isComplete = false) : EventArgs
{
    public int FilesFound { get; } = filesFound;
    public string Folder { get; } = folder;
    public bool IsComplete { get; } = isComplete;
}
