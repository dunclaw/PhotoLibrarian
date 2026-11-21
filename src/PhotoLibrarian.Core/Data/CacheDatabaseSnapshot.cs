// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Data.Sqlite;

namespace PhotoLibrarian.Core.Data;

/// <summary>
/// Creates consistent SQLite snapshots without replacing an open database.
/// Run on a worker thread: SQLite's backup operation is synchronous.
/// </summary>
public static class CacheDatabaseSnapshot
{
    public static CacheSnapshotResult Backup(
        CacheDatabase database,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return new(CacheSnapshotStatus.Cancelled);

        try
        {
            using var source = database.CreateConnection();
            return Copy(source, destinationPath, cancellationToken);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new(CacheSnapshotStatus.Failed, Message: exception.Message);
        }
    }

    /// <summary>
    /// Copies a snapshot to a new file. This never replaces the live database;
    /// a future recovery workflow must coordinate closing all database users.
    /// </summary>
    public static CacheSnapshotResult RecoverToNewFile(
        string snapshotPath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return new(CacheSnapshotStatus.Cancelled);

        try
        {
            using var source = new SqliteConnection(ConnectionString(snapshotPath, SqliteOpenMode.ReadOnly));
            source.Open();
            var validationError = Validate(source);
            if (validationError is not null)
                return new(CacheSnapshotStatus.InvalidSnapshot, Message: validationError);
            return Copy(source, destinationPath, cancellationToken);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new(CacheSnapshotStatus.Failed, Message: exception.Message);
        }
    }

    private static CacheSnapshotResult Copy(
        SqliteConnection source,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return new(CacheSnapshotStatus.Cancelled);

        FileStream reservation;
        try
        {
            reservation = new FileStream(
                destinationPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            return new(CacheSnapshotStatus.DestinationExists);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new(CacheSnapshotStatus.Failed, Message: exception.Message);
        }

        // The reservation prevents replacement while SQLite has the destination open.
        // Leave incomplete files in place rather than deleting a possibly replaced path.
        using (reservation)
        try
        {
            using var destination = new SqliteConnection(ConnectionString(destinationPath, SqliteOpenMode.ReadWrite));
            destination.Open();
            source.BackupDatabase(destination);
            if (cancellationToken.IsCancellationRequested)
                return new(CacheSnapshotStatus.Cancelled, destinationPath);

            var validationError = Validate(destination);
            if (cancellationToken.IsCancellationRequested)
                return new(CacheSnapshotStatus.Cancelled, destinationPath);
            return validationError is null
                ? new(CacheSnapshotStatus.Success, destinationPath)
                : new(CacheSnapshotStatus.InvalidSnapshot, destinationPath, validationError);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new(CacheSnapshotStatus.Failed, destinationPath, exception.Message);
        }
    }

    private static string? Validate(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read())
                return "SQLite integrity check failed.";
        }

        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name IN ('images', 'watched_folders', 'tags', 'face_regions', 'persons');
            """;
        return Convert.ToInt64(command.ExecuteScalar()) == 5
            ? null
            : "The snapshot is not a PhotoLibrarian cache database.";
    }

    private static string ConnectionString(string path, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false
        }.ToString();

    private static bool IsStorageFailure(Exception exception) =>
        exception is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}

public enum CacheSnapshotStatus
{
    Success,
    Cancelled,
    DestinationExists,
    InvalidSnapshot,
    Failed
}

public sealed record CacheSnapshotResult(
    CacheSnapshotStatus Status,
    string? CreatedPath = null,
    string? Message = null);
