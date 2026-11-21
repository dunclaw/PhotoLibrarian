// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class CacheDatabaseSnapshotTests
{
    [Fact]
    public async Task BackupAndRecoveryCopy_PreserveCommittedWalDataWithoutReplacingSource()
    {
        var directory = CreateDirectory();
        try
        {
            var databasePath = Path.Combine(directory, "cache.db");
            var backupPath = Path.Combine(directory, "backup.db");
            var recoveryPath = Path.Combine(directory, "recovered.db");
            using var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            using var writer = database.CreateConnection();
            using var command = writer.CreateCommand();
            command.CommandText = """
                PRAGMA wal_autocheckpoint=0;
                INSERT INTO watched_folders(path) VALUES ('original');
                INSERT INTO images(file_path, file_name, date_modified, face_metadata_export_required)
                    VALUES ('photo.raw', 'photo.raw', '2020-01-01', 1);
                INSERT INTO tags(image_id, tag) VALUES (last_insert_rowid(), 'cache-only');
                """;
            await command.ExecuteNonQueryAsync();

            var backup = CacheDatabaseSnapshot.Backup(database, backupPath);
            Assert.Equal(CacheSnapshotStatus.Success, backup.Status);
            Assert.Equal(backupPath, backup.CreatedPath);

            command.CommandText = "UPDATE watched_folders SET path = 'changed'; DELETE FROM tags;";
            await command.ExecuteNonQueryAsync();
            var recovery = CacheDatabaseSnapshot.RecoverToNewFile(backupPath, recoveryPath);
            Assert.Equal(CacheSnapshotStatus.Success, recovery.Status);
            Assert.Equal(recoveryPath, recovery.CreatedPath);
            Assert.Equal("original", ReadScalar(recoveryPath, "SELECT path FROM watched_folders"));
            Assert.Equal("cache-only", ReadScalar(recoveryPath, "SELECT tag FROM tags"));
            Assert.Equal("1", ReadScalar(recoveryPath, "SELECT face_metadata_export_required FROM images"));
            Assert.Equal("changed", ReadScalar(databasePath, "SELECT path FROM watched_folders"));
            Assert.Equal("cache-only", ReadScalar(backupPath, "SELECT tag FROM tags"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingDestinationAndCancellation_DoNotAlterFiles()
    {
        var directory = CreateDirectory();
        try
        {
            using var database = new CacheDatabase(Path.Combine(directory, "cache.db"));
            await database.InitializeAsync();
            var existingPath = Path.Combine(directory, "existing.db");
            await File.WriteAllTextAsync(existingPath, "keep");
            var cancelledPath = Path.Combine(directory, "cancelled.db");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Equal(CacheSnapshotStatus.DestinationExists,
                CacheDatabaseSnapshot.Backup(database, existingPath).Status);
            Assert.Equal("keep", await File.ReadAllTextAsync(existingPath));
            Assert.Equal(CacheSnapshotStatus.Cancelled,
                CacheDatabaseSnapshot.Backup(database, cancelledPath, cancellation.Token).Status);
            Assert.False(File.Exists(cancelledPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RecoveryRejectsAnUnrelatedDatabaseWithoutCreatingOutput()
    {
        var directory = CreateDirectory();
        try
        {
            var unrelatedPath = Path.Combine(directory, "unrelated.db");
            var destinationPath = Path.Combine(directory, "recovered.db");
            using (var unrelated = new SqliteConnection($"Data Source={unrelatedPath};Pooling=False"))
            {
                unrelated.Open();
                using var command = unrelated.CreateCommand();
                command.CommandText = "CREATE TABLE unrelated (id INTEGER);";
                command.ExecuteNonQuery();
            }

            var result = CacheDatabaseSnapshot.RecoverToNewFile(unrelatedPath, destinationPath);
            Assert.Equal(CacheSnapshotStatus.InvalidSnapshot, result.Status);
            Assert.False(File.Exists(destinationPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BackupReportsAnUnavailableDestination()
    {
        var directory = CreateDirectory();
        try
        {
            using var database = new CacheDatabase(Path.Combine(directory, "cache.db"));
            await database.InitializeAsync();
            var destinationPath = Path.Combine(directory, "missing", "backup.db");
            var result = CacheDatabaseSnapshot.Backup(database, destinationPath);
            Assert.Equal(CacheSnapshotStatus.Failed, result.Status);
            Assert.NotEmpty(result.Message!);
            Assert.Null(result.CreatedPath);
            Assert.False(File.Exists(destinationPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string? ReadScalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar());
    }
}
