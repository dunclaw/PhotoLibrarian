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
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        // Watch all supported extensions
        watcher.Created += OnFileCreated;
        watcher.Changed += OnFileModified;
        watcher.Deleted += OnFileDeleted;
        watcher.Renamed += OnFileRenamed;

        _watchers.Add(watcher);
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
        if (IsSupportedFile(e.FullPath))
            FileChanged?.Invoke(this, new FileChangedEventArgs(e.FullPath, FileChangeType.Created));
    }

    private void OnFileModified(object sender, FileSystemEventArgs e)
    {
        if (IsSupportedFile(e.FullPath))
            FileChanged?.Invoke(this, new FileChangedEventArgs(e.FullPath, FileChangeType.Modified));
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (IsSupportedFile(e.FullPath))
            FileChanged?.Invoke(this, new FileChangedEventArgs(e.FullPath, FileChangeType.Deleted));
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (IsSupportedFile(e.OldFullPath))
            FileChanged?.Invoke(this, new FileChangedEventArgs(e.OldFullPath, FileChangeType.Deleted));
        if (IsSupportedFile(e.FullPath))
            FileChanged?.Invoke(this, new FileChangedEventArgs(e.FullPath, FileChangeType.Created));
    }

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
