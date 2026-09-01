using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Data;

/// <summary>
/// Repository for image CRUD operations against the SQLite cache.
/// </summary>
public sealed class ImageRepository
{
    private readonly CacheDatabase _db;

    public ImageRepository(CacheDatabase db)
    {
        _db = db;
    }

    public async Task<long> UpsertImageAsync(ImageEntry image)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO images (file_path, file_name, file_hash, file_size, width, height,
                                date_taken, date_modified, camera_make, camera_model, lens_model,
                                focal_length, aperture, exposure_time, iso, gps_latitude, gps_longitude,
                                rating, orientation, media_type, video_duration, is_flagged)
            VALUES ($path, $name, $hash, $size, $w, $h, $taken, $modified,
                    $make, $model, $lens, $focal, $aperture, $exposure, $iso,
                    $lat, $lon, $rating, $orient, $mediatype, $duration, $flagged)
            ON CONFLICT(file_path) DO UPDATE SET
                file_hash=excluded.file_hash, file_size=excluded.file_size,
                width=excluded.width, height=excluded.height,
                date_taken=excluded.date_taken, date_modified=excluded.date_modified,
                camera_make=excluded.camera_make, camera_model=excluded.camera_model,
                lens_model=excluded.lens_model, focal_length=excluded.focal_length,
                aperture=excluded.aperture, exposure_time=excluded.exposure_time,
                iso=excluded.iso, gps_latitude=excluded.gps_latitude,
                gps_longitude=excluded.gps_longitude, rating=excluded.rating,
                orientation=excluded.orientation, media_type=excluded.media_type,
                video_duration=excluded.video_duration,
                face_scan_version=CASE
                    WHEN images.file_size <> excluded.file_size
                      OR images.date_modified <> excluded.date_modified
                    THEN NULL
                    ELSE images.face_scan_version
                END
            RETURNING id;
            """;
        // Note: is_flagged is deliberately absent from the DO UPDATE SET. Flags can live in an XMP
        // sidecar the indexer doesn't read (RAW), so a re-scan must never clear an existing flag.

        cmd.Parameters.AddWithValue("$path", image.FilePath);
        cmd.Parameters.AddWithValue("$name", image.FileName);
        cmd.Parameters.AddWithValue("$hash", (object?)image.FileHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$size", image.FileSize);
        cmd.Parameters.AddWithValue("$w", image.Width);
        cmd.Parameters.AddWithValue("$h", image.Height);
        cmd.Parameters.AddWithValue("$taken", (object?)image.DateTaken?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$modified", image.DateModified.ToString("O"));
        cmd.Parameters.AddWithValue("$make", (object?)image.CameraMake ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)image.CameraModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lens", (object?)image.LensModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$focal", (object?)image.FocalLength ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$aperture", (object?)image.Aperture ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exposure", (object?)image.ExposureTime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$iso", (object?)image.Iso ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lat", (object?)image.GpsLatitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lon", (object?)image.GpsLongitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rating", (object?)image.Rating ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$orient", image.Orientation);
        cmd.Parameters.AddWithValue("$mediatype", (int)image.MediaType);
        cmd.Parameters.AddWithValue("$duration", (object?)image.VideoDuration ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$flagged", image.IsFlagged ? 1 : 0);

        var result = await cmd.ExecuteScalarAsync();
        return (long)result!;
    }

    public async Task<ImageEntry?> GetByPathAsync(string filePath)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM images WHERE file_path = $path";
        cmd.Parameters.AddWithValue("$path", filePath);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return ReadImageEntry(reader);
    }

    public async Task<List<ImageEntry>> GetAllAsync(string? orderBy = "date_taken", bool descending = true)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        var dir = descending ? "DESC" : "ASC";
        var validColumns = new HashSet<string> { "date_taken", "file_name", "date_modified", "rating", "file_size" };
        var col = validColumns.Contains(orderBy ?? "") ? orderBy : "date_taken";
        cmd.CommandText = $"SELECT * FROM images ORDER BY {col} {dir}";

        var results = new List<ImageEntry>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadImageEntry(reader));
        }
        return results;
    }

    public async Task<List<ImageEntry>> GetFilteredAsync(
        bool tagRootSelected,
        List<string>? tagFilters,
        string? orderBy = "date_taken",
        bool descending = true)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        var dir = descending ? "DESC" : "ASC";
        var validColumns = new HashSet<string> { "date_taken", "file_name", "date_modified", "rating", "file_size" };
        var col = validColumns.Contains(orderBy ?? "") ? orderBy : "date_taken";

        // Build WHERE clause for tag filtering
        if (tagRootSelected || (tagFilters is not null && tagFilters.Count > 0))
        {
            if (tagRootSelected)
            {
                // Show all images that have ANY tag
                cmd.CommandText = $@"
                    SELECT DISTINCT i.* FROM images i
                    INNER JOIN tags t ON i.id = t.image_id
                    ORDER BY i.{col} {dir}";
            }
            else
            {
                // Show images that have any of the selected tags (OR logic)
                // Because we store all parent paths, we can use simple equality (uses index!)
                // Example: Selecting "people/family" will match images with that exact tag entry,
                // which was inserted for images tagged with "people/family/kids" and its parents
                var conditions = new List<string>();
                for (int i = 0; i < tagFilters!.Count; i++)
                {
                    conditions.Add($"t.tag = $tag{i}");
                    cmd.Parameters.AddWithValue($"$tag{i}", tagFilters[i]);
                }

                var whereClause = string.Join(" OR ", conditions);
                cmd.CommandText = $@"
                    SELECT DISTINCT i.* FROM images i
                    INNER JOIN tags t ON i.id = t.image_id
                    WHERE {whereClause}
                    ORDER BY i.{col} {dir}";
            }
        }
        else
        {
            // No tag filtering
            cmd.CommandText = $"SELECT * FROM images ORDER BY {col} {dir}";
        }

        var results = new List<ImageEntry>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadImageEntry(reader));
        }
        return results;
    }

    public async Task<int> GetCountAsync()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM images";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task DeleteByPathAsync(string filePath)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM images WHERE file_path = $path";
        cmd.Parameters.AddWithValue("$path", filePath);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateRatingAsync(long imageId, int? rating)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE images SET rating = $rating WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$rating", (object?)rating ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Sets or clears the user flag on a single image.</summary>
    public async Task UpdateFlagAsync(long imageId, bool isFlagged)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE images SET is_flagged = $flagged WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$flagged", isFlagged ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Number of images currently flagged — drives the "Flagged" node's count.</summary>
    public async Task<int> GetFlaggedCountAsync()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM images WHERE is_flagged = 1";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task UpdateDateTakenAsync(long imageId, DateTime? dateTaken)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE images SET date_taken = $taken WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$taken", (object?)dateTaken?.ToString("O") ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateOrientationAsync(long imageId, int orientation)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE images SET orientation = $orient WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$orient", orientation);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateDimensionsAsync(long imageId, int width, int height, long fileSize)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE images
            SET width = $w,
                height = $h,
                file_size = $size,
                orientation = 1,
                face_scan_version = NULL
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$w", width);
        cmd.Parameters.AddWithValue("$h", height);
        cmd.Parameters.AddWithValue("$size", fileSize);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdatePathAsync(long imageId, string newPath)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE images SET file_path = $path, file_name = $name WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$path", newPath);
        cmd.Parameters.AddWithValue("$name", System.IO.Path.GetFileName(newPath));
        await cmd.ExecuteNonQueryAsync();
    }

    internal static ImageEntry ReadImageEntry(SqliteDataReader reader)
    {
        return new ImageEntry
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            FilePath = reader.GetString(reader.GetOrdinal("file_path")),
            FileName = reader.GetString(reader.GetOrdinal("file_name")),
            FileHash = reader.IsDBNull(reader.GetOrdinal("file_hash")) ? null : reader.GetString(reader.GetOrdinal("file_hash")),
            FileSize = reader.GetInt64(reader.GetOrdinal("file_size")),
            Width = reader.GetInt32(reader.GetOrdinal("width")),
            Height = reader.GetInt32(reader.GetOrdinal("height")),
            DateTaken = reader.IsDBNull(reader.GetOrdinal("date_taken")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("date_taken"))),
            DateModified = DateTime.Parse(reader.GetString(reader.GetOrdinal("date_modified"))),
            DateIndexed = DateTime.Parse(reader.GetString(reader.GetOrdinal("date_indexed"))),
            FaceScanVersion = ReadNullableString(reader, "face_scan_version"),
            CameraMake = reader.IsDBNull(reader.GetOrdinal("camera_make")) ? null : reader.GetString(reader.GetOrdinal("camera_make")),
            CameraModel = reader.IsDBNull(reader.GetOrdinal("camera_model")) ? null : reader.GetString(reader.GetOrdinal("camera_model")),
            LensModel = reader.IsDBNull(reader.GetOrdinal("lens_model")) ? null : reader.GetString(reader.GetOrdinal("lens_model")),
            FocalLength = reader.IsDBNull(reader.GetOrdinal("focal_length")) ? null : reader.GetDouble(reader.GetOrdinal("focal_length")),
            Aperture = reader.IsDBNull(reader.GetOrdinal("aperture")) ? null : reader.GetDouble(reader.GetOrdinal("aperture")),
            ExposureTime = reader.IsDBNull(reader.GetOrdinal("exposure_time")) ? null : reader.GetString(reader.GetOrdinal("exposure_time")),
            Iso = reader.IsDBNull(reader.GetOrdinal("iso")) ? null : reader.GetInt32(reader.GetOrdinal("iso")),
            GpsLatitude = reader.IsDBNull(reader.GetOrdinal("gps_latitude")) ? null : reader.GetDouble(reader.GetOrdinal("gps_latitude")),
            GpsLongitude = reader.IsDBNull(reader.GetOrdinal("gps_longitude")) ? null : reader.GetDouble(reader.GetOrdinal("gps_longitude")),
            Rating = reader.IsDBNull(reader.GetOrdinal("rating")) ? null : reader.GetInt32(reader.GetOrdinal("rating")),
            Orientation = reader.GetInt32(reader.GetOrdinal("orientation")),
            MediaType = (MediaType)reader.GetInt32(reader.GetOrdinal("media_type")),
            VideoDuration = reader.IsDBNull(reader.GetOrdinal("video_duration")) ? null : reader.GetDouble(reader.GetOrdinal("video_duration")),
            IsFlagged = ReadBoolean(reader, "is_flagged")
        };
    }

    private static bool ReadBoolean(SqliteDataReader reader, string column)
    {
        int ordinal;
        try
        {
            ordinal = reader.GetOrdinal(column);
        }
        catch (IndexOutOfRangeException)
        {
            return false; // column added by a later migration than the one that created this reader
        }
        return !reader.IsDBNull(ordinal) && reader.GetInt32(ordinal) != 0;
    }

    private static string? ReadNullableString(SqliteDataReader reader, string column)
    {
        try
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    public async Task<List<string>> GetImageTagsAsync(long imageId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tag FROM tags WHERE image_id = $id";
        cmd.Parameters.AddWithValue("$id", imageId);

        var tags = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tags.Add(reader.GetString(0));
        }
        return tags;
    }

    public async Task<Dictionary<long, List<string>>> GetImageTagsForMultipleAsync(List<long> imageIds)
    {
        var result = new Dictionary<long, List<string>>();
        
        if (imageIds.Count == 0)
            return result;

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        
        // Build query with IN clause
        var placeholders = string.Join(",", imageIds.Select((_, i) => $"$id{i}"));
        cmd.CommandText = $"SELECT image_id, tag FROM tags WHERE image_id IN ({placeholders})";
        
        for (int i = 0; i < imageIds.Count; i++)
        {
            cmd.Parameters.AddWithValue($"$id{i}", imageIds[i]);
        }

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var imageId = reader.GetInt64(0);
            var tag = reader.GetString(1);
            
            if (!result.ContainsKey(imageId))
                result[imageId] = new List<string>();
            
            result[imageId].Add(tag);
        }
        
        return result;
    }
}
