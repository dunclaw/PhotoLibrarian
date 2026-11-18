// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoLibrarian.Core.Services;

internal enum RecoveryBasedPublicationStatus
{
    PublishedWithBackup,
    Conflict,
    Busy,
    Unsupported,
    RecoveryRequired
}

internal enum RecoveryBasedRecoveryStatus
{
    NotPublished,
    PublishedWithBackup,
    Conflict,
    Invalid,
    Unavailable
}

internal sealed record RecoveryBasedPublicationResult(
    RecoveryBasedPublicationStatus Status,
    string? BackupPath = null,
    string? ManifestPath = null,
    string? Message = null);

internal sealed record RecoveryBasedRecoveryInspection(
    RecoveryBasedRecoveryStatus Status,
    RecoveryBasedPublicationManifest? Manifest = null,
    string? Message = null);

internal sealed record RecoveryBasedPublicationManifest(
    int SchemaVersion,
    string SourcePath,
    string ReplacementPath,
    string BackupPath,
    string ExpectedSourceHash,
    string ReplacementHash,
    Dictionary<string, string> NamedStreamHashes);

/// <summary>
/// Experimental single-file ReplaceFileW candidate. It is not connected to production
/// publication. It deliberately retains both the original backup and journal after success.
/// It does not prevent or fully detect concurrent writes to named streams or mapped views.
/// </summary>
internal static class RecoveryBasedFileReplacementCandidate
{
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static Task<RecoveryBasedPublicationResult> PublishAsync(
        string sourcePath,
        string replacementPath,
        string backupPath,
        string manifestPath,
        string expectedSourceHash,
        CancellationToken cancellationToken = default) =>
        PublishCoreAsync(sourcePath, replacementPath, backupPath, manifestPath,
            expectedSourceHash, null, cancellationToken);

    internal static Task<RecoveryBasedPublicationResult> PublishWithPostReplaceObserverAsync(
        string sourcePath,
        string replacementPath,
        string backupPath,
        string manifestPath,
        string expectedSourceHash,
        Action afterReplacement,
        CancellationToken cancellationToken = default) =>
        PublishCoreAsync(sourcePath, replacementPath, backupPath, manifestPath,
            expectedSourceHash, afterReplacement, cancellationToken);

