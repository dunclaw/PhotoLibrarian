// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Data;

/// <summary>
/// Repository for tag CRUD operations against the SQLite cache.
/// </summary>
public sealed class TagRepository : IAutoTagStore
{
    private readonly CacheDatabase _db;

    public TagRepository(CacheDatabase db)
    {
        _db = db;
    }

    public async Task AddTagAsync(ImageTag tag)
    {
        using var conn = _db.CreateConnection();
        
        // Insert all tag paths (including parents)
        foreach (var tagPath in GetTagPaths(tag.Tag))
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

    private static IEnumerable<string> GetTagPaths(string tag)
    {
        if (!tag.Contains('/'))
        {
            yield return tag;
            yield break;
        }

        var currentPath = "";
        foreach (var part in tag.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = currentPath.Length == 0 ? part : $"{currentPath}/{part}";
            yield return currentPath;
        }
    }

    public async Task<List<ImageTag>> GetTagsAsync(
        long imageId,
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM tags WHERE image_id = $id ORDER BY confidence DESC";
        cmd.Parameters.AddWithValue("$id", imageId);

        var tags = new List<ImageTag>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
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

    public async Task SetAutoTagScanVersionAsync(
        long imageId,
        string scanVersion,
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE images
            SET auto_tag_scan_version = $scanVersion
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$scanVersion", scanVersion);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TryRestoreAutoTagsAsync(
        ImageEntry expectedImage,
        IReadOnlyCollection<ImageTag> expectedAutoTags,
        IReadOnlyCollection<ImageTag> previousAutoTags,
        string? previousScanVersion,
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var transaction = conn.BeginTransaction();
        var currentAutoTags = new List<ImageTag>();

        using (var read = conn.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT tag, confidence
                FROM tags
                WHERE image_id = $id AND source = $source
                """;
            read.Parameters.AddWithValue("$id", expectedImage.Id);
            read.Parameters.AddWithValue("$source", (int)TagSource.AutoML);
            using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                currentAutoTags.Add(new ImageTag
                {
                    ImageId = expectedImage.Id,
                    Tag = reader.GetString(0),
                    Source = TagSource.AutoML,
                    Confidence = reader.GetFloat(1)
                });
            }
        }

        if (!HaveSameAutoTags(currentAutoTags, expectedAutoTags))
        {
            transaction.Rollback();
            return false;
        }

        using (var mark = conn.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = """
                UPDATE images
                SET auto_tag_scan_version = $scanVersion
                WHERE id = $id
                  AND file_size = $fileSize
                  AND date_modified = $dateModified
                  AND auto_tag_scan_version IS NULL
                """;
            mark.Parameters.AddWithValue(
                "$scanVersion",
                previousScanVersion is null
                    ? DBNull.Value
                    : previousScanVersion);
            mark.Parameters.AddWithValue("$id", expectedImage.Id);
            mark.Parameters.AddWithValue("$fileSize", expectedImage.FileSize);
            mark.Parameters.AddWithValue(
                "$dateModified",
                expectedImage.DateModified.ToString("O"));
            if (await mark.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                transaction.Rollback();
                return false;
            }
        }

        using (var delete = conn.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM tags
                WHERE image_id = $id AND source = $source
                """;
            delete.Parameters.AddWithValue("$id", expectedImage.Id);
            delete.Parameters.AddWithValue("$source", (int)TagSource.AutoML);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var tag in previousAutoTags)
        {
            await InsertAutoTagAsync(
                conn,
                transaction,
                expectedImage.Id,
                tag.Tag,
                tag.Confidence,
                cancellationToken);
        }

        transaction.Commit();
        return true;
    }

    private static bool HaveSameAutoTags(
        IReadOnlyCollection<ImageTag> left,
        IReadOnlyCollection<ImageTag> right)
    {
        if (left.Count != right.Count)
            return false;

        var expected = right.ToDictionary(
            tag => tag.Tag,
            tag => tag.Confidence,
            StringComparer.OrdinalIgnoreCase);
        return left.All(tag =>
            expected.TryGetValue(tag.Tag, out var confidence) &&
            confidence.Equals(tag.Confidence));
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
    /// Library-wide count of items with no tag rows, regardless of tag source or hierarchy.
    /// This is the indexed equivalent of ImageRefinementFilter.IsUntagged.
    /// </summary>
    public async Task<int> GetNotTaggedCountAsync()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM images
            WHERE NOT EXISTS (SELECT 1 FROM tags WHERE tags.image_id = images.id)
            """;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<List<ImageEntry>> GetImagesNeedingAutoTagsAsync(
        string scanVersion,
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT *
            FROM images
            WHERE media_type = $imageMediaType
              AND COALESCE(auto_tag_scan_version, '') <> $scanVersion
            ORDER BY date_taken DESC
            """;
        cmd.Parameters.AddWithValue("$imageMediaType", (int)MediaType.Image);
        cmd.Parameters.AddWithValue("$scanVersion", scanVersion);

        var results = new List<ImageEntry>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ImageRepository.ReadImageEntry(reader));
        }

        return results;
    }

