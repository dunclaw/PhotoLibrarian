// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Disk-backed before/after checkpoints. Edits are prepared on private copies, then published
/// only if every input still matches. Replay locks all affected files against writes/renames
/// before validating any of them. RAW bytes are checked but never opened for writing.
/// </summary>
public sealed class FileEditTransaction : IDisposable
{
    private sealed record Version(string? Copy, string? Hash);
    private sealed record Change(string Path, Version Before, Version After);
    private sealed class Lease(Change change, FileStream stream, bool created, bool writable)
    {
        public Change Change { get; } = change;
        public FileStream Stream { get; } = stream;
        public bool Created { get; } = created;
        public bool Writable { get; } = writable;
        public string? RecoveryPath { get; set; }
        public FileStream? Replacement { get; set; }
        public string? ReplacementPath { get; set; }
        public bool ReplacementPublished { get; set; }
    }
    private readonly string _directory;
    private readonly List<Change> _changes;
    private bool _disposed;

    private FileEditTransaction(string directory, List<Change> changes)
    {
        _directory = directory;
        _changes = changes;
    }

    public bool HasChanges => _changes.Any(change => change.Before.Hash != change.After.Hash);

    public string InputIdentity(string photoPath)
    {
        var path = Path.GetFullPath(photoPath);
        return string.Join("|", RelatedPaths(path).Select(related =>
            _changes.Single(change => string.Equals(change.Path, related, StringComparison.OrdinalIgnoreCase)).Before.Hash ?? "-"));
    }

