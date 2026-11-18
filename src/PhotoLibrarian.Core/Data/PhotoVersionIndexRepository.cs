// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Data;

internal enum PhotoVersionIndexWriteStatus { Applied, Stale, ImageMissing, Invalid }

internal sealed record IndexedPhotoVersion(
    Guid VariantId, string Name, bool IsMain, PhotoVersionRecipeStatus Status);

internal sealed record IndexedPhotoVersionFamily(
    Guid Generation,
    PhotoVersionRecipeStatus Status,
    string? Message,
    Guid? SourceId,
    Guid? DocumentRevision,
    string? InputIdentity,
    string? MetadataHash,
    IReadOnlyList<IndexedPhotoVersion> Versions);

/// <summary>Rebuildable cache projection; recipes remain authoritative in photo metadata.</summary>
internal sealed class PhotoVersionIndexRepository(CacheDatabase database)
{
    internal async Task<IndexedPhotoVersionFamily?> GetAsync(
        long imageId, CancellationToken cancellationToken = default)
    {
        using var connection = database.CreateConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var family = connection.CreateCommand();
        family.Transaction = transaction;
        family.CommandText = """
            SELECT generation, status, message, source_id, document_revision, input_identity, metadata_hash
            FROM photo_version_family_index WHERE image_id = $imageId
            """;
        family.Parameters.AddWithValue("$imageId", imageId);
        using var reader = await family.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        var generation = Guid.Parse(reader.GetString(0));
        var status = (PhotoVersionRecipeStatus)reader.GetInt32(1);
        var message = reader.IsDBNull(2) ? null : reader.GetString(2);
        var sourceId = reader.IsDBNull(3) ? (Guid?)null : Guid.Parse(reader.GetString(3));
        var revision = reader.IsDBNull(4) ? (Guid?)null : Guid.Parse(reader.GetString(4));
        var identity = reader.IsDBNull(5) ? null : reader.GetString(5);
        var metadataHash = reader.IsDBNull(6) ? null : reader.GetString(6);
        await reader.DisposeAsync();

        using var versions = connection.CreateCommand();
        versions.Transaction = transaction;
        versions.CommandText = """
            SELECT variant_id, name, is_main, status
            FROM photo_version_index WHERE image_id = $imageId ORDER BY position
            """;
        versions.Parameters.AddWithValue("$imageId", imageId);
        using var rows = await versions.ExecuteReaderAsync(cancellationToken);
        var items = new List<IndexedPhotoVersion>();
        while (await rows.ReadAsync(cancellationToken))
            items.Add(new(Guid.Parse(rows.GetString(0)), rows.GetString(1),
                rows.GetInt32(2) != 0, (PhotoVersionRecipeStatus)rows.GetInt32(3)));
        return new(generation, status, message, sourceId, revision, identity, metadataHash, items);
    }

    /// <summary>
    /// Replaces the projection only if its cache generation still matches. Callers must also
    /// revalidate the photo's input identity when coordinating indexing and file changes.
    /// </summary>
    internal async Task<PhotoVersionIndexWriteStatus> ReconcileAsync(
        long imageId, string expectedPath, Guid? expectedGeneration, PhotoVersionFamilyResult result,
        CancellationToken cancellationToken = default, string? metadataHash = null,
        string? expectedImageMetadataHash = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(result.Status) || !ValidShape(result))
            return PhotoVersionIndexWriteStatus.Invalid;
        using var connection = database.CreateConnection();
        using var transaction = connection.BeginTransaction();
        using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = """
            SELECT i.file_path, f.generation, i.metadata_hash
            FROM images i LEFT JOIN photo_version_family_index f ON f.image_id = i.id
            WHERE i.id = $imageId
            """;
        lookup.Parameters.AddWithValue("$imageId", imageId);
        using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return PhotoVersionIndexWriteStatus.ImageMissing;
        var imagePath = reader.GetString(0);
        var current = reader.IsDBNull(1) ? (Guid?)null : Guid.Parse(reader.GetString(1));
        var imageMetadataHash = reader.IsDBNull(2) ? null : reader.GetString(2);
        await reader.DisposeAsync();
        if (current != expectedGeneration ||
            expectedImageMetadataHash is not null &&
                !string.Equals(imageMetadataHash, expectedImageMetadataHash, StringComparison.Ordinal) ||
            !string.Equals(imagePath, expectedPath, StringComparison.OrdinalIgnoreCase))
            return PhotoVersionIndexWriteStatus.Stale;
        if (result.Status == PhotoVersionRecipeStatus.Success &&
            !ValidRecords(result, imagePath))
            return PhotoVersionIndexWriteStatus.Invalid;

