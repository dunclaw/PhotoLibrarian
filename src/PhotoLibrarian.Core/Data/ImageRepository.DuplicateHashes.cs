// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using System.Globalization;

namespace PhotoLibrarian.Core.Data;

public sealed partial class ImageRepository
{
    public async Task<Dictionary<string, DuplicateHashCacheEntry>> GetDuplicateHashesAsync(
        CancellationToken cancellationToken)
    {
        using var connection = _db.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.file_path, c.file_size, c.modified_ticks, c.sha256, c.perceptual_hash
            FROM duplicate_hash_cache c JOIN images i ON i.id = c.image_id
            WHERE c.hash_version = 1
            """;
        var results = new Dictionary<string, DuplicateHashCacheEntry>(
            StringComparer.OrdinalIgnoreCase);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetString(0), new DuplicateHashCacheEntry(
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4)
                    ? null
                    : ulong.Parse(reader.GetString(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        }
        return results;
    }

    public async Task SaveDuplicateHashesAsync(
        IReadOnlyList<DuplicateFileSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        using var connection = _db.CreateConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO duplicate_hash_cache
                (image_id, file_size, modified_ticks, sha256, perceptual_hash, hash_version)
            SELECT id, $size, $ticks, $sha, $perceptual, 1 FROM images WHERE id = $id
            ON CONFLICT(image_id) DO UPDATE SET
                file_size = excluded.file_size, modified_ticks = excluded.modified_ticks,
                sha256 = excluded.sha256, perceptual_hash = excluded.perceptual_hash,
                hash_version = excluded.hash_version
            """;
        var id = command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
        var size = command.Parameters.Add("$size", Microsoft.Data.Sqlite.SqliteType.Integer);
        var ticks = command.Parameters.Add("$ticks", Microsoft.Data.Sqlite.SqliteType.Integer);
        var sha = command.Parameters.Add("$sha", Microsoft.Data.Sqlite.SqliteType.Text);
        var perceptual = command.Parameters.Add("$perceptual", Microsoft.Data.Sqlite.SqliteType.Text);
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            id.Value = snapshot.Entry.Id;
            size.Value = snapshot.FileSize;
            ticks.Value = snapshot.LastWriteTimeUtc.Ticks;
            sha.Value = (object?)snapshot.Sha256 ?? DBNull.Value;
            perceptual.Value = (object?)snapshot.PerceptualHash?.ToString("X16", CultureInfo.InvariantCulture)
                ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
    }
}

public sealed record DuplicateHashCacheEntry(
    long FileSize,
    long ModifiedTicks,
    string? Sha256,
    ulong? PerceptualHash);
