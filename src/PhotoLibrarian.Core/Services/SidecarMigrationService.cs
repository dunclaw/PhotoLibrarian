// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using XmpCore;

namespace PhotoLibrarian.Core.Services;

public enum SidecarMigrationState { Ready, Skipped, Migrated, Failed, Canceled }

public sealed record SidecarMigrationItem(
    string SidecarPath,
    string? ImagePath,
    SidecarMigrationState State,
    string Reason,
    string? ImageHash = null,
    string? SidecarHash = null,
    string? BackupDirectory = null,
    string? Error = null);

/// <summary>
/// Folder-scoped, opt-in migration. Files, never the index, supply all metadata.
/// Only JPEG and PNG have a proven byte-preserving packet writer.
/// </summary>
public sealed class SidecarMigrationService
{
    internal const string BackupFolderName = ".photolibrarian-migration-backups";
    private readonly Action<FileStream, byte[]> _writeMedia;

    public SidecarMigrationService() : this(Overwrite) { }

    internal SidecarMigrationService(Action<FileStream, byte[]> writeMedia) =>
        _writeMedia = writeMedia;

    public Task<IReadOnlyList<SidecarMigrationItem>> PreviewAsync(
        string folder,
        IProgress<SidecarMigrationItem>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<SidecarMigrationItem>>(() =>
        {
            var results = new List<SidecarMigrationItem>();
            Scan(Path.GetFullPath(folder));
            return results;

            void Scan(string directory)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    EnsureOrdinaryPath(directory);
                    var files = Directory.GetFiles(directory);
                    foreach (var path in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!Path.GetExtension(path).Equals(".xmp", StringComparison.OrdinalIgnoreCase)) continue;
                        var item = Inspect(path, files);
                        results.Add(item);
                        progress?.Report(item);
                    }
                    foreach (var child in Directory.EnumerateDirectories(directory))
                    {
                        if (Path.GetFileName(child).Equals(BackupFolderName, StringComparison.OrdinalIgnoreCase)) continue;
                        Scan(child);
                    }
                }
                catch (Exception ex) when (IsFileError(ex))
                {
                    var issue = new SidecarMigrationItem(directory, null,
                        SidecarMigrationState.Skipped, Reason(ex), Error: ex.Message);
                    results.Add(issue);
                    progress?.Report(issue);
                }
            }
        }, cancellationToken);

    public Task<IReadOnlyList<SidecarMigrationItem>> MigrateAsync(
        IReadOnlyList<SidecarMigrationItem> preview,
        IProgress<SidecarMigrationItem>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<SidecarMigrationItem>>(() =>
        {
            var results = new List<SidecarMigrationItem>();
            foreach (var item in preview)
            {
                var result = item.State != SidecarMigrationState.Ready ? item :
                    cancellationToken.IsCancellationRequested
                        ? item with { State = SidecarMigrationState.Canceled, Reason = "MigrationCanceled" }
                        : Migrate(item, cancellationToken);
                results.Add(result);
                progress?.Report(result);
            }
            return results;
        });

    private static SidecarMigrationItem Inspect(string sidecar, string[] files)
    {
        string? image = null;
        try
        {
            EnsureOrdinaryPath(sidecar);
            image = ResolveImage(sidecar, files);
            EnsureWritable(image);
            EnsureWritable(sidecar);
            using var media = File.Open(image, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var metadata = File.Open(sidecar, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = ReadBounded(media, MigrationXmpPacket.MaximumMediaBytes);
            var packet = ReadBounded(metadata, MigrationXmpPacket.MaximumPacketBytes);
            MigrationXmpPacket.EnsureMigratable(packet);
            var original = MigrationXmpPacket.Inspect(bytes, Path.GetExtension(image));
            var merged = MigrationXmpPacket.Merge(original.Packet, packet);
            var packetLimit = Path.GetExtension(image).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? MigrationXmpPacket.MaximumPacketBytes : 65504;
            if (Encoding.UTF8.GetByteCount(merged) > packetLimit)
            {
                throw new InvalidDataException("MigrationPacketTooLarge");
            }
            return new(sidecar, image, SidecarMigrationState.Ready, "MigrationReady", Hash(bytes), Hash(packet));
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            return new(sidecar, image, SidecarMigrationState.Skipped, Reason(ex), Error: ex.Message);
        }
    }

    private SidecarMigrationItem Migrate(SidecarMigrationItem item, CancellationToken token)
    {
        string? backup = null;
        string? staging = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var image = ResolveImage(item.SidecarPath);
            if (!string.Equals(image, item.ImagePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("MigrationChanged");
            EnsureWritable(image);
            EnsureWritable(item.SidecarPath);

            // Deny writes AND replacement for the entire transaction. Committing through this
            // handle preserves file identity and closes the check-then-replace race.
            using var media = File.Open(image, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var sidecar = OpenSidecarForDeletion(item.SidecarPath);
            var originalLastWrite = File.GetLastWriteTimeUtc(media.SafeFileHandle);
            var originalBytes = ReadBounded(media, MigrationXmpPacket.MaximumMediaBytes);
            var sidecarBytes = ReadBounded(sidecar, MigrationXmpPacket.MaximumPacketBytes);
            if (Hash(originalBytes) != item.ImageHash || Hash(sidecarBytes) != item.SidecarHash)
                throw new InvalidDataException("MigrationChanged");
            var original = MigrationXmpPacket.Inspect(originalBytes, Path.GetExtension(image));
            MigrationXmpPacket.EnsureMigratable(sidecarBytes);
            var merged = MigrationXmpPacket.Merge(original.Packet, sidecarBytes);
            token.ThrowIfCancellationRequested();

            var backupRoot = Path.Combine(Path.GetDirectoryName(image)!, BackupFolderName);
            if (Directory.Exists(backupRoot)) EnsureOrdinaryPath(backupRoot);
            backup = Path.Combine(backupRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            WriteDurable(Path.Combine(backup, Path.GetFileName(image) + ".original"), originalBytes);
            WriteDurable(Path.Combine(backup, Path.GetFileName(item.SidecarPath) + ".original"), sidecarBytes);

            // Reuse the established lossless JPEG/PNG writer, but only on a disposable staging file.
            staging = Path.Combine(backup, Path.GetFileName(image) + ".tmp");
            WriteDurable(staging, originalBytes);
            if (!FaceMetadataStore.TryWriteEmbedded(staging, merged, Path.GetExtension(image)))
                throw new InvalidDataException("MigrationUnsafePacket");
            var stagedBytes = File.ReadAllBytes(staging);
            Verify(stagedBytes);
            token.ThrowIfCancellationRequested();

            try
            {
                // Cancellation stops between files, never halfway through a commit/rollback.
                _writeMedia(media, stagedBytes);
                Verify(ReadBounded(media, MigrationXmpPacket.MaximumMediaBytes));
                // Request removal on the still-locked sidecar, only after on-disk read-back.
                // A deletion failure also restores the original media.
                MarkSidecarForDeletion(sidecar.SafeFileHandle);
            }
            catch (Exception writeError)
            {
                try
                {
                    Overwrite(media, originalBytes);
                    if (!ReadBounded(media, MigrationXmpPacket.MaximumMediaBytes).AsSpan().SequenceEqual(originalBytes))
                        throw new IOException("Original media read-back differs from the backup.");
                    File.SetLastWriteTimeUtc(media.SafeFileHandle, originalLastWrite);
                }
                catch (Exception restoreError)
                {
                    throw new IOException(
                        $"Recovery required. Original media and sidecar backups: {backup}",
                        new AggregateException(writeError, restoreError));
                }
                throw;
            }
            return item with { State = SidecarMigrationState.Migrated, Reason = "MigrationMigrated", BackupDirectory = backup };

            void Verify(byte[] candidate)
            {
                var contents = MigrationXmpPacket.Inspect(candidate, Path.GetExtension(image));
                if (!contents.NonXmpBytes.AsSpan().SequenceEqual(original.NonXmpBytes) ||
                    contents.Packet is null ||
                    !MigrationXmpPacket.Equivalent(Encoding.UTF8.GetBytes(merged), contents.Packet))
                    throw new InvalidDataException("MigrationVerificationFailed");
            }
        }
        catch (OperationCanceledException)
        {
            return item with { State = SidecarMigrationState.Canceled, Reason = "MigrationCanceled", BackupDirectory = backup };
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            return item with
            {
                State = SidecarMigrationState.Failed, Reason = Reason(ex),
                BackupDirectory = backup, Error = ex.ToString()
            };
        }
        finally
        {
            if (staging is not null && File.Exists(staging))
            {
                try { File.Delete(staging); }
                catch (Exception ex) when (IsFileError(ex))
                {
                    System.Diagnostics.Trace.TraceWarning($"[MIGRATION] Staging cleanup failed: {staging}: {ex}");
                }
            }
        }
    }

    private static string ResolveImage(string sidecar, string[]? knownFiles = null)
    {
        EnsureOrdinaryPath(sidecar);
        var directory = Path.GetDirectoryName(sidecar)!;
        var stem = Path.GetFileNameWithoutExtension(sidecar);
        var files = knownFiles ?? Directory.GetFiles(directory);
        // filename.ext.xmp is unambiguous. Legacy filename.xmp must have exactly one sibling.
        var exact = files.Where(p => Path.GetFileName(p).Equals(stem, StringComparison.OrdinalIgnoreCase)).ToArray();
        var matches = exact.Length > 0 ? exact : files.Where(p =>
            !Path.GetExtension(p).Equals(".xmp", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileNameWithoutExtension(p).Equals(stem, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("MigrationAmbiguous");
        var image = matches[0];
        var extension = Path.GetExtension(image).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".png"))
            throw new InvalidDataException("MigrationUnsupported");
        var associated = files.Where(p => p.Equals(image + ".xmp", StringComparison.OrdinalIgnoreCase) ||
            p.Equals(Path.ChangeExtension(image, ".xmp"), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (associated.Length != 1) throw new InvalidDataException("MigrationAmbiguous");
        EnsureOrdinaryPath(image);
        return image;
    }

    private static void EnsureOrdinaryPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("MigrationReparsePoint");
        }
    }

    private static void EnsureWritable(string path)
    {
        EnsureOrdinaryPath(path);
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException("Read-only file: " + path);
    }

    private static byte[] ReadBounded(FileStream stream, int maximum)
    {
        if (stream.Length > maximum) throw new InvalidDataException("MigrationPacketTooLarge");
        stream.Position = 0;
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void WriteDurable(string path, byte[] bytes)
    {
        using var stream = File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private static void Overwrite(FileStream stream, byte[] bytes)
    {
        stream.Position = 0;
        stream.Write(bytes);
        stream.SetLength(bytes.Length);
        stream.Flush(true);
    }

    private static bool IsFileError(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or XmlException or XmpException or Win32Exception;

    private static string Reason(Exception ex) =>
        ex is InvalidDataException && ex.Message.StartsWith("Migration", StringComparison.Ordinal)
            ? ex.Message : "MigrationFileError";

    private static FileStream OpenSidecarForDeletion(string path)
    {
        const uint genericRead = 0x80000000;
        const uint deleteAccess = 0x00010000;
        const uint shareRead = 1;
        const uint openExisting = 3;
        const uint normalAttributes = 0x80;
        var handle = CreateFile(path, genericRead | deleteAccess, shareRead,
            IntPtr.Zero, openExisting, normalAttributes, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return new FileStream(handle, FileAccess.Read);
    }

    private static void MarkSidecarForDeletion(SafeFileHandle handle)
    {
        const int fileDispositionInfo = 4;
        byte delete = 1;
        if (!SetFileInformationByHandle(handle, fileDispositionInfo, ref delete, sizeof(byte)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle handle, int informationClass, ref byte information, int size);
}
