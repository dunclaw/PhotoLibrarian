// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using System.Security.Cryptography;

namespace PhotoLibrarian.Core.Services;

public sealed class LibraryIndexMaintenanceService
{
    private readonly CacheDatabase _database;
    private readonly ImageRepository _images;
    private readonly LibraryIndexingService _indexer;
    private readonly ILibraryMaintenanceFileSystem _fileSystem;
    private int _isRunning;

    public LibraryIndexMaintenanceService(
        CacheDatabase database,
        ImageRepository images,
        LibraryIndexingService indexer,
        ILibraryMaintenanceFileSystem? fileSystem = null)
    {
        _database = database;
        _images = images;
        _indexer = indexer;
        _fileSystem = fileSystem ?? new PhysicalLibraryMaintenanceFileSystem();
    }

    public async Task<LibraryIndexMaintenanceResult> ReconcileAsync(
        IProgress<LibraryIndexMaintenanceProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) != 0)
        {
            throw new InvalidOperationException(
                "Library index maintenance is already running.");
        }

        var issues = new List<LibraryIndexMaintenanceIssue>();
        var directoriesScanned = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var filesFound = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var filesSeen = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var pathsWithErrors = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var repairedMoves = 0;
        var updatedFiles = 0;
        var confirmedMissing = 0;
        var totalFiles = 0;

        try
        {
            var watchedFolders = await GetWatchedFoldersAsync(cancellationToken);
            if (watchedFolders.Count == 0)
            {
                issues.Add(new LibraryIndexMaintenanceIssue(
                    LibraryIndexMaintenanceIssueKind.NoWatchedFolders,
                    "",
                    "Add a watched folder before repairing the library index."));
                return CreateResult();
            }

            var imageEntries = await _images.GetAllAsync(
                cancellationToken: cancellationToken);
            var entriesByPath = imageEntries
                .GroupBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < watchedFolders.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folder = watchedFolders[index];
                progress?.Report(new LibraryIndexMaintenanceProgress(
                    LibraryIndexMaintenanceStage.ScanningFolders,
                    index,
                    watchedFolders.Count,
                    folder.Path));
                ScanFolder(
                    folder,
                    directoriesScanned,
                    filesFound,
                    filesSeen,
                    pathsWithErrors,
                    issues,
                    progress,
                    cancellationToken);
            }

            var missingEntries = imageEntries
                .Where(entry => IsInWatchedFolders(entry.FilePath, watchedFolders) &&
                    IsConfirmedMissing(
                        entry.FilePath,
                        watchedFolders,
                        directoriesScanned,
                        filesFound,
                        filesSeen,
                        pathsWithErrors))
                .ToList();
            confirmedMissing = missingEntries.Count;

            var repairCandidates = missingEntries
                .Where(entry => entry.SearchMetadataIndexed &&
                    !entry.FaceMetadataExportRequired &&
                    entry.FaceMetadataImported)
                .ToList();
            var repairedPaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            if (repairCandidates.Count > 0)
            {
                repairedPaths = await RepairUniqueMovesAsync(
                    repairCandidates,
                    filesFound,
                    entriesByPath,
                    issues,
                    progress,
                    cancellationToken);
                repairedMoves = repairedPaths.Count;
            }

            var candidateFiles = filesFound
                .Where(path => !repairedPaths.Contains(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            totalFiles = candidateFiles.Length;
            for (var index = 0; index < candidateFiles.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = candidateFiles[index];
                progress?.Report(new LibraryIndexMaintenanceProgress(
                    LibraryIndexMaintenanceStage.IndexingFiles,
                    index,
                    totalFiles,
                    path));
                try
                {
                    if (await _images.GetByPathAsync(path) is
                        { FaceMetadataExportRequired: true })
                    {
                        issues.Add(new LibraryIndexMaintenanceIssue(
                            LibraryIndexMaintenanceIssueKind.PendingMetadataExport,
                            path,
                            "People metadata export is pending. Repair skipped this file to avoid writing photo-side metadata."));
                        continue;
                    }

                    if (await _indexer.IndexFileAsync(path, cancellationToken))
                    {
                        updatedFiles++;
                    }
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    issues.Add(new LibraryIndexMaintenanceIssue(
                        exception is UnauthorizedAccessException
                            ? LibraryIndexMaintenanceIssueKind.AccessDenied
                            : LibraryIndexMaintenanceIssueKind.FileIndexFailed,
                        path,
                        exception.Message));
                }
                progress?.Report(new LibraryIndexMaintenanceProgress(
                    LibraryIndexMaintenanceStage.IndexingFiles,
                    index + 1,
                    totalFiles,
                    path));
            }

            progress?.Report(new LibraryIndexMaintenanceProgress(
                LibraryIndexMaintenanceStage.Complete,
                totalFiles,
                totalFiles,
                ""));
            return CreateResult();
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            return CreateResult(cancelled: true);
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
        }

        LibraryIndexMaintenanceResult CreateResult(bool cancelled = false) =>
            new(
                updatedFiles,
                repairedMoves,
                confirmedMissing,
                issues,
                cancelled,
                totalFiles);
    }

    private async Task<List<WatchedFolder>> GetWatchedFoldersAsync(
        CancellationToken cancellationToken)
    {
        using var connection = _database.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT path, include_sub FROM watched_folders ORDER BY path";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var folders = new List<WatchedFolder>();
        while (await reader.ReadAsync(cancellationToken))
        {
            folders.Add(new WatchedFolder(
                reader.GetString(0),
                reader.GetInt64(1) == 1));
        }
        return folders;
    }

    private void ScanFolder(
        WatchedFolder folder,
        HashSet<string> directoriesScanned,
        HashSet<string> filesFound,
        HashSet<string> filesSeen,
        HashSet<string> pathsWithErrors,
        List<LibraryIndexMaintenanceIssue> issues,
        IProgress<LibraryIndexMaintenanceProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(folder.Path);

        while (pendingDirectories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = new List<string>();
            try
            {
                foreach (var entry in _fileSystem
                             .EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entries.Add(entry);
                }
                directoriesScanned.Add(directory);
                progress?.Report(new LibraryIndexMaintenanceProgress(
                    LibraryIndexMaintenanceStage.ScanningFolders,
                    directoriesScanned.Count,
                    0,
                    directory));
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
                pathsWithErrors.Add(directory);
                issues.Add(new LibraryIndexMaintenanceIssue(
                    exception is UnauthorizedAccessException
                        ? LibraryIndexMaintenanceIssueKind.AccessDenied
                        : IsRootFolder(directory, folder.Path)
                            ? LibraryIndexMaintenanceIssueKind.FolderUnavailable
                            : LibraryIndexMaintenanceIssueKind.FolderScanFailed,
                    directory,
                    exception.Message));
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var attributes = _fileSystem.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            pathsWithErrors.Add(entry);
                        }
                        else
                        {
                            filesSeen.Add(entry);
                        }
                        issues.Add(new LibraryIndexMaintenanceIssue(
                            LibraryIndexMaintenanceIssueKind.ReparsePointSkipped,
                            entry,
                            "Skipped a symbolic link or reparse point to avoid following an unverified location."));
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (folder.IncludeSubfolders)
                        {
                            pendingDirectories.Push(entry);
                        }
                    }
                    else if (FolderScannerService.IsSupportedFile(entry))
                    {
                        filesSeen.Add(entry);
                        filesFound.Add(entry);
                    }
                    else if ((attributes & FileAttributes.Directory) == 0)
                    {
                        filesSeen.Add(entry);
                    }
                }
                catch (Exception exception) when (
                    exception is UnauthorizedAccessException or IOException)
                {
                    pathsWithErrors.Add(entry);
                    issues.Add(new LibraryIndexMaintenanceIssue(
                        exception is UnauthorizedAccessException
                            ? LibraryIndexMaintenanceIssueKind.AccessDenied
                            : LibraryIndexMaintenanceIssueKind.FileReadFailed,
                        entry,
                        exception.Message));
                }
            }
        }
    }

    private async Task<HashSet<string>> RepairUniqueMovesAsync(
        IReadOnlyList<ImageEntry> missingEntries,
        IReadOnlySet<string> filesFound,
        IReadOnlyDictionary<string, ImageEntry> entriesByPath,
        List<LibraryIndexMaintenanceIssue> issues,
        IProgress<LibraryIndexMaintenanceProgress>? progress,
        CancellationToken cancellationToken)
    {
        var cachedHashes = await _images.GetDuplicateHashesAsync(
            cancellationToken);
        var cachedHashCounts = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var cached in cachedHashes.Values)
        {
            if (cached.Sha256 is not { } sha256)
            {
                continue;
            }

            cachedHashCounts.TryGetValue(sha256, out var count);
            cachedHashCounts[sha256] = count + 1;
        }
        var candidatesByHash = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);
        var expectedHashes = new Dictionary<string, List<ImageEntry>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var image in missingEntries)
        {
            if (!cachedHashes.TryGetValue(image.FilePath, out var cached) ||
                cached.Sha256 is null ||
                cached.FileSize != image.FileSize ||
                cached.ModifiedTicks != image.DateModified.Ticks)
            {
                continue;
            }

            if (!expectedHashes.TryGetValue(cached.Sha256, out var images))
            {
                images = [];
                expectedHashes.Add(cached.Sha256, images);
            }
            images.Add(image);
        }

        if (expectedHashes.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var targetSizes = expectedHashes.Values
            .SelectMany(images => images)
            .Select(image => image.FileSize)
            .ToHashSet();
        var candidatePaths = new List<string>();
        foreach (var path in filesFound)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fileInfo = new FileInfo(path);
                fileInfo.Refresh();
                if (fileInfo.Exists && targetSizes.Contains(fileInfo.Length))
                {
                    candidatePaths.Add(path);
                }
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
                issues.Add(new LibraryIndexMaintenanceIssue(
                    exception is UnauthorizedAccessException
                        ? LibraryIndexMaintenanceIssueKind.AccessDenied
                        : LibraryIndexMaintenanceIssueKind.FileReadFailed,
                    path,
                    exception.Message));
            }
        }

        for (var index = 0; index < candidatePaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = candidatePaths[index];
            progress?.Report(new LibraryIndexMaintenanceProgress(
                LibraryIndexMaintenanceStage.VerifyingMoves,
                index,
                candidatePaths.Count,
                path));
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken));
                if (expectedHashes.ContainsKey(hash))
                {
                    if (!candidatesByHash.TryGetValue(hash, out var paths))
                    {
                        paths = [];
                        candidatesByHash.Add(hash, paths);
                    }
                    paths.Add(path);
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                issues.Add(new LibraryIndexMaintenanceIssue(
                    exception is UnauthorizedAccessException
                        ? LibraryIndexMaintenanceIssueKind.AccessDenied
                        : LibraryIndexMaintenanceIssueKind.FileReadFailed,
                    path,
                    exception.Message));
            }
        }

        var repaired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in expectedHashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (match.Value.Count != 1 ||
                !cachedHashCounts.TryGetValue(match.Key, out var cachedCount) ||
                cachedCount != 1 ||
                !candidatesByHash.TryGetValue(match.Key, out var paths) ||
                paths.Count != 1 ||
                entriesByPath.ContainsKey(paths[0]))
            {
                continue;
            }

            var oldEntry = match.Value[0];
            var newPath = paths[0];
            var fileInfo = new FileInfo(newPath);
            try
            {
                fileInfo.Refresh();
                if (!fileInfo.Exists)
                {
                    continue;
                }

                var sidecarPath = FaceMetadataStore.GetSidecarPathForImage(newPath);
                FileInfo? sidecar;
                try
                {
                    _fileSystem.GetAttributes(sidecarPath);
                    sidecar = new FileInfo(sidecarPath);
                    sidecar.Refresh();
                }
                catch (FileNotFoundException)
                {
                    sidecar = null;
                }
                catch (DirectoryNotFoundException)
                {
                    sidecar = null;
                }

                if (!HasSameSidecarState(oldEntry, sidecar))
                {
                    continue;
                }

                if (await _images.TryRelocateAsync(
                        oldEntry.Id,
                        newPath,
                        fileInfo.Length,
                        fileInfo.LastWriteTimeUtc,
                        sidecar?.FullName,
                        sidecar?.Length,
                        sidecar?.LastWriteTimeUtc,
                        cancellationToken))
                {
                    repaired.Add(newPath);
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                issues.Add(new LibraryIndexMaintenanceIssue(
                    exception is UnauthorizedAccessException
                        ? LibraryIndexMaintenanceIssueKind.AccessDenied
                        : LibraryIndexMaintenanceIssueKind.FileReadFailed,
                    newPath,
                    exception.Message));
            }
        }

        return repaired;
    }

    private static bool HasSameSidecarState(
        ImageEntry oldEntry,
        FileInfo? newSidecar)
    {
        if (oldEntry.FaceSidecarPath is null)
        {
            return newSidecar is null;
        }

        return newSidecar is not null &&
            oldEntry.FaceSidecarSize == newSidecar.Length &&
            oldEntry.FaceSidecarModified == newSidecar.LastWriteTimeUtc;
    }

    private static bool IsInWatchedFolders(
        string filePath,
        IReadOnlyList<WatchedFolder> watchedFolders) =>
        watchedFolders.Any(folder =>
            IsWithinFolder(filePath, folder.Path, folder.IncludeSubfolders));

    private static bool IsConfirmedMissing(
        string filePath,
        IReadOnlyList<WatchedFolder> watchedFolders,
        IReadOnlySet<string> directoriesScanned,
        IReadOnlySet<string> filesFound,
        IReadOnlySet<string> filesSeen,
        IReadOnlySet<string> pathsWithErrors)
    {
        if (filesFound.Contains(filePath) ||
            filesSeen.Contains(filePath) ||
            pathsWithErrors.Contains(filePath))
        {
            return false;
        }

        var parentDirectory = Path.GetDirectoryName(filePath);
        if (parentDirectory is null)
        {
            return false;
        }

        foreach (var folder in watchedFolders)
        {
            if (!IsWithinFolder(filePath, folder.Path, folder.IncludeSubfolders))
            {
                continue;
            }

            var directory = parentDirectory;
            while (directory is not null &&
                IsDirectoryWithin(directory, folder.Path))
            {
                if (pathsWithErrors.Any(errorPath =>
                    IsDirectoryWithin(directory, errorPath)))
                {
                    return false;
                }

                if (directoriesScanned.Contains(directory))
                {
                    return true;
                }

                var next = Path.GetDirectoryName(directory);
                if (string.Equals(
                        next,
                        directory,
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                directory = next;
            }
        }

        return false;
    }

    private static bool IsDirectoryWithin(string directory, string parent)
    {
        var root = Path.TrimEndingDirectorySeparator(parent);
        var relativePath = Path.GetRelativePath(root, directory);
        return relativePath == "." ||
            !Path.IsPathRooted(relativePath) &&
            relativePath != ".." &&
            !relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWithinFolder(
        string filePath,
        string folderPath,
        bool includeSubfolders)
    {
        var root = Path.TrimEndingDirectorySeparator(folderPath);
        var parent = Path.GetDirectoryName(filePath);
        if (parent is null)
        {
            return false;
        }

        if (string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!includeSubfolders)
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(root, filePath);
        return !Path.IsPathRooted(relativePath) &&
            relativePath != ".." &&
            !relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRootFolder(string directory, string root) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(directory),
            Path.TrimEndingDirectorySeparator(root),
            StringComparison.OrdinalIgnoreCase);

    private sealed record WatchedFolder(string Path, bool IncludeSubfolders);
}

public interface ILibraryMaintenanceFileSystem
{
    IEnumerable<string> EnumerateFileSystemEntries(string directoryPath);
    FileAttributes GetAttributes(string path);
}

public sealed class PhysicalLibraryMaintenanceFileSystem
    : ILibraryMaintenanceFileSystem
{
    public IEnumerable<string> EnumerateFileSystemEntries(
        string directoryPath) =>
        Directory.EnumerateFileSystemEntries(directoryPath);

    public FileAttributes GetAttributes(string path) =>
        File.GetAttributes(path);
}

public sealed record LibraryIndexMaintenanceProgress(
    LibraryIndexMaintenanceStage Stage,
    int Current,
    int Total,
    string Path);

public sealed record LibraryIndexMaintenanceResult(
    int UpdatedFiles,
    int RepairedMoves,
    int ConfirmedMissingFiles,
    IReadOnlyList<LibraryIndexMaintenanceIssue> Issues,
    bool Cancelled,
    int TotalFiles)
{
    public bool Succeeded => !Cancelled && Issues.Count == 0;
}

public sealed record LibraryIndexMaintenanceIssue(
    LibraryIndexMaintenanceIssueKind Kind,
    string Path,
    string Message);

public enum LibraryIndexMaintenanceStage
{
    ScanningFolders,
    VerifyingMoves,
    IndexingFiles,
    Complete
}

public enum LibraryIndexMaintenanceIssueKind
{
    NoWatchedFolders,
    FolderUnavailable,
    AccessDenied,
    FolderScanFailed,
    FileReadFailed,
    FileIndexFailed,
    PendingMetadataExport,
    ReparsePointSkipped
}