        using var family = connection.CreateCommand();
        family.Transaction = transaction;
        family.CommandText = result.Status == PhotoVersionRecipeStatus.Success
            ? """
              INSERT INTO photo_version_family_index
                  (image_id, generation, status, message, source_id, document_revision, input_identity, metadata_hash)
              VALUES ($imageId, $generation, $status, $message, $sourceId, $revision, $identity, $metadataHash)
              ON CONFLICT(image_id) DO UPDATE SET generation=excluded.generation,
                  status=excluded.status, message=excluded.message, source_id=excluded.source_id,
                  document_revision=excluded.document_revision, input_identity=excluded.input_identity,
                  metadata_hash=excluded.metadata_hash
              """
            : """
              INSERT INTO photo_version_family_index (image_id, generation, status, message, metadata_hash)
              VALUES ($imageId, $generation, $status, $message, $metadataHash)
              ON CONFLICT(image_id) DO UPDATE SET generation=excluded.generation,
                  status=excluded.status, message=excluded.message, metadata_hash=excluded.metadata_hash
              """;
        family.Parameters.AddWithValue("$imageId", imageId);
        family.Parameters.AddWithValue("$generation", Guid.NewGuid().ToString("D"));
        family.Parameters.AddWithValue("$status", (int)result.Status);
        family.Parameters.AddWithValue("$message", (object?)result.Message ?? DBNull.Value);
        family.Parameters.AddWithValue("$metadataHash", (object?)metadataHash ?? DBNull.Value);
        if (result.Status == PhotoVersionRecipeStatus.Success)
        {
            family.Parameters.AddWithValue("$sourceId", result.Records![0].Target.SourceId.ToString("D"));
            family.Parameters.AddWithValue("$revision", result.DocumentRevision!.Value.ToString("D"));
            family.Parameters.AddWithValue("$identity", result.InputIdentity!);
        }
        await family.ExecuteNonQueryAsync(cancellationToken);

        if (result.Status == PhotoVersionRecipeStatus.Success)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM photo_version_index WHERE image_id = $imageId";
            delete.Parameters.AddWithValue("$imageId", imageId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            for (var index = 0; index < result.Records!.Count; index++)
            {
                var record = result.Records[index];
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO photo_version_index (image_id, variant_id, name, is_main, position, status)
                    VALUES ($imageId, $variantId, $name, $isMain, $position, $status)
                    """;
                insert.Parameters.AddWithValue("$imageId", imageId);
                insert.Parameters.AddWithValue("$variantId", record.Target.VariantId.ToString("D"));
                insert.Parameters.AddWithValue("$name", record.Name);
                insert.Parameters.AddWithValue("$isMain", record.IsMain ? 1 : 0);
                insert.Parameters.AddWithValue("$position", index);
                insert.Parameters.AddWithValue("$status", (int)record.Status);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return PhotoVersionIndexWriteStatus.Applied;
    }

    private static bool ValidShape(PhotoVersionFamilyResult result) =>
        result.Status == PhotoVersionRecipeStatus.Success
            ? result.Records is { Count: > 0 } &&
              result.DocumentRevision is { } revision && revision != Guid.Empty &&
              !string.IsNullOrWhiteSpace(result.InputIdentity)
            : result.Records is null && result.DocumentRevision is null && result.InputIdentity is null;

    private static bool ValidRecords(PhotoVersionFamilyResult result, string imagePath)
    {
        var records = result.Records!;
        var sourceId = records[0].Target.SourceId;
        if (sourceId == Guid.Empty || !records[0].IsMain) return false;
        var ids = new HashSet<Guid>();
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (record.Target.SourceId != sourceId || record.Target.VariantId == Guid.Empty ||
                !ids.Add(record.Target.VariantId) || record.IsMain != (index == 0) ||
                string.IsNullOrWhiteSpace(record.Name) || !Enum.IsDefined(record.Status) ||
                !string.Equals(record.Target.SourcePath, imagePath, StringComparison.OrdinalIgnoreCase))
                return false;
            if (record.Status == PhotoVersionRecipeStatus.Success)
            {
                var snapshot = record.Snapshot;
                if (snapshot is null || snapshot.Target != record.Target ||
                    snapshot.DocumentRevision != result.DocumentRevision ||
                    snapshot.InputIdentity != result.InputIdentity ||
                    string.IsNullOrWhiteSpace(snapshot.RecipeXmp))
                    return false;
            }
            else if (record.Snapshot is not null)
                return false;
        }
        return true;
    }
}
