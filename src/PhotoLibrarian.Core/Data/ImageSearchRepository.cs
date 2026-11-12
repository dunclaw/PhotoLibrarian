// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Data;

public sealed class ImageSearchRepository(CacheDatabase database)
{
    public async Task<List<ImageEntry>> FilterAsync(
        string text, IReadOnlyList<ImageEntry> scope, int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (scope.Count == 0) return [];
        var matches = (await SearchAsync(text, cancellationToken: cancellationToken))
            .Select(image => image.Id).ToHashSet();
        // Preserve the caller's grid order, not FTS relevance. Limit only after intersecting.
        return scope.Where(image => matches.Contains(image.Id))
            .Take(limit ?? int.MaxValue).ToList();
    }

    internal static async Task InitializeAsync(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var probe = connection.CreateCommand();
        probe.Transaction = transaction;
        probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'image_search' AND type = 'table'";
        var needsBackfill = Convert.ToInt64(await probe.ExecuteScalarAsync()) == 0;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS image_search USING fts5(
                file_name, caption, title, tags, camera, people,
                tokenize = 'unicode61 remove_diacritics 2', prefix = '2 3 4'
            );
            CREATE VIEW IF NOT EXISTS image_search_documents AS
                SELECT i.id AS rowid, i.file_name, i.caption, i.title,
                    (SELECT group_concat(tag, ' ') FROM tags WHERE image_id = i.id) AS tags,
                    coalesce(i.camera_make, '') || ' ' || coalesce(i.camera_model, '') AS camera,
                    (SELECT group_concat(coalesce(p.name, f.person_name), ' ')
                     FROM face_regions f LEFT JOIN persons p ON p.id = f.person_id
                     WHERE f.image_id = i.id) AS people
                FROM images i;

            CREATE TRIGGER IF NOT EXISTS image_search_insert AFTER INSERT ON images BEGIN
                INSERT INTO image_search(rowid, file_name, caption, title, tags, camera, people)
                    SELECT * FROM image_search_documents WHERE rowid = NEW.id;
            END;
            CREATE TRIGGER IF NOT EXISTS image_search_update
            AFTER UPDATE OF file_name, caption, title, camera_make, camera_model ON images BEGIN
                DELETE FROM image_search WHERE rowid = OLD.id;
                INSERT INTO image_search(rowid, file_name, caption, title, tags, camera, people)
                    SELECT * FROM image_search_documents WHERE rowid = NEW.id;
            END;
            CREATE TRIGGER IF NOT EXISTS image_search_delete AFTER DELETE ON images BEGIN
                DELETE FROM image_search WHERE rowid = OLD.id;
            END;
            CREATE TRIGGER IF NOT EXISTS image_search_person_update AFTER UPDATE OF name ON persons BEGIN
                DELETE FROM image_search WHERE rowid IN
                    (SELECT image_id FROM face_regions WHERE person_id = NEW.id);
                INSERT INTO image_search(rowid, file_name, caption, title, tags, camera, people)
                    SELECT * FROM image_search_documents WHERE rowid IN
                        (SELECT image_id FROM face_regions WHERE person_id = NEW.id);
            END;
            """;
        await command.ExecuteNonQueryAsync();

        // Keep the index in the same transaction as every writer, including bulk tag/face operations.
        foreach (var table in new[] { "tags", "face_regions" })
        {
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var affected = operation switch
                {
                    "INSERT" => "NEW.image_id",
                    "DELETE" => "OLD.image_id",
                    _ => "OLD.image_id, NEW.image_id"
                };
                var eventClause = operation == "UPDATE"
                    ? $"UPDATE OF image_id, {(table == "tags" ? "tag" : "person_id, person_name")}"
                    : operation;
                command.CommandText = $"""
                    CREATE TRIGGER IF NOT EXISTS image_search_{table}_{operation}
                    AFTER {eventClause} ON {table} BEGIN
                        DELETE FROM image_search WHERE rowid IN ({affected});
                        INSERT INTO image_search(rowid, file_name, caption, title, tags, camera, people)
                            SELECT * FROM image_search_documents WHERE rowid IN ({affected});
                    END;
                    """;
                await command.ExecuteNonQueryAsync();
            }
        }

        if (needsBackfill)
        {
            command.CommandText = """
                INSERT INTO image_search(rowid, file_name, caption, title, tags, camera, people)
                    SELECT * FROM image_search_documents;
                """;
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }

    public async Task<List<ImageEntry>> SearchAsync(
        string text, int? limit = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (limit is <= 0) throw new ArgumentOutOfRangeException(nameof(limit));

        var query = ImageSearchQueryParser.Parse(text);
        if (!query.IsValid ||
            (query.FullTextQuery.Length == 0 && query.FieldClauses.Count == 0))
            return [];

        using var connection = database.CreateConnection();
        using var command = connection.CreateCommand();
        var predicates = new List<string>();
        var joinsSearchIndex = query.FullTextQuery.Length > 0;
        if (joinsSearchIndex)
        {
            predicates.Add("image_search MATCH $query");
            command.Parameters.AddWithValue("$query", query.FullTextQuery);
        }

        var parameterIndex = 0;
        foreach (var clause in query.FieldClauses)
            AddFieldPredicate(command, predicates, clause, ref parameterIndex);

        command.CommandText = $"""
            SELECT i.* FROM images i
            {(joinsSearchIndex ? "JOIN image_search ON image_search.rowid = i.id" : string.Empty)}
            WHERE {string.Join(" AND ", predicates)}
            {(joinsSearchIndex ? "ORDER BY rank, i.file_name, i.id" : "ORDER BY i.date_taken DESC, i.file_name, i.id")}
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit ?? -1);
        var results = new List<ImageEntry>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(ImageRepository.ReadImageEntry(reader));
        return results;
    }

    public static string? GetQuerySyntaxError(string text) =>
        ImageSearchQueryParser.GetSyntaxError(text);

    private static void AddFieldPredicate(
        SqliteCommand command,
        List<string> predicates,
        ImageSearchFieldClause clause,
        ref int parameterIndex)
    {
        switch (clause.Field)
        {
            case ImageSearchField.Rating:
            case ImageSearchField.Iso:
                var column = clause.Field == ImageSearchField.Rating ? "i.rating" : "i.iso";
                var numberParameter = $"$field{parameterIndex++}";
                predicates.Add($"{column} {ToSqlOperator(clause.Comparison)} {numberParameter}");
                command.Parameters.AddWithValue(numberParameter, clause.Number);
                break;

            case ImageSearchField.Flag:
                predicates.Add($"i.is_flagged = $field{parameterIndex}");
                command.Parameters.AddWithValue($"$field{parameterIndex++}", clause.FlagValue ? 1 : 0);
                break;

            case ImageSearchField.Date:
                var startParameter = $"$field{parameterIndex++}";
                var endParameter = $"$field{parameterIndex++}";
                command.Parameters.AddWithValue(startParameter, clause.DateStart.ToString("yyyy-MM-dd"));
                command.Parameters.AddWithValue(endParameter, clause.DateEnd.ToString("yyyy-MM-dd"));
                switch (clause.Comparison)
                {
                    case SearchComparison.GreaterThan:
                        predicates.Add($"date(i.date_taken) >= {endParameter}");
                        break;
                    case SearchComparison.GreaterThanOrEqual:
                        predicates.Add($"date(i.date_taken) >= {startParameter}");
                        break;
                    case SearchComparison.LessThan:
                        predicates.Add($"date(i.date_taken) < {startParameter}");
                        break;
                    case SearchComparison.LessThanOrEqual:
                        predicates.Add($"date(i.date_taken) < {endParameter}");
                        break;
                    default:
                        predicates.Add($"date(i.date_taken) >= {startParameter} AND date(i.date_taken) < {endParameter}");
                        break;
                }
                break;

        }
    }

    private static string ToSqlOperator(SearchComparison comparison) =>
        comparison switch
        {
            SearchComparison.GreaterThan => ">",
            SearchComparison.GreaterThanOrEqual => ">=",
            SearchComparison.LessThan => "<",
            SearchComparison.LessThanOrEqual => "<=",
            _ => "="
        };
}
