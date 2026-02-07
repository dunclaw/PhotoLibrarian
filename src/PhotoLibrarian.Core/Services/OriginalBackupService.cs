using System.Security.Cryptography;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Manages backup copies of original images before edits are applied.
/// Originals are stored in AppData keyed by file content hash.
/// </summary>
public sealed class OriginalBackupService
{
    private readonly string _backupDir;

    public OriginalBackupService(string? backupDirectory = null)
    {
        _backupDir = backupDirectory ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PhotoLibrarian", "Originals");
        Directory.CreateDirectory(_backupDir);
    }

    /// <summary>
    /// Backs up the original file before first edit. Returns true if backup was created,
    /// false if a backup already existed.
    /// </summary>
    public async Task<bool> BackupOriginalAsync(string filePath)
    {
        var hash = await ComputeFileHashAsync(filePath);
        var ext = Path.GetExtension(filePath);
        var backupPath = GetBackupPath(hash, ext);

        if (File.Exists(backupPath))
            return false; // Already backed up

        // Create subdirectory based on first 2 chars of hash
        var dir = Path.GetDirectoryName(backupPath)!;
        Directory.CreateDirectory(dir);

        await Task.Run(() => File.Copy(filePath, backupPath, overwrite: false));
        return true;
    }

    /// <summary>
    /// Checks if a backup exists for the given file.
    /// </summary>
    public async Task<bool> HasBackupAsync(string filePath)
    {
        var hash = await ComputeFileHashAsync(filePath);
        var ext = Path.GetExtension(filePath);
        return File.Exists(GetBackupPath(hash, ext));
    }

    /// <summary>
    /// Restores the original file, replacing the current edited version.
    /// </summary>
    public async Task<bool> RestoreOriginalAsync(string filePath, string originalHash)
    {
        var ext = Path.GetExtension(filePath);
        var backupPath = GetBackupPath(originalHash, ext);

        if (!File.Exists(backupPath))
            return false;

        await Task.Run(() => File.Copy(backupPath, filePath, overwrite: true));
        return true;
    }

    /// <summary>
    /// Gets the backup file path for a given original file.
    /// </summary>
    public async Task<string?> GetBackupPathAsync(string filePath)
    {
        var hash = await ComputeFileHashAsync(filePath);
        var ext = Path.GetExtension(filePath);
        var path = GetBackupPath(hash, ext);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Computes SHA-256 hash of the first 64KB of the file (fast fingerprint).
    /// </summary>
    public static async Task<string> ComputeFileHashAsync(string filePath)
    {
        const int chunkSize = 65536;
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);

        var buffer = new byte[Math.Min(chunkSize, stream.Length)];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));

        // Also include file size in hash for uniqueness
        var sizeBytes = BitConverter.GetBytes(stream.Length);
        sha.TransformBlock(buffer, 0, bytesRead, null, 0);
        sha.TransformFinalBlock(sizeBytes, 0, sizeBytes.Length);

        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>
    /// Gets total size of all backups in bytes.
    /// </summary>
    public long GetTotalBackupSize()
    {
        if (!Directory.Exists(_backupDir)) return 0;
        return new DirectoryInfo(_backupDir)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(f => f.Length);
    }

    /// <summary>
    /// Deletes all backups (frees disk space).
    /// </summary>
    public void ClearAllBackups()
    {
        if (Directory.Exists(_backupDir))
            Directory.Delete(_backupDir, recursive: true);
        Directory.CreateDirectory(_backupDir);
    }

    private string GetBackupPath(string hash, string extension)
    {
        // Use first 2 chars as subdirectory for filesystem performance
        var subDir = hash[..2];
        return Path.Combine(_backupDir, subDir, $"{hash}{extension}");
    }
}
