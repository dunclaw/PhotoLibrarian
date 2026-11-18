// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoLibrarian.Core.Services;

public enum FileEditRecoveryStatus
{
    Ready,
    Applied,
    Resolved,
    Conflict,
    Invalid,
    Unsupported,
    Unavailable,
    Busy
}

public enum FileEditRecoveryChoice { Before, After }

public sealed record FileEditRecoveryInspection(
    FileEditRecoveryStatus Status, string? Message = null);

public sealed record FileEditRecoveryCandidate(
    string TransactionDirectory, string? SourcePath, FileEditRecoveryInspection Inspection);

/// <summary>Inspects interrupted version-family edits and restores explicitly chosen checkpoints.</summary>
public static class FileEditRecovery
{
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Finds interrupted transactions in the app's edit-history directory.</summary>
    public static async Task<IReadOnlyList<FileEditRecoveryCandidate>> FindAsync(
        string historyRoot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<FileEditRecoveryCandidate>();
        var root = historyRoot;
        try
        {
            root = Path.GetFullPath(historyRoot);
            var attributes = GetFileAttributes(root);
            if (attributes == uint.MaxValue)
            {
                var error = Marshal.GetLastWin32Error();
                if (error is 2 or 3) return [];
                throw new Win32Exception(error, $"Cannot inspect recovery history {root}.");
            }
            if (HasReparsePoint(root))
                return [new(root, null, new(FileEditRecoveryStatus.Invalid,
                    "The history root path contains a reparse point."))];
            if ((attributes & (uint)FileAttributes.Directory) == 0)
                return [new(root, null, new(FileEditRecoveryStatus.Invalid,
                    "The history root is not a directory."))];
            foreach (var session in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(session) & FileAttributes.ReparsePoint) != 0)
                {
                    results.Add(new(session, null, new(FileEditRecoveryStatus.Invalid,
                        "The history session directory is a reparse point.")));
                    continue;
                }
                foreach (var transaction in Directory.EnumerateDirectories(session))
                    await FindTransactionAsync(transaction, results, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or Win32Exception)
        {
            results.Add(new(root, null, new(FileEditRecoveryStatus.Unavailable,
                $"Recovery discovery could not inspect {root}: {exception.Message}")));
        }
        return results;
    }