    public async Task<List<ImageEntry>> GetImagesWithAutoTagMetadataAsync(
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT images.*
            FROM images
            WHERE images.auto_tag_scan_version IS NOT NULL
               OR EXISTS (
                    SELECT 1
                    FROM tags
                    WHERE tags.image_id = images.id
                      AND tags.source = $source
               )
            """;
        cmd.Parameters.AddWithValue("$source", (int)TagSource.AutoML);

        var images = new List<ImageEntry>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            images.Add(ImageRepository.ReadImageEntry(reader));
        }

        return images;
    }

    public Task<bool> TryReplaceAutoTagsAsync(
        ImageEntry expectedImage,
        IReadOnlyCollection<GeneratedImageTag> tags,
        string? scanVersion,
        CancellationToken cancellationToken = default) =>
        TryReplaceAutoTagsCoreAsync(
            expectedImage,
            tags,
            scanVersion,
            null,
            null,
            cancellationToken);

    public Task<bool> TryReplaceAutoTagsIfUnchangedAsync(
        ImageEntry expectedImage,
        IReadOnlyCollection<GeneratedImageTag> tags,
        IReadOnlyCollection<ImageTag> expectedAutoTags,
        string? expectedScanVersion,
        CancellationToken cancellationToken = default) =>
        TryReplaceAutoTagsCoreAsync(
            expectedImage,
            tags,
            null,
            expectedAutoTags,
            expectedScanVersion,
            cancellationToken);

    private async Task<bool> TryReplaceAutoTagsCoreAsync(
        ImageEntry expectedImage,
        IReadOnlyCollection<GeneratedImageTag> tags,
        string? scanVersion,
        IReadOnlyCollection<ImageTag>? expectedAutoTags,
        string? expectedScanVersion,
        CancellationToken cancellationToken)
    {
        using var conn = _db.CreateConnection();
        using var transaction = conn.BeginTransaction();

        if (expectedAutoTags is not null)
        {
            var currentAutoTags = new List<ImageTag>();
            using var read = conn.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = """
                SELECT tag, confidence
                FROM tags
                WHERE image_id = $id AND source = $source
                """;
            read.Parameters.AddWithValue("$id", expectedImage.Id);
            read.Parameters.AddWithValue("$source", (int)TagSource.AutoML);
            using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                currentAutoTags.Add(new ImageTag
                {
                    ImageId = expectedImage.Id,
                    Tag = reader.GetString(0),
                    Source = TagSource.AutoML,
                    Confidence = reader.GetFloat(1)
                });
            }

            if (!HaveSameAutoTags(currentAutoTags, expectedAutoTags))
            {
                transaction.Rollback();
                return false;
            }
        }

        using (var mark = conn.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = """
                UPDATE images
                SET auto_tag_scan_version = $scanVersion
                WHERE id = $id
                  AND file_size = $fileSize
                  AND date_modified = $dateModified
                """;
            if (expectedAutoTags is not null)
                mark.CommandText += " AND auto_tag_scan_version IS $expectedScanVersion";
            mark.Parameters.AddWithValue(
                "$scanVersion",
                scanVersion is null ? DBNull.Value : scanVersion);
            if (expectedAutoTags is not null)
            {
                mark.Parameters.AddWithValue(
                    "$expectedScanVersion",
                    expectedScanVersion is null
                        ? DBNull.Value
                        : expectedScanVersion);
            }
            mark.Parameters.AddWithValue("$id", expectedImage.Id);
            mark.Parameters.AddWithValue("$fileSize", expectedImage.FileSize);
            mark.Parameters.AddWithValue(
                "$dateModified",
                expectedImage.DateModified.ToString("O"));
            if (await mark.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                transaction.Rollback();
                return false;
            }
        }

        using (var delete = conn.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM tags
                WHERE image_id = $id AND source = $source
                """;
            delete.Parameters.AddWithValue("$id", expectedImage.Id);
            delete.Parameters.AddWithValue("$source", (int)TagSource.AutoML);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        var normalizedTags = tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag.Tag))
            .Select(tag => new GeneratedImageTag(
                tag.Tag.Trim().Trim('/'),
                Math.Clamp(tag.Confidence, 0, 1)))
            .Where(tag => tag.Tag.Length > 0)
            .SelectMany(tag => GetTagPaths($"{ImageTag.AutomaticRootTag}/{tag.Tag}")
                .Select(path => new GeneratedImageTag(path, tag.Confidence)))
            .GroupBy(tag => tag.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.MaxBy(tag => tag.Confidence)!)
            .ToList();

        foreach (var tag in normalizedTags)
        {
            await InsertAutoTagAsync(
                conn,
                transaction,
                expectedImage.Id,
                tag.Tag,
                tag.Confidence,
                cancellationToken);
        }

        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Adds explicitly approved automatic tags without removing existing generated or
    /// user-created tags. The scan version records that this image was reviewed.
    /// </summary>
    public async Task<bool> TryAddReviewedAutoTagsAsync(
        ImageEntry expectedImage,
        IReadOnlyCollection<GeneratedImageTag> tags,
        string scanVersion,
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var transaction = conn.BeginTransaction();

        using (var mark = conn.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = """
                UPDATE images
                SET auto_tag_scan_version = $scanVersion
                WHERE id = $id
                  AND file_size = $fileSize
                  AND date_modified = $dateModified
                """;
            mark.Parameters.AddWithValue("$scanVersion", scanVersion);
            mark.Parameters.AddWithValue("$id", expectedImage.Id);
            mark.Parameters.AddWithValue("$fileSize", expectedImage.FileSize);
            mark.Parameters.AddWithValue(
                "$dateModified",
                expectedImage.DateModified.ToString("O"));
            if (await mark.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                transaction.Rollback();
                return false;
            }
        }

        var normalizedTags = tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag.Tag))
            .Select(tag => new GeneratedImageTag(
                tag.Tag.Trim().Trim('/'),
                Math.Clamp(tag.Confidence, 0, 1)))
            .Where(tag => tag.Tag.Length > 0)
            .SelectMany(tag => GetTagPaths(
                    $"{ImageTag.AutomaticRootTag}/{tag.Tag}")
                .Select(path => new GeneratedImageTag(path, tag.Confidence)))
            .GroupBy(tag => tag.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.MaxBy(tag => tag.Confidence)!)
            .ToList();

        foreach (var tag in normalizedTags)
        {
            await InsertAutoTagAsync(
                conn,
                transaction,
                expectedImage.Id,
                tag.Tag,
                tag.Confidence,
                cancellationToken);
        }

        transaction.Commit();
        return true;
    }

    public async Task<AutoTagRemovalResult> RemoveAllAutoTagsAsync(
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var transaction = conn.BeginTransaction();
        int photoCount;

        using (var count = conn.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = """
                SELECT COUNT(DISTINCT image_id)
                FROM tags
                WHERE source = $source
                """;
            count.Parameters.AddWithValue("$source", (int)TagSource.AutoML);
            photoCount = Convert.ToInt32(
                await count.ExecuteScalarAsync(cancellationToken));
        }

        int tagCount;
        using (var delete = conn.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM tags WHERE source = $source";
            delete.Parameters.AddWithValue("$source", (int)TagSource.AutoML);
            tagCount = await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var reset = conn.CreateCommand())
        {
            reset.Transaction = transaction;
            reset.CommandText = """
                UPDATE images
                SET auto_tag_scan_version = NULL
                WHERE auto_tag_scan_version IS NOT NULL
                """;
            await reset.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
        return new AutoTagRemovalResult(tagCount, photoCount);
    }

    private static async Task InsertAutoTagAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long imageId,
        string tag,
        float confidence,
        CancellationToken cancellationToken)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO tags (image_id, tag, source, confidence)
            VALUES ($id, $tag, $source, $confidence)
            ON CONFLICT(image_id, tag) DO UPDATE SET confidence = excluded.confidence
            WHERE tags.source = $source
            """;
        insert.Parameters.AddWithValue("$id", imageId);
        insert.Parameters.AddWithValue("$tag", tag);
        insert.Parameters.AddWithValue("$source", (int)TagSource.AutoML);
        insert.Parameters.AddWithValue("$confidence", confidence);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Imports file keywords without replacing a tag's existing source, such as a
    /// manually-created or reviewed automatic tag.
    /// </summary>
    public async Task AddMetadataTagAsync(
        long imageId,
        string tag,
        CancellationToken cancellationToken = default) =>
        await ImportTagAsync(
            imageId,
            tag,
            TagSource.Metadata,
            1.0f,
            cancellationToken);

    public async Task ReplaceTagsFromPortableMetadataAsync(
        long imageId,
        IReadOnlyCollection<ImageTag> tags,
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var transaction = conn.BeginTransaction();

        using (var delete = conn.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM tags WHERE image_id = $id";
            delete.Parameters.AddWithValue("$id", imageId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var tag in tags)
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO tags (image_id, tag, source, confidence)
                VALUES ($id, $tag, $source, $confidence)
                ON CONFLICT(image_id, tag) DO NOTHING
                """;
            insert.Parameters.AddWithValue("$id", imageId);
            insert.Parameters.AddWithValue("$tag", tag.Tag);
            insert.Parameters.AddWithValue("$source", (int)tag.Source);
            insert.Parameters.AddWithValue("$confidence", tag.Confidence);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    /// <summary>
    /// Imports file-side tag data without replacing an existing cached tag.
    /// </summary>
    public async Task ImportTagAsync(
        long imageId,
        string tag,
        TagSource source,
        float confidence,
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        foreach (var tagPath in GetTagPaths(tag))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tags (image_id, tag, source, confidence)
                VALUES ($id, $tag, $source, $confidence)
                ON CONFLICT(image_id, tag) DO NOTHING
                """;
            cmd.Parameters.AddWithValue("$id", imageId);
            cmd.Parameters.AddWithValue("$tag", tagPath);
            cmd.Parameters.AddWithValue("$source", (int)source);
            cmd.Parameters.AddWithValue("$confidence", confidence);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task RestoreAutoTagScanVersionAsync(
        long imageId,
        string? scanVersion,
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE images
            SET auto_tag_scan_version = $scanVersion
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue(
            "$scanVersion",
            (object?)scanVersion ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
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

public sealed record AutoTagRemovalResult(int TagCount, int PhotoCount);
