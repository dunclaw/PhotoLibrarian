using Microsoft.Data.Sqlite;

namespace PhotoLibrarian.Core.Data;

/// <summary>
/// Manages the SQLite cache database. This is a performance cache only —
/// all authoritative data lives in image file metadata (EXIF/XMP/IPTC).
/// The database can be safely deleted and rebuilt by re-scanning.
/// </summary>
public sealed class CacheDatabase : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _initConnection;

    public CacheDatabase(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task InitializeAsync()
    {
        // Keep one connection open to hold the shared cache alive
        _initConnection = new SqliteConnection(_connectionString);
        await _initConnection.OpenAsync();

        // WAL mode for concurrent reads during writes
        await ExecutePragmaAsync(_initConnection, "PRAGMA journal_mode=WAL;");
        // 64KB page size for better BLOB performance
        await ExecutePragmaAsync(_initConnection, "PRAGMA page_size=65536;");
        // Performance tuning
        await ExecutePragmaAsync(_initConnection, "PRAGMA synchronous=NORMAL;");
        await ExecutePragmaAsync(_initConnection, "PRAGMA temp_store=MEMORY;");
        await ExecutePragmaAsync(_initConnection, "PRAGMA mmap_size=268435456;"); // 256MB memory map
        await ExecutePragmaAsync(_initConnection, "PRAGMA foreign_keys=ON;");

        await CreateTablesAsync(_initConnection);
    }

    private static async Task CreateTablesAsync(SqliteConnection conn)
    {
        const string schema = """
            CREATE TABLE IF NOT EXISTS watched_folders (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                path        TEXT    NOT NULL UNIQUE,
                include_sub INTEGER NOT NULL DEFAULT 1,
                date_added  TEXT    NOT NULL DEFAULT (datetime('now')),
                last_scanned TEXT
            );

            CREATE TABLE IF NOT EXISTS images (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_path       TEXT    NOT NULL UNIQUE,
                file_name       TEXT    NOT NULL,
                file_hash       TEXT,
                file_size       INTEGER NOT NULL DEFAULT 0,
                width           INTEGER NOT NULL DEFAULT 0,
                height          INTEGER NOT NULL DEFAULT 0,
                date_taken      TEXT,
                date_modified   TEXT    NOT NULL,
                date_indexed    TEXT    NOT NULL DEFAULT (datetime('now')),
                camera_make     TEXT,
                camera_model    TEXT,
                lens_model      TEXT,
                focal_length    REAL,
                aperture        REAL,
                exposure_time   TEXT,
                iso             INTEGER,
                gps_latitude    REAL,
                gps_longitude   REAL,
                rating          INTEGER,
                orientation     INTEGER NOT NULL DEFAULT 1,
                media_type      INTEGER NOT NULL DEFAULT 0,
                video_duration  REAL
            );

            -- Note: thumbnails table removed - we now use Windows thumbnail cache instead
            -- This eliminates storage duplication and leverages OS-level optimization

            CREATE TABLE IF NOT EXISTS tags (
                image_id    INTEGER NOT NULL,
                tag         TEXT    NOT NULL,
                source      INTEGER NOT NULL DEFAULT 0,
                confidence  REAL    NOT NULL DEFAULT 1.0,
                PRIMARY KEY (image_id, tag),
                FOREIGN KEY (image_id) REFERENCES images(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS persons (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT    NOT NULL,
                thumbnail   BLOB,
                face_count  INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS face_regions (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                image_id    INTEGER NOT NULL,
                x           REAL    NOT NULL,
                y           REAL    NOT NULL,
                width       REAL    NOT NULL,
                height      REAL    NOT NULL,
                person_name TEXT,
                person_id   INTEGER,
                embedding   BLOB,
                confidence  REAL    NOT NULL DEFAULT 0.0,
                FOREIGN KEY (image_id) REFERENCES images(id) ON DELETE CASCADE,
                FOREIGN KEY (person_id) REFERENCES persons(id) ON DELETE SET NULL
            );

            -- Indexes for common queries
            CREATE INDEX IF NOT EXISTS idx_images_file_path ON images(file_path);
            CREATE INDEX IF NOT EXISTS idx_images_date_taken ON images(date_taken);
            CREATE INDEX IF NOT EXISTS idx_images_file_hash ON images(file_hash);
            CREATE INDEX IF NOT EXISTS idx_tags_tag ON tags(tag);
            CREATE INDEX IF NOT EXISTS idx_tags_image_id ON tags(image_id);
            CREATE INDEX IF NOT EXISTS idx_face_regions_image_id ON face_regions(image_id);
            CREATE INDEX IF NOT EXISTS idx_face_regions_person_id ON face_regions(person_id);
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = schema;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Creates and returns a new open connection from the pool.
    /// Callers should dispose the connection when done.
    /// </summary>
    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>
    /// Returns the shared connection for backward compatibility.
    /// Prefer CreateConnection() for thread-safe access.
    /// </summary>
    public SqliteConnection GetConnection()
    {
        if (_initConnection is null || _initConnection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("Database not initialized. Call InitializeAsync first.");
        return _initConnection;
    }

    private static async Task ExecutePragmaAsync(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        _initConnection?.Dispose();
    }
}