    private static async Task FindTransactionAsync(
        string directory, List<FileEditRecoveryCandidate> results, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            results.Add(new(directory, null, new(FileEditRecoveryStatus.Invalid,
                "The recovery directory is a reparse point.")));
            return;
        }
        var path = Path.Combine(directory, FileEditRecoveryManifest.FileName);
        if (HasReparsePoint(path))
        {
            results.Add(new(directory, null, new(FileEditRecoveryStatus.Invalid,
                "The recovery manifest path contains a reparse point.")));
            return;
        }
        var manifestAttributes = GetExistingAttributes(path);
        if (manifestAttributes is not null &&
            (manifestAttributes.Value & FileAttributes.Directory) != 0)
        {
            results.Add(new(directory, null, new(FileEditRecoveryStatus.Invalid,
                "The recovery manifest is not a file.")));
        }
        else if (manifestAttributes is not null)
        {
            string? source = null;
            FileEditRecoveryInspection inspection;
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("SourcePath", out var property) &&
                    property.ValueKind == JsonValueKind.String)
                    source = property.GetString();
                if (HasDuplicateProperties(document.RootElement))
                    inspection = new(FileEditRecoveryStatus.Invalid, "The recovery manifest has duplicate fields.");
                else if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("SchemaVersion", out var schema) &&
                    schema.ValueKind == JsonValueKind.Number &&
                    schema.TryGetInt32(out var version) && version != 1)
                    inspection = new(FileEditRecoveryStatus.Unsupported, "The recovery manifest version is unsupported.");
                else
                    inspection = string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source)
                        ? new(FileEditRecoveryStatus.Invalid, "The recovery manifest has no valid source path.")
                        : await InspectAsync(directory, source, token);
            }
            catch (JsonException exception)
            {
                inspection = new(FileEditRecoveryStatus.Invalid, exception.Message);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception)
            {
                inspection = new(FileEditRecoveryStatus.Unavailable, exception.Message);
            }
            if (inspection.Status != FileEditRecoveryStatus.Resolved)
                results.Add(new(directory, source, inspection));
        }
        var attempts = Path.Combine(directory, "recovery-attempts");
        if (HasReparsePoint(attempts))
        {
            results.Add(new(attempts, null, new(FileEditRecoveryStatus.Invalid,
                "The recovery-attempt directory is a reparse point.")));
            return;
        }
        var attemptAttributes = GetExistingAttributes(attempts);
        if (attemptAttributes is null) return;
        if ((attemptAttributes.Value & FileAttributes.Directory) == 0)
        {
            results.Add(new(attempts, null, new(FileEditRecoveryStatus.Invalid,
                "The recovery-attempt path is not a directory.")));
            return;
        }
        foreach (var attempt in Directory.EnumerateDirectories(attempts))
            await FindTransactionAsync(attempt, results, token);
    }

    private static FileAttributes? GetExistingAttributes(string path)
    {
        var attributes = GetFileAttributes(path);
        if (attributes != uint.MaxValue)
            return (FileAttributes)attributes;
        var error = Marshal.GetLastWin32Error();
        if (error is 2 or 3) return null;
        throw new Win32Exception(error, $"Cannot inspect recovery path {path}.");
    }

    public static async Task<FileEditRecoveryInspection> InspectAsync(
        string transactionDirectory, string sourcePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var directory = Path.GetFullPath(transactionDirectory);
            var source = Path.GetFullPath(sourcePath);
            var manifestPath = Path.Combine(directory, FileEditRecoveryManifest.FileName);
            if (HasReparsePoint(manifestPath))
                return new(FileEditRecoveryStatus.Invalid, "The recovery manifest path contains a reparse point.");
            var manifestAttributes = GetExistingAttributes(manifestPath);
            if (manifestAttributes is null)
                return new(FileEditRecoveryStatus.Unavailable, "The recovery manifest is missing.");
            if ((manifestAttributes.Value & FileAttributes.Directory) != 0)
                return new(FileEditRecoveryStatus.Invalid, "The recovery manifest is not a file.");

            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            return await InspectDocumentAsync(directory, source, json, cancellationToken);
        }
        catch (JsonException exception)
        {
            return new(FileEditRecoveryStatus.Invalid, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception)
        {
            return new(FileEditRecoveryStatus.Unavailable, exception.Message);
        }
    }

    private static async Task<FileEditRecoveryInspection> InspectDocumentAsync(
        string directory, string source, string json, CancellationToken cancellationToken)
    {
        using var parsed = JsonDocument.Parse(json);
        if (HasDuplicateProperties(parsed.RootElement))
            return new(FileEditRecoveryStatus.Invalid, "The recovery manifest has duplicate fields.");
        if (parsed.RootElement.ValueKind != JsonValueKind.Object ||
            !parsed.RootElement.TryGetProperty("SchemaVersion", out var schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number ||
            !schemaVersion.TryGetInt32(out var version))
            return new(FileEditRecoveryStatus.Invalid, "The recovery manifest has no valid version.");
        if (version != 1)
            return new(FileEditRecoveryStatus.Unsupported, "The recovery manifest version is unsupported.");
        var manifest = JsonSerializer.Deserialize<FileEditRecoveryManifest>(json, ManifestOptions);
        if (manifest is null || manifest.Changes is null || manifest.Companions is null)
            return new(FileEditRecoveryStatus.Invalid, "The recovery manifest is incomplete.");
        if (string.IsNullOrWhiteSpace(manifest.SourcePath) ||
            !Path.IsPathFullyQualified(manifest.SourcePath) ||
            !string.Equals(Path.GetFullPath(manifest.SourcePath), source, StringComparison.OrdinalIgnoreCase))
            return new(FileEditRecoveryStatus.Invalid, "The recovery manifest identifies another source photo.");

        var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            source, Path.ChangeExtension(source, ".xmp"), source + ".xmp"
        };
        if (expectedPaths.Count != 3 || manifest.Changes.Count != expectedPaths.Count)
            return new(FileEditRecoveryStatus.Invalid, "The recovery manifest has unexpected files.");
        for (var index = 0; index < manifest.Changes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var change = manifest.Changes[index];
            if (change is null || change.Before is null || change.After is null ||
                string.IsNullOrWhiteSpace(change.Path) ||
                !Path.IsPathFullyQualified(change.Path) ||
                !expectedPaths.Remove(Path.GetFullPath(change.Path)) ||
                !ValidVersion(change.Before, directory, "before", index) ||
                !ValidVersion(change.After, directory, "checkpoints", index) ||
                !ValidTemporaryPath(change.RecoveryPath, change.Path) ||
                !ValidTemporaryPath(change.ReplacementPath, change.Path))
                return new(FileEditRecoveryStatus.Invalid, "The recovery manifest has invalid paths or hashes.");
            if (HasReparsePoint(change.Path) ||
                change.Before.Copy is { } before && HasReparsePoint(before) ||
                change.After.Copy is { } after && HasReparsePoint(after) ||
                change.RecoveryPath is { } recovery && HasReparsePoint(recovery) ||
                change.ReplacementPath is { } replacement && HasReparsePoint(replacement))
                return new(FileEditRecoveryStatus.Invalid, "A recovery path contains a reparse point.");
        }

        if (HasReparsePoint(Path.Combine(directory, "recovery.completed")))
            return new(FileEditRecoveryStatus.Invalid, "The recovery completion path contains a reparse point.");
        var resolution = await ReadResolutionAsync(directory, json, cancellationToken);
        if (resolution == ResolutionState.Valid)
            return new(FileEditRecoveryStatus.Resolved);
        if (resolution == ResolutionState.Invalid)
            return new(FileEditRecoveryStatus.Invalid, "The recovery completion record is invalid.");
        if (!FileEditTransaction.GetMediaCompanions(source).SequenceEqual(
            manifest.Companions, StringComparer.OrdinalIgnoreCase))
            return new(FileEditRecoveryStatus.Conflict, "The photo's same-name companions changed.");
        var legacySidecar = Path.ChangeExtension(source, ".xmp");
        if (manifest.Companions.Count > 0 && manifest.Changes.Any(change =>
            string.Equals(change.Path, legacySidecar, StringComparison.OrdinalIgnoreCase) &&
            change.Before.Hash != change.After.Hash))
            return new(FileEditRecoveryStatus.Conflict,
                "The interrupted edit changed a sidecar shared with another photo.");
        var attempts = Path.Combine(directory, "recovery-attempts");
        if (HasReparsePoint(attempts))
            return new(FileEditRecoveryStatus.Invalid, "The recovery-attempt path contains a reparse point.");
        var attemptAttributes = GetExistingAttributes(attempts);
        if (attemptAttributes is not null && (attemptAttributes.Value & FileAttributes.Directory) == 0)
            return new(FileEditRecoveryStatus.Invalid, "The recovery-attempt path is not a directory.");
        if (attemptAttributes is not null)
        {
            foreach (var attempt in Directory.EnumerateDirectories(attempts))
            {
                var attemptPath = Path.Combine(attempt, FileEditRecoveryManifest.FileName);
                if (HasReparsePoint(attemptPath) ||
                    HasReparsePoint(Path.Combine(attempt, "recovery.completed")))
                    return new(FileEditRecoveryStatus.Invalid, "A recovery-attempt path contains a reparse point.");
                var attemptManifestAttributes = GetExistingAttributes(attemptPath);
                if (attemptManifestAttributes is not null &&
                    ((attemptManifestAttributes.Value & FileAttributes.Directory) != 0 ||
                     await ReadResolutionAsync(attempt,
                        await File.ReadAllTextAsync(attemptPath, cancellationToken), cancellationToken) != ResolutionState.Valid))
                    return new(FileEditRecoveryStatus.Conflict,
                        $"An interrupted recovery must be addressed first: {attempt}.");
            }
        }
        foreach (var change in manifest.Changes)
        {
            if (change.Before.Hash != change.After.Hash)
            {
                if (!await CheckCheckpointAsync(change.Before, cancellationToken) ||
                    !await CheckCheckpointAsync(change.After, cancellationToken))
                    return new(FileEditRecoveryStatus.Conflict, "A recovery checkpoint is missing or changed.");
            }

            var current = await HashIfPresentAsync(change.Path, cancellationToken);
            if (current == change.Before.Hash || current == change.After.Hash)
                continue;

            var previousHash = manifest.Undo ? change.After.Hash : change.Before.Hash;
            if (current is null && previousHash is not null && change.RecoveryPath is not null &&
                await HashIfPresentAsync(change.RecoveryPath, cancellationToken) == previousHash)
                continue;

            return new(FileEditRecoveryStatus.Conflict,
                $"A file changed outside the interrupted edit: {change.Path}.");
        }
        return new(FileEditRecoveryStatus.Ready);
    }

    /// <summary>
    /// Restores an explicitly selected checkpoint only when every current input still matches
    /// the inspected interrupted edit. Recovery itself is recorded before publication.
    /// </summary>
    public static async Task<FileEditRecoveryInspection> RestoreAsync(
        string historyRoot, string transactionDirectory, string sourcePath, FileEditRecoveryChoice choice,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var leaseStatus = TryAcquireHistoryLease(historyRoot, out var lease);
        if (leaseStatus.Status != FileEditRecoveryStatus.Ready)
            return leaseStatus;
        using (lease)
        {
            var candidates = await FindAsync(historyRoot, cancellationToken);
            var directory = Path.GetFullPath(transactionDirectory);
            var candidate = candidates.FirstOrDefault(item =>
                string.Equals(Path.GetFullPath(item.TransactionDirectory), directory, StringComparison.OrdinalIgnoreCase));
            if (candidate is null || candidate.SourcePath is null ||
                !string.Equals(Path.GetFullPath(candidate.SourcePath), Path.GetFullPath(sourcePath),
                    StringComparison.OrdinalIgnoreCase) || !CanRestore(candidate, candidates))
                return new(FileEditRecoveryStatus.Conflict,
                    "Recovery history changed or contains another unresolved or ambiguous recovery.");
            return await RestoreCoreAsync(directory, sourcePath, choice, cancellationToken, null);
        }
    }

    internal static FileEditRecoveryInspection TryAcquireHistoryLease(string historyRoot, out FileStream? lease)
    {
        lease = null;
        try
        {
            var root = Path.GetFullPath(historyRoot);
            var path = Path.Combine(root, ".recovery.lock");
            if (HasReparsePoint(path))
                return new(FileEditRecoveryStatus.Invalid, "The recovery lock path contains a reparse point.");
            var attributes = GetExistingAttributes(root);
            if (attributes is null || (attributes.Value & FileAttributes.Directory) == 0)
                return new(FileEditRecoveryStatus.Unavailable, "The recovery history directory is unavailable.");
            lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (HasReparsePoint(path))
            {
                lease.Dispose();
                lease = null;
                return new(FileEditRecoveryStatus.Invalid, "The recovery lock path contains a reparse point.");
            }
            return new(FileEditRecoveryStatus.Ready);
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
        {
            lease?.Dispose();
            lease = null;
            return new(FileEditRecoveryStatus.Busy, $"Another photo edit or recovery is active: {exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or Win32Exception)
        {
            lease?.Dispose();
            lease = null;
            return new(FileEditRecoveryStatus.Unavailable, $"Recovery history is unavailable: {exception.Message}");
        }
    }

    public static bool CanRestore(
        FileEditRecoveryCandidate candidate, IReadOnlyList<FileEditRecoveryCandidate> candidates)
    {
        if (candidate.Inspection.Status != FileEditRecoveryStatus.Ready || candidate.SourcePath is null)
            return false;
        var source = Path.GetFullPath(candidate.SourcePath);
        foreach (var other in candidates)
        {
            if (ReferenceEquals(candidate, other)) continue;
            if (other.SourcePath is null || other.Inspection.Status is FileEditRecoveryStatus.Invalid or
                FileEditRecoveryStatus.Unsupported or FileEditRecoveryStatus.Unavailable or FileEditRecoveryStatus.Busy)
                return false;
            if (string.Equals(source, Path.GetFullPath(other.SourcePath), StringComparison.OrdinalIgnoreCase) &&
                !IsAncestor(candidate.TransactionDirectory, other.TransactionDirectory) &&
                !IsAncestor(other.TransactionDirectory, candidate.TransactionDirectory))
                return false;
        }
        return true;
    }

    private static bool IsAncestor(string ancestor, string path)
    {
        var relative = Path.GetRelativePath(ancestor, path);
        return relative != "." && !Path.IsPathRooted(relative) &&
            relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static Task<FileEditRecoveryInspection> RestoreAsync(
        string transactionDirectory, string sourcePath, FileEditRecoveryChoice choice,
        CancellationToken cancellationToken = default) =>
        RestoreCoreAsync(transactionDirectory, sourcePath, choice, cancellationToken, null);

    internal static Task<FileEditRecoveryInspection> RestoreWithPublicationObserverAsync(
        string transactionDirectory, string sourcePath, FileEditRecoveryChoice choice,
        Action afterRename) =>
        RestoreCoreAsync(transactionDirectory, sourcePath, choice, CancellationToken.None, afterRename);

    private static async Task<FileEditRecoveryInspection> RestoreCoreAsync(
        string transactionDirectory, string sourcePath, FileEditRecoveryChoice choice,
        CancellationToken cancellationToken, Action? afterRename)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(choice))
            return new(FileEditRecoveryStatus.Invalid, "The recovery choice is invalid.");
        FileEditTransaction? prepared = null;
        var publishing = false;
        try
        {
            var directory = Path.GetFullPath(transactionDirectory);
            var source = Path.GetFullPath(sourcePath);
            var manifestPath = Path.Combine(directory, FileEditRecoveryManifest.FileName);
            if (HasReparsePoint(manifestPath))
                return new(FileEditRecoveryStatus.Invalid, "The recovery manifest path contains a reparse point.");
            await using var manifestStream = new FileStream(
                manifestPath, FileMode.Open, FileAccess.Read, FileShare.None);
            using var reader = new StreamReader(manifestStream, Encoding.UTF8, true, leaveOpen: true);
            var json = await reader.ReadToEndAsync(cancellationToken);
            var beforeInspection = await FileEditTransaction.ReadIdentityAsync(source, cancellationToken);
            var inspection = await InspectDocumentAsync(directory, source, json, cancellationToken);
            if (inspection.Status != FileEditRecoveryStatus.Ready)
                return inspection;
            var inspectedIdentity = await FileEditTransaction.ReadIdentityAsync(source, cancellationToken);
            if (!string.Equals(beforeInspection, inspectedIdentity, StringComparison.Ordinal))
                return new(FileEditRecoveryStatus.Conflict, "The photo changed during recovery inspection.");
            var manifest = JsonSerializer.Deserialize<FileEditRecoveryManifest>(json, ManifestOptions)!;
            var changes = manifest.Changes.ToDictionary(
                change => Path.GetFullPath(change.Path), StringComparer.OrdinalIgnoreCase);
            var paths = new[] { source, Path.ChangeExtension(source, ".xmp"), source + ".xmp" };
            var targetIdentity = string.Join("|", paths.Select(path =>
            {
                var change = changes[path];
                return (choice == FileEditRecoveryChoice.Before ? change.Before.Hash : change.After.Hash) ?? "-";
            }));
            prepared = await FileEditTransaction.PrepareAsync(
                Path.Combine(directory, "recovery-attempts"), [source], copies =>
                {
                    var stagedSource = copies[source];
                    var stagedPaths = new[]
                    {
                        stagedSource, Path.ChangeExtension(stagedSource, ".xmp"), stagedSource + ".xmp"
                    };
                    for (var index = 0; index < paths.Length; index++)
                    {
                        var change = changes[paths[index]];
                        if (change.Before.Hash == change.After.Hash) continue;
                        var version = choice == FileEditRecoveryChoice.Before ? change.Before : change.After;
                        if (version.Copy is null)
                        {
                            if (File.Exists(stagedPaths[index])) File.Delete(stagedPaths[index]);
                        }
                        else File.Copy(version.Copy, stagedPaths[index], overwrite: true);
                    }
                    return Task.CompletedTask;
                }, cancellationToken, durableRecovery: true);
            if (prepared.InputIdentity(source) != inspectedIdentity ||
                prepared.OutputIdentity(source) != targetIdentity)
                return new(FileEditRecoveryStatus.Conflict, "A recovery input or checkpoint changed.");
            cancellationToken.ThrowIfCancellationRequested();
            publishing = true;
            var result = afterRename is null
                ? await prepared.MoveAsync(false, () => Task.CompletedTask)
                : await prepared.MoveWithPublicationObserverAsync(false, () => Task.CompletedTask, afterRename);
            publishing = false;
            if (result.Status != EditHistoryStatus.Applied)
                return new(FileEditRecoveryStatus.Conflict, result.Message);
            try
            {
                prepared.Dispose();
                prepared = null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                prepared = null;
                return new(FileEditRecoveryStatus.Applied,
                    $"Recovery was applied, but its checkpoints could not be released: {exception.Message}");
            }
            try
            {
                WriteResolution(directory, json, choice);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new(FileEditRecoveryStatus.Applied,
                    $"Recovery was applied, but its completion record could not be saved: {exception.Message}");
            }
            return new(FileEditRecoveryStatus.Applied,
                $"Recovery was applied. Checkpoints remain in {directory}; temporary files may remain beside the photo.");
        }
        catch (JsonException exception)
        {
            return new(FileEditRecoveryStatus.Invalid, exception.Message);
        }
        catch (XmpCore.XmpException exception)
        {
            return new(FileEditRecoveryStatus.Invalid, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception)
        {
            return new(FileEditRecoveryStatus.Unavailable, publishing
                ? $"Recovery may require further inspection: {exception.Message}"
                : exception.Message);
        }
        finally
        {
            if (prepared is not null && !publishing)
                prepared.Dispose();
        }
    }

    private enum ResolutionState { Missing, Valid, Invalid }

    private static async Task<ResolutionState> ReadResolutionAsync(
        string directory, string json, CancellationToken token)
    {
        var path = Path.Combine(directory, "recovery.completed");
        var attributes = GetExistingAttributes(path);
        if (attributes is null) return ResolutionState.Missing;
        if ((attributes.Value & FileAttributes.Directory) != 0) return ResolutionState.Invalid;
        var contents = await File.ReadAllTextAsync(path, token);
        var lines = contents.Split('\n');
        return lines.Length == 3 && lines[2].Length == 0 &&
            lines[0] == ManifestHash(json) &&
            Enum.TryParse<FileEditRecoveryChoice>(lines[1], out var choice) &&
            Enum.IsDefined(choice)
                ? ResolutionState.Valid : ResolutionState.Invalid;
    }

    private static void WriteResolution(string directory, string json, FileEditRecoveryChoice choice)
    {
        var path = Path.Combine(directory, "recovery.completed");
        var temporary = Path.Combine(directory, $"recovery-completed-{Guid.NewGuid():N}.tmp");
        var bytes = Encoding.UTF8.GetBytes($"{ManifestHash(json)}\n{choice}\n");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string ManifestHash(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    private static bool ValidVersion(FileEditRecoveryVersion version, string directory, string folder, int index)
    {
        if (version.Hash is not null &&
            (version.Hash.Length != 64 || !version.Hash.All(Uri.IsHexDigit)))
            return false;
        if (version.Copy is null)
            return true;
        return version.Hash is not null && Path.IsPathFullyQualified(version.Copy) &&
            string.Equals(Path.GetFullPath(version.Copy), Path.Combine(directory, folder, index.ToString()),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidTemporaryPath(string? temporary, string target)
    {
        if (temporary is null) return true;
        if (!Path.IsPathFullyQualified(temporary) ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(temporary)), Path.GetDirectoryName(target),
                StringComparison.OrdinalIgnoreCase))
            return false;
        var name = Path.GetFileName(temporary);
        const string prefix = ".PhotoLibrarian-edit-";
        const string suffix = ".tmp";
        return name.StartsWith(prefix, StringComparison.Ordinal) &&
            name.EndsWith(suffix, StringComparison.Ordinal) &&
            Guid.TryParseExact(name[prefix.Length..^suffix.Length], "N", out _);
    }

    internal static bool HasReparsePoint(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            var attributes = GetFileAttributes(current);
            if (attributes == uint.MaxValue)
            {
                var error = Marshal.GetLastWin32Error();
                if (error is not (2 or 3))
                    throw new Win32Exception(error, $"Cannot inspect recovery path {current}.");
            }
            else if ((attributes & (uint)FileAttributes.ReparsePoint) != 0)
                return true;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFileAttributesW")]
    private static extern uint GetFileAttributes(string path);

    private static async Task<bool> CheckCheckpointAsync(
        FileEditRecoveryVersion version, CancellationToken cancellationToken)
    {
        if (version.Hash is null) return version.Copy is null;
        return version.Copy is not null &&
            await HashIfPresentAsync(version.Copy, cancellationToken) == version.Hash;
    }

    private static async Task<string?> HashIfPresentAsync(string path, CancellationToken cancellationToken)
    {
        var attributes = GetExistingAttributes(path);
        if (attributes is null) return null;
        if ((attributes.Value & FileAttributes.Directory) != 0)
            throw new IOException($"A recovery file path is a directory: {path}.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

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
}