    private static async Task<RecoveryBasedPublicationResult> PublishCoreAsync(
        string sourcePath,
        string replacementPath,
        string backupPath,
        string manifestPath,
        string expectedSourceHash,
        Action? afterReplacement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sourcePath = Path.GetFullPath(sourcePath);
        replacementPath = Path.GetFullPath(replacementPath);
        backupPath = Path.GetFullPath(backupPath);
        manifestPath = Path.GetFullPath(manifestPath);
        var paths = new[] { sourcePath, replacementPath, backupPath, manifestPath };
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            return new(RecoveryBasedPublicationStatus.Conflict,
                Message: "Source, replacement, backup, and recovery manifest paths must be distinct.");

        if (!File.Exists(sourcePath) || !File.Exists(replacementPath))
            return new(RecoveryBasedPublicationStatus.Conflict,
                Message: "The source or staged replacement is missing.");
        if (File.Exists(backupPath) || File.Exists(manifestPath))
            return new(RecoveryBasedPublicationStatus.Conflict,
                Message: "A backup or recovery manifest already exists.");
        if (FileEditRecovery.HasReparsePoint(sourcePath) ||
            FileEditRecovery.HasReparsePoint(replacementPath) ||
            FileEditRecovery.HasReparsePoint(backupPath) ||
            FileEditRecovery.HasReparsePoint(manifestPath))
            return new(RecoveryBasedPublicationStatus.Conflict,
                Message: "A publication path contains a reparse point.");

        var sourceVolume = GetVolume(sourcePath);
        var replacementVolume = GetVolume(replacementPath);
        var backupVolume = GetVolume(backupPath);
        if (sourceVolume is null || replacementVolume is null || backupVolume is null ||
            !string.Equals(sourceVolume.Value.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(sourceVolume.Value.Root, replacementVolume.Value.Root,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(sourceVolume.Value.Root, backupVolume.Value.Root,
                StringComparison.OrdinalIgnoreCase))
            return new(RecoveryBasedPublicationStatus.Unsupported,
                Message: "The candidate requires source, replacement, and backup files on the same NTFS volume.");

        var manifestDirectory = Path.GetDirectoryName(manifestPath);
        var backupDirectory = Path.GetDirectoryName(backupPath);
        if (manifestDirectory is null || !Directory.Exists(manifestDirectory) ||
            backupDirectory is null || !Directory.Exists(backupDirectory))
            return new(RecoveryBasedPublicationStatus.Unsupported,
                Message: "The backup and recovery journal directories must already exist.");

        try
        {
            await FlushReplacementAsync(replacementPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new(StreamInspectionFailure(exception),
                Message: $"The staged replacement could not be flushed before publication: {exception.Message}");
        }

        string actualSourceHash;
        try
        {
            actualSourceHash = await HashFileAsync(sourcePath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new(StreamInspectionFailure(exception),
                Message: $"The source cannot be inspected for publication: {exception.Message}");
        }
        if (!string.Equals(actualSourceHash, expectedSourceHash, StringComparison.Ordinal))
            return new(RecoveryBasedPublicationStatus.Conflict,
                Message: "The source changed before publication.");

        Dictionary<string, string> sourceStreams;
        Dictionary<string, string> replacementStreams;
        try
        {
            sourceStreams = await GetNamedStreamHashesAsync(sourcePath, cancellationToken);
            replacementStreams = await GetNamedStreamHashesAsync(replacementPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new(StreamInspectionFailure(exception),
                Message: $"The candidate could not inspect all named streams: {exception.Message}");
        }
        if (!ReplacementStreamsAreCompatible(sourceStreams, replacementStreams))
            return new(RecoveryBasedPublicationStatus.Conflict,
                Message: "The staged replacement contains named-stream data absent from or different from the source; no publication was attempted.");

        var replacementHash = await HashFileAsync(replacementPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(actualSourceHash,
                await HashFileAsync(sourcePath, cancellationToken), StringComparison.Ordinal))
            return new(RecoveryBasedPublicationStatus.Conflict,
                Message: "The source changed while publication was being prepared.");
        if (!SameStreams(sourceStreams,
                await GetNamedStreamHashesAsync(sourcePath, cancellationToken)))
            return new(RecoveryBasedPublicationStatus.Conflict,
                Message: "The source's named streams changed while publication was being prepared.");

        var manifest = new RecoveryBasedPublicationManifest(
            1, sourcePath, replacementPath, backupPath, expectedSourceHash,
            replacementHash, sourceStreams);
        WriteManifestDurably(manifestPath, manifest);

        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(actualSourceHash,
                await HashFileAsync(sourcePath, cancellationToken), StringComparison.Ordinal))
            return new(RecoveryBasedPublicationStatus.Conflict, backupPath, manifestPath,
                "The source changed after the recovery journal was written; the journal was retained.");
        if (!SameStreams(sourceStreams,
                await GetNamedStreamHashesAsync(sourcePath, cancellationToken)))
            return new(RecoveryBasedPublicationStatus.Conflict, backupPath, manifestPath,
                "The source's named streams changed after the recovery journal was written; the journal was retained.");

        if (!ReplaceFile(sourcePath, replacementPath, backupPath, 0, IntPtr.Zero, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            return new(error is 32 or 33
                    ? RecoveryBasedPublicationStatus.Busy
                    : RecoveryBasedPublicationStatus.RecoveryRequired,
                backupPath, manifestPath,
                new Win32Exception(error, "ReplaceFileW did not complete; retain all journaled files and inspect them before recovery.")
                    .Message);
        }

        afterReplacement?.Invoke();
        string publishedHash;
        string backupHash;
        Dictionary<string, string> publishedStreams;
        Dictionary<string, string> backupStreams;
        try
        {
            publishedHash = await HashFileAsync(sourcePath, CancellationToken.None);
            backupHash = await HashFileAsync(backupPath, CancellationToken.None);
            publishedStreams = await GetNamedStreamHashesAsync(sourcePath, CancellationToken.None);
            backupStreams = await GetNamedStreamHashesAsync(backupPath, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new(RecoveryBasedPublicationStatus.RecoveryRequired, backupPath, manifestPath,
                $"Publication returned, but verification could not complete. Retain all files and inspect the journal: {exception.Message}");
        }
        if (!string.Equals(publishedHash, replacementHash, StringComparison.Ordinal) ||
            !string.Equals(backupHash, actualSourceHash, StringComparison.Ordinal) ||
            !SameStreams(sourceStreams, publishedStreams) ||
            !SameStreams(sourceStreams, backupStreams))
            return new(RecoveryBasedPublicationStatus.RecoveryRequired, backupPath, manifestPath,
                "The published file, named streams, or retained backup did not match the pre-publication snapshot.");

        return new(RecoveryBasedPublicationStatus.PublishedWithBackup, backupPath, manifestPath,
            "The replacement was published. The original backup and journal are intentionally retained; review them before any cleanup.");
    }

    internal static async Task<RecoveryBasedRecoveryInspection> InspectAsync(
        string manifestPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        manifestPath = Path.GetFullPath(manifestPath);
        if (FileEditRecovery.HasReparsePoint(manifestPath))
            return new(RecoveryBasedRecoveryStatus.Invalid,
                Message: "The recovery manifest path contains a reparse point.");
        if (!File.Exists(manifestPath))
            return new(RecoveryBasedRecoveryStatus.Unavailable,
                Message: "The recovery manifest is missing.");

        RecoveryBasedPublicationManifest? manifest;
        try
        {
            await using var stream = new FileStream(
                manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (HasDuplicateProperties(document.RootElement))
                return new(RecoveryBasedRecoveryStatus.Invalid,
                    Message: "The recovery manifest contains duplicate fields.");
            manifest = JsonSerializer.Deserialize<RecoveryBasedPublicationManifest>(
                document.RootElement.GetRawText(), ManifestOptions);
        }
        catch (JsonException exception)
        {
            return new(RecoveryBasedRecoveryStatus.Invalid, Message: exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(RecoveryBasedRecoveryStatus.Unavailable, Message: exception.Message);
        }

        if (manifest is null || manifest.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(manifest.SourcePath) ||
            string.IsNullOrWhiteSpace(manifest.ReplacementPath) ||
            string.IsNullOrWhiteSpace(manifest.BackupPath) ||
            !Path.IsPathFullyQualified(manifest.SourcePath) ||
            !Path.IsPathFullyQualified(manifest.ReplacementPath) ||
            !Path.IsPathFullyQualified(manifest.BackupPath) ||
            !IsSha256(manifest.ExpectedSourceHash) || !IsSha256(manifest.ReplacementHash) ||
            !ValidNamedStreamMap(manifest.NamedStreamHashes))
            return new(RecoveryBasedRecoveryStatus.Invalid,
                Message: "The recovery manifest is incomplete or unsupported.");

        string source;
        string replacement;
        string backup;
        try
        {
            source = Path.GetFullPath(manifest.SourcePath);
            replacement = Path.GetFullPath(manifest.ReplacementPath);
            backup = Path.GetFullPath(manifest.BackupPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(RecoveryBasedRecoveryStatus.Invalid, Message: exception.Message);
        }
        var paths = new[] { source, replacement, backup, manifestPath };
        try
        {
            if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length ||
                paths.Any(FileEditRecovery.HasReparsePoint))
                return new(RecoveryBasedRecoveryStatus.Invalid,
                    Message: "The recovery manifest contains aliased or reparse-point paths.");
        }
        catch (Win32Exception exception)
        {
            return new(RecoveryBasedRecoveryStatus.Unavailable, manifest, exception.Message);
        }

        try
        {
            var sourceHash = await HashIfPresentAsync(source, cancellationToken);
            var replacementHash = await HashIfPresentAsync(replacement, cancellationToken);
            var backupHash = await HashIfPresentAsync(backup, cancellationToken);
            var sourceStreams = sourceHash is null
                ? null
                : await GetNamedStreamHashesAsync(source, cancellationToken);
            var replacementStreams = replacementHash is null
                ? null
                : await GetNamedStreamHashesAsync(replacement, cancellationToken);
            var backupStreams = backupHash is null
                ? null
                : await GetNamedStreamHashesAsync(backup, cancellationToken);
            if (sourceHash == manifest.ExpectedSourceHash &&
                replacementHash == manifest.ReplacementHash && backupHash is null &&
                sourceStreams is not null &&
                SameStreams(manifest.NamedStreamHashes, sourceStreams) &&
                replacementStreams is not null &&
                ReplacementStreamsAreCompatible(manifest.NamedStreamHashes, replacementStreams))
                return new(RecoveryBasedRecoveryStatus.NotPublished, manifest,
                    "The original source and staged replacement are intact; publication is not verified.");
            if (sourceHash == manifest.ReplacementHash &&
                replacementHash is null && backupHash == manifest.ExpectedSourceHash &&
                sourceStreams is not null && backupStreams is not null &&
                SameStreams(manifest.NamedStreamHashes, sourceStreams) &&
                SameStreams(manifest.NamedStreamHashes, backupStreams))
                return new(RecoveryBasedRecoveryStatus.PublishedWithBackup, manifest,
                    "The replacement and original backup match the journal; both are retained.");
            return new(RecoveryBasedRecoveryStatus.Conflict, manifest,
                "The source, staged file, or backup does not match the journal. Do not overwrite or delete any of them.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new(RecoveryBasedRecoveryStatus.Unavailable, manifest, exception.Message);
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static RecoveryBasedPublicationStatus StreamInspectionFailure(Exception exception) =>
        exception switch
        {
            Win32Exception native when native.NativeErrorCode is 32 or 33 =>
                RecoveryBasedPublicationStatus.Busy,
            IOException io when (io.HResult & 0xFFFF) is 32 or 33 =>
                RecoveryBasedPublicationStatus.Busy,
            _ => RecoveryBasedPublicationStatus.Unsupported
        };

    private static bool ValidNamedStreamMap(Dictionary<string, string>? streams) =>
        streams is not null && streams.All(item =>
            !string.IsNullOrWhiteSpace(item.Key) && item.Key.StartsWith(':') &&
            IsSha256(item.Value));

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                    return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (HasDuplicateProperties(item)) return true;
        }
        return false;
    }

    private static async Task<string?> HashIfPresentAsync(
        string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task<Dictionary<string, string>> GetNamedStreamHashesAsync(
        string path, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var data = new FindStreamData();
        var find = FindFirstStream(path, 0, ref data, 0);
        if (find == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 38) return result;
            throw new Win32Exception(error, $"Cannot enumerate named streams for {path}.");
        }

        try
        {
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(data.StreamName, "::$DATA", StringComparison.OrdinalIgnoreCase))
                {
                    var streamPath = path + data.StreamName;
                    await using var stream = new FileStream(
                        streamPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    result.Add(data.StreamName,
                        Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
                }
                data = new FindStreamData();
            }
            while (FindNextStream(find, ref data));

            var nextError = Marshal.GetLastWin32Error();
            if (nextError != 38)
                throw new Win32Exception(nextError, $"Cannot finish enumerating named streams for {path}.");
            return result;
        }
        finally
        {
            _ = FindClose(find);
        }
    }

    private static bool SameStreams(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second) =>
        first.Count == second.Count &&
        first.All(item => second.TryGetValue(item.Key, out var hash) &&
            string.Equals(item.Value, hash, StringComparison.Ordinal));

    private static bool ReplacementStreamsAreCompatible(
        IReadOnlyDictionary<string, string> source,
        IReadOnlyDictionary<string, string> replacement) =>
        replacement.All(item => source.TryGetValue(item.Key, out var hash) &&
            string.Equals(item.Value, hash, StringComparison.Ordinal));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindStreamData
    {
        public FindStreamData()
        {
            StreamSize = 0;
            StreamName = string.Empty;
        }

        public long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string StreamName = string.Empty;
    }

    private static (string Root, string FileSystem)? GetVolume(string path)
    {
        var root = new string('\0', 261);
        if (!GetVolumePathName(path, root, (uint)root.Length))
            return null;
        var fileSystemName = new string('\0', 261);
        if (!GetVolumeInformation(root, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, fileSystemName, (uint)fileSystemName.Length))
            return null;
        return (root.TrimEnd('\0'), fileSystemName.TrimEnd('\0'));
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task FlushReplacementAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteManifestDurably(
        string manifestPath, RecoveryBasedPublicationManifest manifest)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(manifestPath)!,
            $".{Path.GetFileName(manifestPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, manifest, ManifestOptions);
                stream.Flush(flushToDisk: true);
            }
            if (!MoveFileEx(temporaryPath, manifestPath, 0x8))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"Cannot publish recovery manifest {manifestPath}.");
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, string volumePathName, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string rootPathName, IntPtr volumeNameBuffer, uint volumeNameSize,
        IntPtr volumeSerialNumber, IntPtr maximumComponentLength, IntPtr fileSystemFlags,
        string fileSystemNameBuffer, uint fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ReplaceFileW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReplaceFile(
        string replacedFileName, string replacementFileName, string? backupFileName,
        uint replaceFlags, IntPtr exclude, IntPtr reserved);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MoveFileExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindFirstStreamW")]
    private static extern IntPtr FindFirstStream(
        string fileName, int informationLevel, ref FindStreamData findStreamData, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindNextStreamW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStream(IntPtr findStream, ref FindStreamData findStreamData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findFile);
}
