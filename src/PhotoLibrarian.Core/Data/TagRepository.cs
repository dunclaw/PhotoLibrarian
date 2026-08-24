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
        using var conn = _db.CreateConnection();
        
        // For hierarchical tags like "people/family/kids", insert all parent paths too
        // This allows efficient index-based queries for parent tags
        var tagsToInsert = new List<string>();
        
        if (tag.Tag.Contains('/'))
        {
            var parts = tag.Tag.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string currentPath = "";
            
            foreach (var part in parts)
            {
                currentPath = string.IsNullOrEmpty(currentPath) ? part : $"{currentPath}/{part}";
                tagsToInsert.Add(currentPath);
            }
        }
        else
        {
            tagsToInsert.Add(tag.Tag);
        }
        
        // Insert all tag paths (including parents)
        foreach (var tagPath in tagsToInsert)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO tags (image_id, tag, source, confidence)
                VALUES ($id, $tag, $source, $conf)
                """;
            cmd.Parameters.AddWithValue("$id", tag.ImageId);
            cmd.Parameters.AddWithValue("$tag", tagPath);
            cmd.Parameters.AddWithValue("$source", (int)tag.Source);
            cmd.Parameters.AddWithValue("$conf", tag.Confidence);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task<List<ImageTag>> GetTagsAsync(long imageId)
    {
        using var conn = _db.CreateConnection();
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
        using var conn = _db.CreateConnection();
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
        using var conn = _db.CreateConnection();
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
        using var conn = _db.CreateConnection();
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
    /// Loads tag membership in one pass for client-side refinement composition.
    /// </summary>
    public async Task<Dictionary<long, HashSet<string>>> GetTagsByImageIdAsync()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT image_id, tag FROM tags";

        var results = new Dictionary<long, HashSet<string>>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var imageId = reader.GetInt64(0);
            if (!results.TryGetValue(imageId, out var tags))
            {
                tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                results.Add(imageId, tags);
            }

            tags.Add(reader.GetString(1));
        }

        return results;
    }

    /// <summary>
    /// Renames a tag across all images.
    /// </summary>
    public async Task RenameTagAsync(string oldTag, string newTag)
    {
        using var conn = _db.CreateConnection();
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
        using var conn = _db.CreateConnection();

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
