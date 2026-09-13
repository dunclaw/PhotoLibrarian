using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;

namespace PhotoLibrarian.Core.Data;

/// <summary>
/// Repository for face regions and person data against the SQLite cache.
/// </summary>
public sealed class FaceRepository : IFaceScanStore
{
    private const double FaceRegionMatchThreshold = 0.5;
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
            INSERT INTO face_regions
                (image_id, x, y, width, height, person_name, person_id,
                 embedding, confidence, metadata_managed)
            VALUES
                ($img, $x, $y, $w, $h, $name, $pid, $embed, $conf,
                 $metadataManaged)
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
        cmd.Parameters.AddWithValue(
            "$metadataManaged",
            face.IsMetadataManaged ? 1 : 0);

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
                command.CommandText = """
                    UPDATE persons
                    SET representative_face_region_id = NULL
                    WHERE representative_face_region_id = $id;
                    DELETE FROM face_regions WHERE id = $id;
                    """;
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

    public async Task<List<FaceRegion>> GetAllFacesWithEmbeddingsAsync(
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM face_regions WHERE embedding IS NOT NULL";

        var faces = new List<FaceRegion>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            faces.Add(ReadFaceRegion(reader));
        }
        return faces;
    }

    public async Task<Dictionary<long, string>> GetFaceImagePathsAsync(
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT i.id, i.file_path
            FROM images i
            JOIN face_regions fr ON fr.image_id = i.id
            """;

        var paths = new Dictionary<long, string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            paths.Add(reader.GetInt64(0), reader.GetString(1));
        }
        return paths;
    }

    public async Task<List<FaceRegion>> GetFaceRegionsForManagementAsync(
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT id, image_id, x, y, width, height, person_name, person_id,
                   NULL AS embedding, confidence, metadata_managed
            FROM face_regions
            ORDER BY id
            """;

        var faces = new List<FaceRegion>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
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

    public async Task<List<ImageEntry>>
        GetImagesRequiringFaceMetadataExportAsync(
            CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT *
            FROM images
            WHERE face_metadata_export_required = 1
            ORDER BY id
            """;
        var images = new List<ImageEntry>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            images.Add(ImageRepository.ReadImageEntry(reader));
        }
        return images;
    }

    public async Task SetFaceMetadataImportedAsync(
        long imageId,
        bool imported,
        string? sidecarPath = null,
        long? sidecarSize = null,
        DateTime? sidecarModified = null,
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = """
            UPDATE images
            SET face_metadata_imported = $imported,
                face_sidecar_path = CASE
                    WHEN $imported = 1 THEN $sidecarPath
                    ELSE face_sidecar_path
                END,
                face_sidecar_size = CASE
                    WHEN $imported = 1 THEN $sidecarSize
                    ELSE face_sidecar_size
                END,
                face_sidecar_modified = CASE
                    WHEN $imported = 1 THEN $sidecarModified
                    ELSE face_sidecar_modified
                END,
                date_indexed = CASE
                    WHEN $imported = 1 THEN datetime('now')
                    ELSE date_indexed
                END
            WHERE id = $imageId
            """;
        command.Parameters.AddWithValue("$imageId", imageId);
        command.Parameters.AddWithValue("$imported", imported ? 1 : 0);
        command.Parameters.AddWithValue(
            "$sidecarPath",
            (object?)sidecarPath ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$sidecarSize",
            (object?)sidecarSize ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$sidecarModified",
            (object?)sidecarModified?.ToString("O") ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "The photo no longer exists in the cache.");
        }
    }

    public async Task SetFaceMetadataExportRequiredAsync(
        long imageId,
        bool required,
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = """
            UPDATE images
            SET face_metadata_export_required = $required
            WHERE id = $imageId
            """;
        command.Parameters.AddWithValue("$imageId", imageId);
        command.Parameters.AddWithValue("$required", required ? 1 : 0);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "The photo no longer exists in the cache.");
        }
    }

    public async Task<PhotoFaceMetadata> GetPhotoFaceMetadataAsync(
        long imageId,
        int imageWidth,
        int imageHeight,
        CancellationToken cancellationToken = default)
    {
        var faces = await GetFacesForImageAsync(imageId);
        var people = (await GetAllPersonsAsync()).ToDictionary(
            person => person.Id);
        var rejections = await GetRejectedPersonIdsByFaceIdAsync(
            cancellationToken);
        var hiddenFaceIds = await GetHiddenFaceSuggestionIdsAsync(
            cancellationToken);
        return new PhotoFaceMetadata(
            imageWidth,
            imageHeight,
            faces.Select(face => new PortableFaceMetadata(
                    face.Id,
                    face.X,
                    face.Y,
                    face.Width,
                    face.Height,
                    face.PersonName,
                    hiddenFaceIds.Contains(face.Id),
                    face.PersonId is long personId &&
                    people.TryGetValue(personId, out var person) &&
                    person.SuggestionsHidden,
                    rejections.GetValueOrDefault(face.Id)?
                        .Where(people.ContainsKey)
                        .Select(personId => people[personId].Name)
                        .ToArray() ??
                    []))
                .ToList());
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

        var existingFaces = new List<FaceRegion>();
        using (var select = conn.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                "SELECT * FROM face_regions WHERE image_id = $imageId";
            select.Parameters.AddWithValue("$imageId", imageId);
            using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingFaces.Add(ReadFaceRegion(reader));
            }
        }

        var incomingFaces = faces.ToList();
        var matches = MatchExistingFaces(existingFaces, incomingFaces);
        var retainedFaceIds = matches.Values
            .Select(face => face.Id)
            .ToHashSet();

        for (var index = 0; index < incomingFaces.Count; index++)
        {
            var face = incomingFaces[index];
            if (matches.TryGetValue(index, out var existingFace))
            {
                using var update = conn.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE face_regions
                    SET x = $x, y = $y, width = $width, height = $height,
                        person_name = $personName, person_id = $personId,
                        embedding = $embedding, confidence = $confidence,
                        metadata_managed = $metadataManaged
                    WHERE id = $id
                    """;
                update.Parameters.AddWithValue("$id", existingFace.Id);
                update.Parameters.AddWithValue("$x", face.X);
                update.Parameters.AddWithValue("$y", face.Y);
                update.Parameters.AddWithValue("$width", face.Width);
                update.Parameters.AddWithValue("$height", face.Height);
                update.Parameters.AddWithValue(
                    "$personName",
                    (object?)(existingFace.PersonId.HasValue
                        ? existingFace.PersonName
                        : face.PersonName) ?? DBNull.Value);
                update.Parameters.AddWithValue(
                    "$personId",
                    (object?)(existingFace.PersonId ?? face.PersonId) ?? DBNull.Value);
                update.Parameters.AddWithValue(
                    "$embedding",
                    face.Embedding is null
                        ? DBNull.Value
                        : EmbeddingToBytes(face.Embedding));
                update.Parameters.AddWithValue("$confidence", face.Confidence);
                update.Parameters.AddWithValue(
                    "$metadataManaged",
                    face.IsMetadataManaged ? 1 : 0);
                await update.ExecuteNonQueryAsync(cancellationToken);
                continue;
            }

            using var insert = conn.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO face_regions
                    (image_id, x, y, width, height, person_name, person_id,
                     embedding, confidence, metadata_managed)
                VALUES
                    ($imageId, $x, $y, $width, $height, $personName, $personId,
                     $embedding, $confidence, $metadataManaged)
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
            insert.Parameters.AddWithValue(
                "$metadataManaged",
                face.IsMetadataManaged ? 1 : 0);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var delete = conn.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText =
                "DELETE FROM face_regions WHERE id = $id";
            var id = delete.Parameters.Add("$id", SqliteType.Integer);
            delete.Prepare();
            foreach (var existingFace in existingFaces)
            {
                if (retainedFaceIds.Contains(existingFace.Id)) continue;

                id.Value = existingFace.Id;
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        transaction.Commit();
        return true;
    }

    public async Task ImportFaceMetadataAsync(
        long imageId,
        PhotoFaceMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var transaction = conn.BeginTransaction();
        var existingFaces = new List<FaceRegion>();
        using (var select = conn.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                "SELECT * FROM face_regions WHERE image_id = $imageId";
            select.Parameters.AddWithValue("$imageId", imageId);
            using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingFaces.Add(ReadFaceRegion(reader));
            }
        }

        var personNames = metadata.Faces
            .SelectMany(face =>
                face.RejectedPersonNames.Append(face.PersonName ?? ""))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var personIds = new Dictionary<string, long>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var personName in personNames)
        {
            personIds[personName] = await GetOrCreatePersonIdAsync(
                conn,
                transaction,
                personName,
                cancellationToken);
        }

        foreach (var group in metadata.Faces
                     .Where(face => !string.IsNullOrWhiteSpace(face.PersonName))
                     .GroupBy(face => face.PersonName!, StringComparer.OrdinalIgnoreCase))
        {
            using var updatePerson = conn.CreateCommand();
            updatePerson.Transaction = transaction;
            updatePerson.CommandText = """
                UPDATE persons
                SET suggestions_hidden = $hidden
                WHERE id = $personId
                """;
            updatePerson.Parameters.AddWithValue(
                "$hidden",
                group.Any(face => face.PersonSuggestionsHidden) ? 1 : 0);
            updatePerson.Parameters.AddWithValue(
                "$personId",
                personIds[group.Key]);
            await updatePerson.ExecuteNonQueryAsync(cancellationToken);
        }

        var importedFaces = metadata.Faces
            .Select(face => new FaceRegion
            {
                ImageId = imageId,
                X = face.X,
                Y = face.Y,
                Width = face.Width,
                Height = face.Height,
                PersonName = face.PersonName
            })
            .ToList();
        var matches = MatchExistingFaces(existingFaces, importedFaces);
        for (var index = 0; index < metadata.Faces.Count; index++)
        {
            var imported = metadata.Faces[index];
            var personId = imported.PersonName is null
                ? (long?)null
                : personIds[imported.PersonName];
            long faceId;
            if (matches.TryGetValue(index, out var existing))
            {
                faceId = existing.Id;
                using var update = conn.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE face_regions
                    SET x = $x, y = $y, width = $width, height = $height,
                        person_name = $personName, person_id = $personId,
                        embedding = NULL, metadata_managed = 1
                    WHERE id = $faceId
                    """;
                update.Parameters.AddWithValue("$faceId", faceId);
                update.Parameters.AddWithValue("$x", imported.X);
                update.Parameters.AddWithValue("$y", imported.Y);
                update.Parameters.AddWithValue("$width", imported.Width);
                update.Parameters.AddWithValue("$height", imported.Height);
                update.Parameters.AddWithValue(
                    "$personName",
                    (object?)imported.PersonName ?? DBNull.Value);
                update.Parameters.AddWithValue(
                    "$personId",
                    (object?)personId ?? DBNull.Value);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                using var insert = conn.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO face_regions
                        (image_id, x, y, width, height, person_name, person_id,
                         embedding, confidence, metadata_managed)
                    VALUES
                        ($imageId, $x, $y, $width, $height, $personName, $personId,
                         NULL, 0, 1)
                    RETURNING id
                    """;
                insert.Parameters.AddWithValue("$imageId", imageId);
                insert.Parameters.AddWithValue("$x", imported.X);
                insert.Parameters.AddWithValue("$y", imported.Y);
                insert.Parameters.AddWithValue("$width", imported.Width);
                insert.Parameters.AddWithValue("$height", imported.Height);
                insert.Parameters.AddWithValue(
                    "$personName",
                    (object?)imported.PersonName ?? DBNull.Value);
                insert.Parameters.AddWithValue(
                    "$personId",
                    (object?)personId ?? DBNull.Value);
                faceId = (long)(await insert.ExecuteScalarAsync(
                    cancellationToken))!;
            }

            using (var clearReview = conn.CreateCommand())
            {
                clearReview.Transaction = transaction;
                clearReview.CommandText = """
                    DELETE FROM face_rejections WHERE face_region_id = $faceId;
                    DELETE FROM hidden_face_suggestions
                    WHERE face_region_id = $faceId;
                    """;
                clearReview.Parameters.AddWithValue("$faceId", faceId);
                await clearReview.ExecuteNonQueryAsync(cancellationToken);
            }

            if (imported.SuggestionsHidden)
            {
                using var hide = conn.CreateCommand();
                hide.Transaction = transaction;
                hide.CommandText = """
                    INSERT INTO hidden_face_suggestions (face_region_id)
                    VALUES ($faceId)
                    """;
                hide.Parameters.AddWithValue("$faceId", faceId);
                await hide.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var rejectedName in imported.RejectedPersonNames
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!personIds.TryGetValue(rejectedName, out var rejectedPersonId))
                {
                    continue;
                }

                using var reject = conn.CreateCommand();
                reject.Transaction = transaction;
                reject.CommandText = """
                    INSERT OR IGNORE INTO face_rejections
                        (face_region_id, person_id)
                    VALUES ($faceId, $personId)
                    """;
                reject.Parameters.AddWithValue("$faceId", faceId);
                reject.Parameters.AddWithValue("$personId", rejectedPersonId);
                await reject.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        var matchedFaceIds = matches.Values
            .Select(face => face.Id)
            .ToHashSet();
        foreach (var existing in existingFaces)
        {
            if (matchedFaceIds.Contains(existing.Id)) continue;

            using var clearPortableState = conn.CreateCommand();
            clearPortableState.Transaction = transaction;
            clearPortableState.CommandText = """
                UPDATE face_regions
                SET person_id = NULL, person_name = NULL,
                    metadata_managed = 0
                WHERE id = $faceId;
                DELETE FROM face_rejections WHERE face_region_id = $faceId;
                DELETE FROM hidden_face_suggestions
                WHERE face_region_id = $faceId;
                """;
            clearPortableState.Parameters.AddWithValue("$faceId", existing.Id);
            await clearPortableState.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var invalidateScan = conn.CreateCommand())
        {
            invalidateScan.Transaction = transaction;
            invalidateScan.CommandText = """
                UPDATE images
                SET face_scan_version = NULL
                WHERE id = $imageId
                """;
            invalidateScan.Parameters.AddWithValue("$imageId", imageId);
            await invalidateScan.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var removeOrphanPeople = conn.CreateCommand())
        {
            removeOrphanPeople.Transaction = transaction;
            removeOrphanPeople.CommandText = """
                DELETE FROM persons
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM face_regions
                    WHERE face_regions.person_id = persons.id
                )
                  AND NOT EXISTS (
                    SELECT 1
                    FROM face_rejections
                    WHERE face_rejections.person_id = persons.id
                )
                """;
            await removeOrphanPeople.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    private static async Task<long> GetOrCreatePersonIdAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string personName,
        CancellationToken cancellationToken)
    {
        using (var select = conn.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT id
                FROM persons
                WHERE name = $name COLLATE NOCASE
                LIMIT 1
                """;
            select.Parameters.AddWithValue("$name", personName);
            var existing = await select.ExecuteScalarAsync(cancellationToken);
            if (existing is long personId) return personId;
        }

        using var insert = conn.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO persons (name) VALUES ($name) RETURNING id";
        insert.Parameters.AddWithValue("$name", personName);
        return (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
    }

    private static Dictionary<int, FaceRegion> MatchExistingFaces(
        IReadOnlyList<FaceRegion> existingFaces,
        IReadOnlyList<FaceRegion> incomingFaces)
    {
        var matches = new Dictionary<int, FaceRegion>();
        var usedExistingFaceIds = new HashSet<long>();
        var candidates = incomingFaces
            .SelectMany(
                (_, incomingIndex) => existingFaces.Select(existing => (
                    IncomingIndex: incomingIndex,
                    Existing: existing,
                    Overlap: CalculateIntersectionOverUnion(
                        incomingFaces[incomingIndex],
                        existing))))
            .Where(candidate => candidate.Overlap >= FaceRegionMatchThreshold)
            .OrderByDescending(candidate => candidate.Overlap);

        foreach (var candidate in candidates)
        {
            if (matches.ContainsKey(candidate.IncomingIndex) ||
                !usedExistingFaceIds.Add(candidate.Existing.Id))
            {
                continue;
            }

            matches.Add(candidate.IncomingIndex, candidate.Existing);
        }
        return matches;
    }

    private static double CalculateIntersectionOverUnion(
        FaceRegion first,
        FaceRegion second)
    {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        var intersection =
            Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union =
            first.Width * first.Height +
            second.Width * second.Height -
            intersection;
        return union <= 0 ? 0 : intersection / union;
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

    public async Task AssignFacesToPersonAsync(
        IReadOnlyCollection<long> faceIds,
        long personId,
        string personName,
        CancellationToken cancellationToken = default)
    {
        await AssignFacesToPersonWithStateAsync(
            faceIds,
            personId,
            personName,
            cancellationToken);
    }

    public async Task<IReadOnlyList<FaceTagState>> AssignFacesToPersonWithStateAsync(
        IReadOnlyCollection<long> faceIds,
        long personId,
        string personName,
        CancellationToken cancellationToken = default)
    {
        if (faceIds.Count == 0) return [];

        using var conn = _db.CreateConnection();
        using var transaction = conn.BeginTransaction();
        var previousStates = await GetFaceTagStatesAsync(
            conn,
            transaction,
            faceIds,
            cancellationToken);
        await AssignFacesToPersonAsync(
            conn,
            transaction,
            faceIds,
            personId,
            personName,
            cancellationToken);
        transaction.Commit();
        return previousStates;
    }

    public async Task<(long PersonId, IReadOnlyList<FaceTagState> PreviousStates)>
        CreatePersonAndAssignFacesAsync(
        string personName,
        IReadOnlyCollection<long> faceIds,
        CancellationToken cancellationToken = default)
    {
        if (faceIds.Count == 0)
        {
            throw new ArgumentException(
                "Select at least one face.",
                nameof(faceIds));
        }

        using var conn = _db.CreateConnection();
        using var transaction = conn.BeginTransaction();
        var previousStates = await GetFaceTagStatesAsync(
            conn,
            transaction,
            faceIds,
            cancellationToken);
        long personId;
        using (var create = conn.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText =
                "INSERT INTO persons (name) VALUES ($name) RETURNING id";
            create.Parameters.AddWithValue("$name", personName);
            personId = (long)(await create.ExecuteScalarAsync(cancellationToken))!;
        }

        await AssignFacesToPersonAsync(
            conn,
            transaction,
            faceIds,
            personId,
            personName,
            cancellationToken);
        transaction.Commit();
        return (personId, previousStates);
    }

    private static async Task AssignFacesToPersonAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        IReadOnlyCollection<long> faceIds,
        long personId,
        string personName,
        CancellationToken cancellationToken)
    {
        var parameters = AddFaceIdParameters(faceIds);
        var idList = string.Join(", ", parameters.Select(parameter => parameter.ParameterName));

        using (var clearRepresentatives = conn.CreateCommand())
        {
            clearRepresentatives.Transaction = transaction;
            clearRepresentatives.CommandText = $"""
                UPDATE persons
                SET representative_face_region_id = NULL
                WHERE id <> $personId
                  AND representative_face_region_id IN ({idList})
                """;
            clearRepresentatives.Parameters.AddWithValue("$personId", personId);
            foreach (var parameter in AddFaceIdParameters(faceIds))
            {
                clearRepresentatives.Parameters.Add(parameter);
            }
            await clearRepresentatives.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var update = conn.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = $"""
                UPDATE face_regions
                SET person_id = $personId, person_name = $personName
                WHERE id IN ({idList})
                """;
            update.Parameters.AddWithValue("$personId", personId);
            update.Parameters.AddWithValue("$personName", personName);
            foreach (var parameter in parameters)
            {
                update.Parameters.Add(parameter);
            }
            var affected = await update.ExecuteNonQueryAsync(cancellationToken);
            if (affected != parameters.Count)
            {
                throw new InvalidOperationException(
                    "One or more selected face suggestions no longer exist.");
            }
        }

        using (var clearRejections = conn.CreateCommand())
        {
            clearRejections.Transaction = transaction;
            clearRejections.CommandText =
                $"DELETE FROM face_rejections WHERE face_region_id IN ({idList})";
            foreach (var parameter in AddFaceIdParameters(faceIds))
            {
                clearRejections.Parameters.Add(parameter);
            }
            await clearRejections.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var restoreSuggestions = conn.CreateCommand())
        {
            restoreSuggestions.Transaction = transaction;
            restoreSuggestions.CommandText =
                $"DELETE FROM hidden_face_suggestions WHERE face_region_id IN ({idList})";
            foreach (var parameter in AddFaceIdParameters(faceIds))
            {
                restoreSuggestions.Parameters.Add(parameter);
            }
            await restoreSuggestions.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task RestoreFaceTagStatesAsync(
        IReadOnlyCollection<FaceTagState> states,
        long expectedPersonId,
        bool deleteExpectedPersonIfEmpty,
        CancellationToken cancellationToken = default)
    {
        if (states.Count == 0) return;

        using var conn = _db.CreateConnection();
        using var transaction = conn.BeginTransaction();
        foreach (var state in states)
        {
            using (var clearRepresentative = conn.CreateCommand())
            {
                clearRepresentative.Transaction = transaction;
                clearRepresentative.CommandText = """
                    UPDATE persons
                    SET representative_face_region_id = NULL
                    WHERE representative_face_region_id = $faceId
                    """;
                clearRepresentative.Parameters.AddWithValue("$faceId", state.FaceRegionId);
                await clearRepresentative.ExecuteNonQueryAsync(cancellationToken);
            }

            using (var restoreAssignment = conn.CreateCommand())
            {
                restoreAssignment.Transaction = transaction;
                restoreAssignment.CommandText = """
                    UPDATE face_regions
                    SET person_id = $personId, person_name = $personName
                    WHERE id = $faceId AND person_id = $expectedPersonId
                    """;
                restoreAssignment.Parameters.AddWithValue("$faceId", state.FaceRegionId);
                restoreAssignment.Parameters.AddWithValue(
                    "$personId",
                    (object?)state.PersonId ?? DBNull.Value);
                restoreAssignment.Parameters.AddWithValue(
                    "$personName",
                    (object?)state.PersonName ?? DBNull.Value);
                restoreAssignment.Parameters.AddWithValue(
                    "$expectedPersonId",
                    expectedPersonId);
                if (await restoreAssignment.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new InvalidOperationException(
                        "The last tag action can no longer be undone because a face changed.");
                }
            }

            using (var clearRejections = conn.CreateCommand())
            {
                clearRejections.Transaction = transaction;
                clearRejections.CommandText =
                    "DELETE FROM face_rejections WHERE face_region_id = $faceId";
                clearRejections.Parameters.AddWithValue("$faceId", state.FaceRegionId);
                await clearRejections.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var rejectedPersonId in state.RejectedPersonIds)
            {
                using var restoreRejection = conn.CreateCommand();
                restoreRejection.Transaction = transaction;
                restoreRejection.CommandText = """
                    INSERT INTO face_rejections (face_region_id, person_id)
                    VALUES ($faceId, $personId)
                    """;
                restoreRejection.Parameters.AddWithValue("$faceId", state.FaceRegionId);
                restoreRejection.Parameters.AddWithValue("$personId", rejectedPersonId);
                await restoreRejection.ExecuteNonQueryAsync(cancellationToken);
            }

            using (var restoreHidden = conn.CreateCommand())
            {
                restoreHidden.Transaction = transaction;
                restoreHidden.CommandText = state.SuggestionsHidden
                    ? """
                      INSERT OR IGNORE INTO hidden_face_suggestions (face_region_id)
                      VALUES ($faceId)
                      """
                    : """
                      DELETE FROM hidden_face_suggestions
                      WHERE face_region_id = $faceId
                      """;
                restoreHidden.Parameters.AddWithValue("$faceId", state.FaceRegionId);
                await restoreHidden.ExecuteNonQueryAsync(cancellationToken);
            }

            if (state.WasRepresentative && state.PersonId.HasValue)
            {
                using var restoreRepresentative = conn.CreateCommand();
                restoreRepresentative.Transaction = transaction;
                restoreRepresentative.CommandText = """
                    UPDATE persons
                    SET representative_face_region_id = $faceId
                    WHERE id = $personId
                    """;
                restoreRepresentative.Parameters.AddWithValue("$faceId", state.FaceRegionId);
                restoreRepresentative.Parameters.AddWithValue("$personId", state.PersonId.Value);
                await restoreRepresentative.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        if (deleteExpectedPersonIfEmpty)
        {
            using var deletePerson = conn.CreateCommand();
            deletePerson.Transaction = transaction;
            deletePerson.CommandText = """
                DELETE FROM persons
                WHERE id = $personId
                  AND NOT EXISTS (
                      SELECT 1 FROM face_regions WHERE person_id = $personId
                  )
                """;
            deletePerson.Parameters.AddWithValue("$personId", expectedPersonId);
            await deletePerson.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    private static async Task<IReadOnlyList<FaceTagState>> GetFaceTagStatesAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        IReadOnlyCollection<long> faceIds,
        CancellationToken cancellationToken)
    {
        var parameters = AddFaceIdParameters(faceIds);
        var idList = string.Join(", ", parameters.Select(parameter => parameter.ParameterName));
        var states = new Dictionary<long, FaceTagState>();
        using (var assignments = conn.CreateCommand())
        {
            assignments.Transaction = transaction;
            assignments.CommandText = $"""
                SELECT fr.id, fr.person_id, fr.person_name,
                       CASE WHEN p.representative_face_region_id = fr.id THEN 1 ELSE 0 END,
                       CASE WHEN hidden.face_region_id IS NULL THEN 0 ELSE 1 END
                FROM face_regions fr
                LEFT JOIN persons p ON p.id = fr.person_id
                LEFT JOIN hidden_face_suggestions hidden
                  ON hidden.face_region_id = fr.id
                WHERE fr.id IN ({idList})
                """;
            foreach (var parameter in parameters)
            {
                assignments.Parameters.Add(parameter);
            }
            using var reader = await assignments.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var faceId = reader.GetInt64(0);
                states.Add(
                    faceId,
                    new FaceTagState(
                        faceId,
                        reader.IsDBNull(1) ? null : reader.GetInt64(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetInt32(3) != 0,
                        reader.GetInt32(4) != 0,
                        []));
            }
        }

        if (states.Count != parameters.Count)
        {
            throw new InvalidOperationException(
                "One or more selected face suggestions no longer exist.");
        }

        using (var rejections = conn.CreateCommand())
        {
            rejections.Transaction = transaction;
            rejections.CommandText = $"""
                SELECT face_region_id, person_id
                FROM face_rejections
                WHERE face_region_id IN ({idList})
                """;
            foreach (var parameter in AddFaceIdParameters(faceIds))
            {
                rejections.Parameters.Add(parameter);
            }
            var rejectedPeople = states.Keys.ToDictionary(
                faceId => faceId,
                _ => new List<long>());
            using var reader = await rejections.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rejectedPeople[reader.GetInt64(0)].Add(reader.GetInt64(1));
            }
            foreach (var (faceId, rejectedPersonIds) in rejectedPeople)
            {
                states[faceId] = states[faceId] with
                {
                    RejectedPersonIds = rejectedPersonIds
                };
            }
        }

        return states.Values.ToList();
    }

    public async Task RejectPersonForFacesAsync(
        IReadOnlyCollection<long> faceIds,
        long personId,
        CancellationToken cancellationToken = default)
    {
        if (faceIds.Count == 0) return;

        using var conn = _db.CreateConnection();
        using var transaction = conn.BeginTransaction();
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO face_rejections (face_region_id, person_id)
            VALUES ($faceId, $personId)
            """;
        var faceIdParameter = command.Parameters.Add("$faceId", SqliteType.Integer);
        command.Parameters.AddWithValue("$personId", personId);
        command.Prepare();
        foreach (var faceId in faceIds)
        {
            faceIdParameter.Value = faceId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    public async Task<Dictionary<long, HashSet<long>>> GetRejectedPersonIdsByFaceIdAsync(
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT face_region_id, person_id FROM face_rejections";

        var results = new Dictionary<long, HashSet<long>>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var faceId = reader.GetInt64(0);
            if (!results.TryGetValue(faceId, out var personIds))
            {
                personIds = [];
                results.Add(faceId, personIds);
            }
            personIds.Add(reader.GetInt64(1));
        }
        return results;
    }

    public async Task HideFacesFromSuggestionsAsync(
        IReadOnlyCollection<long> faceIds,
        CancellationToken cancellationToken = default)
    {
        if (faceIds.Count == 0) return;

        using var conn = _db.CreateConnection();
        using var transaction = conn.BeginTransaction();
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO hidden_face_suggestions (face_region_id)
            VALUES ($faceId)
            """;
        var faceIdParameter = command.Parameters.Add("$faceId", SqliteType.Integer);
        command.Prepare();
        foreach (var faceId in faceIds.Distinct())
        {
            faceIdParameter.Value = faceId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
    }

    public async Task<HashSet<long>> GetHiddenFaceSuggestionIdsAsync(
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT face_region_id FROM hidden_face_suggestions";

        var results = new HashSet<long>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetInt64(0));
        }
        return results;
    }

    public async Task RestoreFacesToSuggestionsAsync(
        IReadOnlyCollection<long> faceIds,
        CancellationToken cancellationToken = default)
    {
        if (faceIds.Count == 0) return;

        using var conn = _db.CreateConnection();
        using var transaction = conn.BeginTransaction();
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM hidden_face_suggestions
            WHERE face_region_id = $faceId
            """;
        var faceIdParameter = command.Parameters.Add("$faceId", SqliteType.Integer);
        command.Prepare();
        var restored = 0;
        foreach (var faceId in faceIds.Distinct())
        {
            faceIdParameter.Value = faceId;
            restored += await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (restored != faceIds.Distinct().Count())
        {
            transaction.Rollback();
            throw new InvalidOperationException(
                "One or more selected excluded faces no longer exist.");
        }
        transaction.Commit();
    }

    public async Task SetPersonSuggestionsHiddenAsync(
        long personId,
        bool hidden,
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = """
            UPDATE persons
            SET suggestions_hidden = $hidden
            WHERE id = $personId
            """;
        command.Parameters.AddWithValue("$hidden", hidden ? 1 : 0);
        command.Parameters.AddWithValue("$personId", personId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The selected person no longer exists.");
        }
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
            SELECT p.id, p.name, p.thumbnail, p.suggestions_hidden,
                   p.representative_face_region_id,
                   (SELECT COUNT(*) FROM face_regions fr WHERE fr.person_id = p.id) as face_count
            FROM persons p
            ORDER BY face_count DESC, p.name COLLATE NOCASE
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
                SuggestionsHidden = reader.GetInt32(3) != 0,
                RepresentativeFaceRegionId = reader.IsDBNull(4)
                    ? null
                    : reader.GetInt64(4),
                FaceCount = reader.GetInt32(5)
            });
        }
        return persons;
    }

    public async Task<Dictionary<long, (FaceRegion Face, string ImagePath)>>
        GetPersonRepresentativeFacesAsync(
            CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT p.id AS owner_person_id,
                   fr.id, fr.image_id, fr.x, fr.y, fr.width, fr.height,
                   fr.person_name, fr.person_id, NULL AS embedding, fr.confidence,
                   fr.metadata_managed, i.file_path
            FROM persons p
            JOIN face_regions fr ON fr.id = COALESCE(
                (
                    SELECT selected.id
                    FROM face_regions selected
                    WHERE selected.id = p.representative_face_region_id
                      AND selected.person_id = p.id
                ),
                (
                    SELECT MIN(fallback.id)
                    FROM face_regions fallback
                    WHERE fallback.person_id = p.id
                )
            )
            JOIN images i ON i.id = fr.image_id
            """;

        var results =
            new Dictionary<long, (FaceRegion Face, string ImagePath)>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                reader.GetInt64(reader.GetOrdinal("owner_person_id")),
                (
                    ReadFaceRegion(reader),
                    reader.GetString(reader.GetOrdinal("file_path"))
                ));
        }
        return results;
    }

    public async Task SetPersonRepresentativeFaceAsync(
        long personId,
        long faceRegionId,
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = """
            UPDATE persons
            SET representative_face_region_id = $faceRegionId
            WHERE id = $personId
              AND EXISTS (
                  SELECT 1
                  FROM face_regions
                  WHERE id = $faceRegionId
                    AND person_id = $personId
              )
            """;
        command.Parameters.AddWithValue("$personId", personId);
        command.Parameters.AddWithValue("$faceRegionId", faceRegionId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "The selected face is no longer assigned to this person.");
        }
    }

    public async Task RenamePersonAsync(
        long personId,
        string name,
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var transaction = conn.BeginTransaction();

        using (var duplicate = conn.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = """
                SELECT COUNT(*)
                FROM persons
                WHERE id <> $personId
                  AND name = $name COLLATE NOCASE
                """;
            duplicate.Parameters.AddWithValue("$personId", personId);
            duplicate.Parameters.AddWithValue("$name", name);
            if (Convert.ToInt64(
                    await duplicate.ExecuteScalarAsync(cancellationToken)) > 0)
            {
                transaction.Rollback();
                throw new InvalidOperationException(
                    $"A person named {name} already exists. Merge the people instead.");
            }
        }

        using (var updatePerson = conn.CreateCommand())
        {
            updatePerson.Transaction = transaction;
            updatePerson.CommandText = """
                UPDATE persons SET name = $name WHERE id = $personId
                """;
            updatePerson.Parameters.AddWithValue("$personId", personId);
            updatePerson.Parameters.AddWithValue("$name", name);
            if (await updatePerson.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                transaction.Rollback();
                throw new InvalidOperationException("The selected person no longer exists.");
            }
        }

        using (var updateFaces = conn.CreateCommand())
        {
            updateFaces.Transaction = transaction;
            updateFaces.CommandText = """
                UPDATE face_regions
                SET person_name = $name
                WHERE person_id = $personId
                """;
            updateFaces.Parameters.AddWithValue("$personId", personId);
            updateFaces.Parameters.AddWithValue("$name", name);
            await updateFaces.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    public async Task UnassignFacesAsync(
        IReadOnlyCollection<long> faceIds,
        CancellationToken cancellationToken = default)
    {
        if (faceIds.Count == 0) return;

        using var conn = _db.CreateConnection();
        using var transaction = conn.BeginTransaction();
        using var unassign = conn.CreateCommand();
        using var clearRejections = conn.CreateCommand();
        using var restoreSuggestions = conn.CreateCommand();
        unassign.Transaction = transaction;
        clearRejections.Transaction = transaction;
        restoreSuggestions.Transaction = transaction;
        unassign.CommandText = """
            UPDATE face_regions
            SET person_id = NULL, person_name = NULL
            WHERE id = $faceId AND person_id IS NOT NULL
            """;
        clearRejections.CommandText =
            "DELETE FROM face_rejections WHERE face_region_id = $faceId";
        restoreSuggestions.CommandText =
            "DELETE FROM hidden_face_suggestions WHERE face_region_id = $faceId";
        var unassignFaceId = unassign.Parameters.Add("$faceId", SqliteType.Integer);
        var rejectionFaceId = clearRejections.Parameters.Add("$faceId", SqliteType.Integer);
        var suggestionFaceId = restoreSuggestions.Parameters.Add("$faceId", SqliteType.Integer);
        unassign.Prepare();
        clearRejections.Prepare();
        restoreSuggestions.Prepare();

        foreach (var faceId in faceIds.Distinct())
        {
            using (var clearRepresentative = conn.CreateCommand())
            {
                clearRepresentative.Transaction = transaction;
                clearRepresentative.CommandText = """
                    UPDATE persons
                    SET representative_face_region_id = NULL
                    WHERE representative_face_region_id = $faceId
                    """;
                clearRepresentative.Parameters.AddWithValue("$faceId", faceId);
                await clearRepresentative.ExecuteNonQueryAsync(cancellationToken);
            }

            unassignFaceId.Value = faceId;
            if (await unassign.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                transaction.Rollback();
                throw new InvalidOperationException(
                    "One or more selected faces are no longer assigned.");
            }
            rejectionFaceId.Value = faceId;
            await clearRejections.ExecuteNonQueryAsync(cancellationToken);
            suggestionFaceId.Value = faceId;
            await restoreSuggestions.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
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

    public async Task MergePersonsAsync(
        long sourcePersonId,
        long targetPersonId,
        CancellationToken cancellationToken = default)
    {
        if (sourcePersonId == targetPersonId)
        {
            throw new ArgumentException("Choose a different person to merge into.");
        }

        using var conn = _db.CreateConnection();
        using var transaction = conn.BeginTransaction();
        string targetName;

        using (var people = conn.CreateCommand())
        {
            people.Transaction = transaction;
            people.CommandText = """
                SELECT id, name FROM persons WHERE id IN ($sourceId, $targetId)
                """;
            people.Parameters.AddWithValue("$sourceId", sourcePersonId);
            people.Parameters.AddWithValue("$targetId", targetPersonId);
            var existingIds = new HashSet<long>();
            string? loadedTargetName = null;
            using var reader = await people.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                existingIds.Add(id);
                if (id == targetPersonId) loadedTargetName = reader.GetString(1);
            }
            if (existingIds.Count != 2 || loadedTargetName is null)
            {
                transaction.Rollback();
                throw new InvalidOperationException(
                    "One or more selected people no longer exist.");
            }
            targetName = loadedTargetName;
        }

        using (var preserveRepresentative = conn.CreateCommand())
        {
            preserveRepresentative.Transaction = transaction;
            preserveRepresentative.CommandText = """
                UPDATE persons
                SET representative_face_region_id = COALESCE(
                    representative_face_region_id,
                    (
                        SELECT representative_face_region_id
                        FROM persons
                        WHERE id = $sourceId
                    )
                )
                WHERE id = $targetId
                """;
            preserveRepresentative.Parameters.AddWithValue("$sourceId", sourcePersonId);
            preserveRepresentative.Parameters.AddWithValue("$targetId", targetPersonId);
            await preserveRepresentative.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var table in new[] { "face_rejections", "hidden_face_suggestions" })
        {
            using var clearDecisions = conn.CreateCommand();
            clearDecisions.Transaction = transaction;
            clearDecisions.CommandText = $"""
                DELETE FROM {table}
                WHERE face_region_id IN (
                    SELECT id FROM face_regions WHERE person_id = $sourceId
                )
                """;
            clearDecisions.Parameters.AddWithValue("$sourceId", sourcePersonId);
            await clearDecisions.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var transferRejections = conn.CreateCommand())
        {
            transferRejections.Transaction = transaction;
            transferRejections.CommandText = """
                INSERT OR IGNORE INTO face_rejections (face_region_id, person_id)
                SELECT face_region_id, $targetId
                FROM face_rejections
                WHERE person_id = $sourceId
                """;
            transferRejections.Parameters.AddWithValue("$sourceId", sourcePersonId);
            transferRejections.Parameters.AddWithValue("$targetId", targetPersonId);
            await transferRejections.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var moveFaces = conn.CreateCommand())
        {
            moveFaces.Transaction = transaction;
            moveFaces.CommandText = """
                UPDATE face_regions
                SET person_id = $targetId, person_name = $targetName
                WHERE person_id = $sourceId
                """;
            moveFaces.Parameters.AddWithValue("$sourceId", sourcePersonId);
            moveFaces.Parameters.AddWithValue("$targetId", targetPersonId);
            moveFaces.Parameters.AddWithValue("$targetName", targetName);
            await moveFaces.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var deleteSource = conn.CreateCommand())
        {
            deleteSource.Transaction = transaction;
            deleteSource.CommandText = "DELETE FROM persons WHERE id = $sourceId";
            deleteSource.Parameters.AddWithValue("$sourceId", sourcePersonId);
            await deleteSource.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    public async Task DeletePersonAsync(
        long personId,
        CancellationToken cancellationToken = default)
    {
        using var conn = _db.CreateConnection();
        using var transaction = conn.BeginTransaction();

        foreach (var table in new[] { "face_rejections", "hidden_face_suggestions" })
        {
            using var clearDecisions = conn.CreateCommand();
            clearDecisions.Transaction = transaction;
            clearDecisions.CommandText = $"""
                DELETE FROM {table}
                WHERE face_region_id IN (
                    SELECT id FROM face_regions WHERE person_id = $personId
                )
                """;
            clearDecisions.Parameters.AddWithValue("$personId", personId);
            await clearDecisions.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var unassign = conn.CreateCommand())
        {
            unassign.Transaction = transaction;
            unassign.CommandText = """
                UPDATE face_regions
                SET person_id = NULL, person_name = NULL
                WHERE person_id = $personId
                """;
            unassign.Parameters.AddWithValue("$personId", personId);
            await unassign.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var deletePerson = conn.CreateCommand())
        {
            deletePerson.Transaction = transaction;
            deletePerson.CommandText = "DELETE FROM persons WHERE id = $personId";
            deletePerson.Parameters.AddWithValue("$personId", personId);
            if (await deletePerson.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                transaction.Rollback();
                throw new InvalidOperationException("The selected person no longer exists.");
            }
        }

        transaction.Commit();
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
            Confidence = reader.GetFloat(reader.GetOrdinal("confidence")),
            IsMetadataManaged = ReadBoolean(reader, "metadata_managed")
        };
    }

    private static bool ReadBoolean(
        SqliteDataReader reader,
        string column)
    {
        try
        {
            var ordinal = reader.GetOrdinal(column);
            return !reader.IsDBNull(ordinal) && reader.GetInt32(ordinal) != 0;
        }
        catch (Exception exception) when (
            exception is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return false;
        }
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

    private static List<SqliteParameter> AddFaceIdParameters(
        IReadOnlyCollection<long> faceIds)
    {
        var parameters = new List<SqliteParameter>(faceIds.Count);
        var index = 0;
        foreach (var faceId in faceIds.Distinct())
        {
            parameters.Add(new SqliteParameter($"$faceId{index++}", faceId));
        }
        return parameters;
    }
}