    public static async Task<string> ReadIdentityAsync(string photoPath)
    {
        var hashes = new List<string>();
        foreach (var path in RelatedPaths(Path.GetFullPath(photoPath)))
        {
            if (!File.Exists(path)) hashes.Add("-");
            else
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                hashes.Add(Convert.ToHexString(await SHA256.HashDataAsync(stream)));
            }
        }
        return string.Join("|", hashes);
    }

    private static string[] RelatedPaths(string path) => [path, Path.ChangeExtension(path, ".xmp"), path + ".xmp"];

    public static async Task<FileEditTransaction> PrepareAsync(
        string storageDirectory,
        IReadOnlyCollection<string> photoPaths,
        Func<IReadOnlyDictionary<string, string>, Task> prepare)
    {
        var directory = Path.Combine(storageDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in photoPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var folder = Path.GetDirectoryName(path)!;
                if (!folders.TryGetValue(folder, out var stagedFolder))
                {
                    stagedFolder = Path.Combine(directory, "after", folders.Count.ToString());
                    Directory.CreateDirectory(stagedFolder);
                    folders.Add(folder, stagedFolder);
                }
                var staged = Path.Combine(stagedFolder, Path.GetFileName(path));
                mapping.Add(path, staged);
                files[path] = staged;
                files[Path.ChangeExtension(path, ".xmp")] = Path.ChangeExtension(staged, ".xmp");
                files[path + ".xmp"] = staged + ".xmp";

                // Preserve same-stem companion ownership rules without copying unrelated photos.
                foreach (var companion in Directory.EnumerateFiles(folder, Path.GetFileNameWithoutExtension(path) + ".*"))
                {
                    if (!FolderScannerService.IsSupportedFile(companion)) continue;
                    var placeholder = Path.Combine(stagedFolder, Path.GetFileName(companion));
                    if (!File.Exists(placeholder)) File.WriteAllBytes(placeholder, []);
                }
            }

            var before = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, staged) in files)
            {
                var copy = Path.Combine(directory, "before", before.Count.ToString());
                before.Add(path, await CaptureAsync(path, copy));
                if (before[path].Copy is { } source)
                {
                    await using (var input = File.OpenRead(source))
                    await using (var output = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None))
                        await input.CopyToAsync(output);
                    if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(staged, FileAttributes.ReadOnly);
                }
                else if (File.Exists(staged))
                    File.Delete(staged);
                if (Path.GetExtension(path).Equals(".xmp", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(staged))
                {
                    using var stream = File.OpenRead(staged);
                    _ = XmpCore.XmpMetaFactory.Parse(stream);
                }
            }
            await prepare(mapping);
            var changes = new List<Change>();
            foreach (var (path, staged) in files)
            {
                var after = await CaptureAsync(staged, Path.Combine(directory, "checkpoints", changes.Count.ToString()));
                if (RawToneMappingService.IsRaw(path) && before[path].Hash != after.Hash)
                    throw new InvalidOperationException("An edit attempted to change a RAW original.");
                var previous = before[path];
                if (previous.Hash == after.Hash)
                {
                    if (previous.Copy is not null) File.Delete(previous.Copy);
                    if (after.Copy is not null) File.Delete(after.Copy);
                    previous = previous with { Copy = null };
                    after = after with { Copy = null };
                }
                changes.Add(new(path, previous, after));
            }
            DeleteStorage(Path.Combine(directory, "after"));
            return new(directory, changes);
        }
        catch
        {
            DeleteStorage(directory);
            throw;
        }
    }

    private static async Task<Version> CaptureAsync(string path, string copy)
    {
        if (!File.Exists(path)) return new(null, null);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using (var destination = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await source.CopyToAsync(destination);
        source.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(source));
        return new(copy, hash);
    }

    public async Task<EditHistoryResult> MoveAsync(bool undo, Func<Task> refresh)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var leases = new List<Lease>();
        var published = false;
        var rollbackFailed = false;
        var cleanupErrors = new List<Exception>();
        try
        {
            foreach (var change in _changes.OrderBy(change => change.Path, StringComparer.OrdinalIgnoreCase))
            {
                var expected = undo ? change.After : change.Before;
                if (File.Exists(change.Path) != (expected.Hash is not null))
                    return Conflict(change.Path);
                var writable = change.Before.Hash != change.After.Hash;
                if (!writable && expected.Hash is null) continue;
                var created = expected.Hash is null;
                var leasedPath = created ? TemporarySibling(change.Path) : change.Path;
                var handle = OpenLocked(leasedPath, writable, created);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    if (error is 2 or 3 or 32 or 33 or 80 or 183)
                        return Conflict(change.Path);
                    throw new Win32Exception(error, $"Cannot lock {change.Path} for edit history.");
                }
                var stream = new FileStream(handle, writable ? FileAccess.ReadWrite : FileAccess.Read);
                var lease = new Lease(change, stream, created, writable);
                if (created) lease.RecoveryPath = leasedPath;
                leases.Add(lease);
                if (expected.Hash is not null &&
                    Convert.ToHexString(await SHA256.HashDataAsync(stream)) != expected.Hash)
                    return Conflict(change.Path);
            }
            foreach (var lease in leases.Where(lease => lease.Created))
                if (File.Exists(lease.Change.Path)) return Conflict(lease.Change.Path);

            // Write fresh siblings before publishing. Truncating a viewer's memory-mapped
            // original is not supported by Windows, even after its read stream is closed.
            foreach (var lease in leases.Where(lease => lease.Writable))
            {
                var version = undo ? lease.Change.Before : lease.Change.After;
                if (version.Hash is null) continue;
                var temporary = TemporarySibling(lease.Change.Path);
                var handle = OpenLocked(temporary, writable: true, create: true);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new Win32Exception(error, $"Cannot prepare {lease.Change.Path} for edit history.");
                }
                lease.ReplacementPath = temporary;
                lease.Replacement = new FileStream(handle, FileAccess.ReadWrite);
                await WriteAsync(lease.Replacement, version);
            }

            try
            {
                foreach (var lease in leases.Where(lease => lease.Writable))
                {
                    if (!lease.Created)
                    {
                        var recovery = TemporarySibling(lease.Change.Path);
                        RenameLocked(lease.Stream.SafeFileHandle, recovery);
                        lease.RecoveryPath = recovery;
                    }
                    if (lease.Replacement is not null)
                    {
                        RenameLocked(lease.Replacement.SafeFileHandle, lease.Change.Path);
                        lease.ReplacementPublished = true;
                    }
                }
                published = true;
            }
            catch (Exception publishError)
            {
                try
                {
                    foreach (var lease in leases.Where(lease => lease.ReplacementPublished).Reverse())
                    {
                        RenameLocked(lease.Replacement!.SafeFileHandle, lease.ReplacementPath!);
                        lease.ReplacementPublished = false;
                    }
                    foreach (var lease in leases.Where(lease => lease.RecoveryPath is not null && !lease.Created).Reverse())
                    {
                        RenameLocked(lease.Stream.SafeFileHandle, lease.Change.Path);
                        lease.RecoveryPath = null;
                    }
                }
                catch (Exception rollbackError)
                {
                    rollbackFailed = true;
                    throw new IOException($"Edit and rollback failed. Recovery checkpoints are in {_directory}.",
                        new AggregateException(publishError, rollbackError));
                }
                throw;
            }
        }
        finally
        {
            foreach (var lease in leases)
            {
                try
                {
                    if (!rollbackFailed && (published && lease.Writable || !published && lease.Created))
                        MarkForDeletion(lease.Stream.SafeFileHandle);
                }
                catch (Exception exception) { cleanupErrors.Add(exception); }
                try { lease.Stream.Dispose(); }
                catch (Exception exception) { cleanupErrors.Add(exception); }
                if (lease.Replacement is not null)
                {
                    try
                    {
                        if (!published && !rollbackFailed) MarkForDeletion(lease.Replacement.SafeFileHandle);
                    }
                    catch (Exception exception) { cleanupErrors.Add(exception); }
                    try { lease.Replacement.Dispose(); }
                    catch (Exception exception) { cleanupErrors.Add(exception); }
                }
            }
            if (!published && cleanupErrors.Count > 0)
                throw new IOException($"Could not release edit-history files. Recovery checkpoints are in {_directory}.",
                    new AggregateException(cleanupErrors));
        }

        try
        {
            await refresh();
        }
        catch (Exception refreshError)
        {
            // Revalidate before compensating: never roll back over a later external write.
            var rollback = await MoveWithoutRefreshAsync(!undo);
            if (rollback.Status != EditHistoryStatus.Applied)
                throw new IOException($"Refresh failed and files changed again. Recovery checkpoints are in {_directory}.", refreshError);
            throw;
        }
        if (cleanupErrors.Count == 0) return new(EditHistoryStatus.Applied);
        var message = $"Edit applied, but temporary recovery files could not be removed. Checkpoints: {_directory}.";
        Diagnostics.DebugLog.WriteLine($"[HISTORY] {message} {new AggregateException(cleanupErrors)}");
        return new(EditHistoryStatus.Applied, message);
    }

    private Task<EditHistoryResult> MoveWithoutRefreshAsync(bool undo) =>
        MoveAsync(undo, () => Task.CompletedTask);

    private static async Task WriteAsync(FileStream destination, Version version)
    {
        destination.Position = 0;
        if (version.Copy is { } copy)
        {
            await using var source = File.OpenRead(copy);
            if (Convert.ToHexString(await SHA256.HashDataAsync(source)) != version.Hash)
                throw new IOException($"The edit-history checkpoint is damaged: {copy}");
            source.Position = 0;
            await source.CopyToAsync(destination);
        }
        destination.SetLength(destination.Position);
        destination.Flush(flushToDisk: true);
    }

    private static EditHistoryResult Conflict(string path) => new(EditHistoryStatus.Conflict,
        $"History was not changed: {Path.GetFileName(path)} is missing, busy, or changed outside this edit. " +
        "Resolve the external change or restart to begin a new history.");

    private static string TemporarySibling(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, $".PhotoLibrarian-edit-{Guid.NewGuid():N}.tmp");

    private static void RenameLocked(SafeFileHandle handle, string destination)
    {
        var name = Encoding.Unicode.GetBytes(destination + "\0");
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(int);
        var size = nameOffset + name.Length;
        var information = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, information, size);
            Marshal.WriteIntPtr(information, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(information, lengthOffset, name.Length - sizeof(char));
            Marshal.Copy(name, 0, information + nameOffset, name.Length);
            // FileRenameInfo with ReplaceIfExists=false: a concurrent new destination wins.
            if (!SetFileRenameInformation(handle, 3, information, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot publish edit-history file {destination}.");
        }
        finally { Marshal.FreeHGlobal(information); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        DeleteStorage(_directory);
        _disposed = true;
    }

    private static SafeFileHandle OpenLocked(string path, bool writable, bool create) =>
        // GENERIC_READ | GENERIC_WRITE | DELETE, sharing reads only. DELETE access permits
        // removing a newly-created sidecar via this locked handle, with no close/delete race.
        CreateFile(path, writable ? 0xC0010000u : 0x80000000u, 1, IntPtr.Zero,
            create ? 1u : 3u, 0x80, IntPtr.Zero);

    private static void DeleteStorage(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
    }

    private static void MarkForDeletion(SafeFileHandle handle)
    {
        // POSIX disposition unlinks the old name even while an image viewer maps its bytes.
        var information = 3; // FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS
        if (SetFileInformationByHandle(handle, 21, ref information, sizeof(int))) return;
        var error = Marshal.GetLastWin32Error();
        if (error is not (1 or 50 or 87))
            throw new Win32Exception(error, "Cannot remove an edit-history recovery file.");
        information = 1;
        if (!SetFileInformationByHandle(handle, 4, ref information, sizeof(int)))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot remove an edit-history recovery file.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref int information, int size);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileRenameInformation(SafeFileHandle handle, int informationClass,
        IntPtr information, uint size);
}
