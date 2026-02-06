using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Data;

/// <summary>
/// Repository for thumbnail BLOB operations against the SQLite cache.
/// </summary>
public sealed class ThumbnailRepository
{
    private readonly CacheDatabase _db;

    public ThumbnailRepository(CacheDatabase db)
    {
        _db = db;
    }

    public async Task SaveThumbnailAsync(long imageId, ThumbnailSize size, byte[] data)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO thumbnails (image_id, size, data)
            VALUES ($id, $size, $data)
            """;
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$size", (int)size);
        cmd.Parameters.AddWithValue("$data", data);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<byte[]?> GetThumbnailAsync(long imageId, ThumbnailSize size)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM thumbnails WHERE image_id = $id AND size = $size";
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$size", (int)size);

        var result = await cmd.ExecuteScalarAsync();
        return result as byte[];
    }

    public async Task<bool> HasThumbnailAsync(long imageId, ThumbnailSize size)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM thumbnails WHERE image_id = $id AND size = $size";
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$size", (int)size);

        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result) > 0;
    }

    public async Task DeleteThumbnailsAsync(long imageId)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM thumbnails WHERE image_id = $id";
        cmd.Parameters.AddWithValue("$id", imageId);
        await cmd.ExecuteNonQueryAsync();
    }
}
