using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Data;

/// <summary>
/// Repository for tag CRUD operations against the SQLite cache.
/// </summary>
public sealed class TagRepository
{
    private readonly CacheDatabase _db;

    public TagRepository(CacheDatabase db)
    {
        _db = db;
    }

    public async Task AddTagAsync(ImageTag tag)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO tags (image_id, tag, source, confidence)
            VALUES ($id, $tag, $source, $conf)
            """;
        cmd.Parameters.AddWithValue("$id", tag.ImageId);
        cmd.Parameters.AddWithValue("$tag", tag.Tag);
        cmd.Parameters.AddWithValue("$source", (int)tag.Source);
        cmd.Parameters.AddWithValue("$conf", tag.Confidence);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<ImageTag>> GetTagsAsync(long imageId)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM tags WHERE image_id = $id ORDER BY confidence DESC";
        cmd.Parameters.AddWithValue("$id", imageId);

        var tags = new List<ImageTag>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tags.Add(new ImageTag
            {
                ImageId = reader.GetInt64(0),
                Tag = reader.GetString(1),
                Source = (TagSource)reader.GetInt32(2),
                Confidence = reader.GetFloat(3)
            });
        }
        return tags;
    }

    public async Task RemoveTagAsync(long imageId, string tag)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tags WHERE image_id = $id AND tag = $tag";
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$tag", tag);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Gets all unique tags with their usage count.
    /// </summary>
    public async Task<List<(string Tag, int Count)>> GetAllTagsWithCountAsync()
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tag, COUNT(*) as cnt FROM tags GROUP BY tag ORDER BY cnt DESC";

        var results = new List<(string, int)>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((reader.GetString(0), reader.GetInt32(1)));
        }
        return results;
    }

    /// <summary>
    /// Finds images that have a specific tag.
    /// </summary>
    public async Task<List<long>> GetImageIdsWithTagAsync(string tag)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT image_id FROM tags WHERE tag = $tag";
        cmd.Parameters.AddWithValue("$tag", tag);

        var ids = new List<long>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    /// <summary>
    /// Renames a tag across all images.
    /// </summary>
    public async Task RenameTagAsync(string oldTag, string newTag)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tags SET tag = $new WHERE tag = $old";
        cmd.Parameters.AddWithValue("$old", oldTag);
        cmd.Parameters.AddWithValue("$new", newTag);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Merges one tag into another (rename + deduplicate).
    /// </summary>
    public async Task MergeTagsAsync(string sourceTag, string targetTag)
    {
        var conn = _db.GetConnection();

        // Delete where target already exists to avoid duplicates
        using var del = conn.CreateCommand();
        del.CommandText = """
            DELETE FROM tags WHERE tag = $source 
            AND image_id IN (SELECT image_id FROM tags WHERE tag = $target)
            """;
        del.Parameters.AddWithValue("$source", sourceTag);
        del.Parameters.AddWithValue("$target", targetTag);
        await del.ExecuteNonQueryAsync();

        // Rename remaining
        await RenameTagAsync(sourceTag, targetTag);
    }
}
