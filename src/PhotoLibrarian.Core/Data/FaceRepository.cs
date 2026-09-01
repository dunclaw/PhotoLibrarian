using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;

namespace PhotoLibrarian.Core.Data;

/// <summary>
/// Repository for face regions and person data against the SQLite cache.
/// </summary>
public sealed class FaceRepository : IFaceScanStore
{
    private readonly CacheDatabase _db;

    public FaceRepository(CacheDatabase db)
    {
        _db = db;
    }

    public async Task<long> AddFaceRegionAsync(FaceRegion face)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO face_regions (image_id, x, y, width, height, person_name, person_id, embedding, confidence)
            VALUES ($img, $x, $y, $w, $h, $name, $pid, $embed, $conf)
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("$img", face.ImageId);
        cmd.Parameters.AddWithValue("$x", face.X);
        cmd.Parameters.AddWithValue("$y", face.Y);
        cmd.Parameters.AddWithValue("$w", face.Width);
        cmd.Parameters.AddWithValue("$h", face.Height);
        cmd.Parameters.AddWithValue("$name", (object?)face.PersonName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pid", (object?)face.PersonId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$embed", face.Embedding is not null ? EmbeddingToBytes(face.Embedding) : DBNull.Value);
        cmd.Parameters.AddWithValue("$conf", face.Confidence);

        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<List<FaceRegion>> GetFacesForImageAsync(long imageId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM face_regions WHERE image_id = $id";
        cmd.Parameters.AddWithValue("$id", imageId);

        var faces = new List<FaceRegion>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            faces.Add(ReadFaceRegion(reader));
        }
        return faces;
    }

    public async Task RemapFaceRegionsAfterCropAsync(
        long imageId,
        uint sourceWidth,
        uint sourceHeight,
        CropRectangle crop,
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var transaction = conn.BeginTransaction();
        var faces = new List<FaceRegion>();

        using (var select = conn.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT * FROM face_regions WHERE image_id = $imageId";
            select.Parameters.AddWithValue("$imageId", imageId);
            using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                faces.Add(ReadFaceRegion(reader));
            }
        }

        foreach (var face in faces)
        {
            var remapped = CropMetadataRemapper.RemapFaceRegion(face, sourceWidth, sourceHeight, crop);
            using var command = conn.CreateCommand();
            command.Transaction = transaction;
            if (remapped is null)
            {
                command.CommandText = "DELETE FROM face_regions WHERE id = $id";
            }
            else
            {
                command.CommandText = """
                    UPDATE face_regions
                    SET x = $x, y = $y, width = $width, height = $height
                    WHERE id = $id
                    """;
                command.Parameters.AddWithValue("$x", remapped.X);
                command.Parameters.AddWithValue("$y", remapped.Y);
                command.Parameters.AddWithValue("$width", remapped.Width);
                command.Parameters.AddWithValue("$height", remapped.Height);
            }

            command.Parameters.AddWithValue("$id", face.Id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    public async Task<List<FaceRegion>> GetAllFacesWithEmbeddingsAsync()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM face_regions WHERE embedding IS NOT NULL";

        var faces = new List<FaceRegion>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            faces.Add(ReadFaceRegion(reader));
        }
        return faces;
    }

    public async Task<List<ImageEntry>> GetImagesNeedingFaceScanAsync(
        string scanVersion,
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT *
            FROM images
            WHERE media_type = $imageMediaType
              AND (face_scan_version IS NULL OR face_scan_version <> $scanVersion)
            ORDER BY id
            """;
        cmd.Parameters.AddWithValue("$imageMediaType", (int)MediaType.Image);
        cmd.Parameters.AddWithValue("$scanVersion", scanVersion);

        var images = new List<ImageEntry>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            images.Add(ImageRepository.ReadImageEntry(reader));
        }

        return images;
    }

    public async Task<bool> TryReplaceFaceRegionsAsync(
        long imageId,
        long expectedFileSize,
        DateTime expectedDateModified,
        IReadOnlyCollection<FaceRegion> faces,
        string scanVersion,
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var transaction = conn.BeginTransaction();

        using (var markComplete = conn.CreateCommand())
        {
            markComplete.Transaction = transaction;
            markComplete.CommandText = """
                UPDATE images
                SET face_scan_version = $scanVersion
                WHERE id = $imageId
                  AND file_size = $fileSize
                  AND date_modified = $dateModified
                """;
            markComplete.Parameters.AddWithValue("$scanVersion", scanVersion);
            markComplete.Parameters.AddWithValue("$imageId", imageId);
            markComplete.Parameters.AddWithValue("$fileSize", expectedFileSize);
            markComplete.Parameters.AddWithValue("$dateModified", expectedDateModified.ToString("O"));
            if (await markComplete.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                transaction.Rollback();
                return false;
            }
        }

        using (var delete = conn.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM face_regions WHERE image_id = $imageId";
            delete.Parameters.AddWithValue("$imageId", imageId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var face in faces)
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO face_regions
                    (image_id, x, y, width, height, person_name, person_id, embedding, confidence)
                VALUES
                    ($imageId, $x, $y, $width, $height, $personName, $personId, $embedding, $confidence)
                """;
            insert.Parameters.AddWithValue("$imageId", imageId);
            insert.Parameters.AddWithValue("$x", face.X);
            insert.Parameters.AddWithValue("$y", face.Y);
            insert.Parameters.AddWithValue("$width", face.Width);
            insert.Parameters.AddWithValue("$height", face.Height);
            insert.Parameters.AddWithValue("$personName", (object?)face.PersonName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$personId", (object?)face.PersonId ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "$embedding",
                face.Embedding is null ? DBNull.Value : EmbeddingToBytes(face.Embedding));
            insert.Parameters.AddWithValue("$confidence", face.Confidence);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
        return true;
    }

    public async Task UpdatePersonAsync(long faceId, long? personId, string? personName)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE face_regions SET person_id = $pid, person_name = $name WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", faceId);
        cmd.Parameters.AddWithValue("$pid", (object?)personId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", (object?)personName ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> CreatePersonAsync(string name)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO persons (name) VALUES ($name) RETURNING id";
        cmd.Parameters.AddWithValue("$name", name);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<List<Person>> GetAllPersonsAsync()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.id, p.name, p.thumbnail,
                   (SELECT COUNT(*) FROM face_regions fr WHERE fr.person_id = p.id) as face_count
            FROM persons p ORDER BY face_count DESC
            """;

        var persons = new List<Person>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            persons.Add(new Person
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                ThumbnailData = reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2),
                FaceCount = reader.GetInt32(3)
            });
        }
        return persons;
    }

    /// <summary>
    /// Loads named-person membership in one pass for client-side refinement composition.
    /// </summary>
    public async Task<Dictionary<long, HashSet<long>>> GetPersonIdsByImageIdAsync()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT image_id, person_id
            FROM face_regions
            WHERE person_id IS NOT NULL
            """;

        var results = new Dictionary<long, HashSet<long>>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var imageId = reader.GetInt64(0);
            if (!results.TryGetValue(imageId, out var personIds))
            {
                personIds = [];
                results.Add(imageId, personIds);
            }

            personIds.Add(reader.GetInt64(1));
        }

        return results;
    }

    public async Task MergePersonsAsync(long sourcePersonId, long targetPersonId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE face_regions SET person_id = $target WHERE person_id = $source";
        cmd.Parameters.AddWithValue("$source", sourcePersonId);
        cmd.Parameters.AddWithValue("$target", targetPersonId);
        await cmd.ExecuteNonQueryAsync();

        using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM persons WHERE id = $id";
        del.Parameters.AddWithValue("$id", sourcePersonId);
        await del.ExecuteNonQueryAsync();
    }

    private static FaceRegion ReadFaceRegion(SqliteDataReader reader)
    {
        var embedOrd = reader.GetOrdinal("embedding");
        float[]? embedding = null;
        if (!reader.IsDBNull(embedOrd))
        {
            var bytes = (byte[])reader.GetValue(embedOrd);
            embedding = BytesToEmbedding(bytes);
        }

        return new FaceRegion
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            ImageId = reader.GetInt64(reader.GetOrdinal("image_id")),
            X = reader.GetDouble(reader.GetOrdinal("x")),
            Y = reader.GetDouble(reader.GetOrdinal("y")),
            Width = reader.GetDouble(reader.GetOrdinal("width")),
            Height = reader.GetDouble(reader.GetOrdinal("height")),
            PersonName = reader.IsDBNull(reader.GetOrdinal("person_name")) ? null : reader.GetString(reader.GetOrdinal("person_name")),
            PersonId = reader.IsDBNull(reader.GetOrdinal("person_id")) ? null : reader.GetInt64(reader.GetOrdinal("person_id")),
            Embedding = embedding,
            Confidence = reader.GetFloat(reader.GetOrdinal("confidence"))
        };
    }

    private static byte[] EmbeddingToBytes(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BytesToEmbedding(byte[] bytes)
    {
        var embedding = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);
        return embedding;
    }
}
