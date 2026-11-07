// Copyright (c) Duncan Lawler. All Rights Reserved.

using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Data;

public sealed class WatchedFolderRepository(CacheDatabase database)
{
    public async Task AddAsync(string path, bool includeSubfolders = true)
    {
        using var connection = database.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO watched_folders (path, include_sub)
            VALUES ($path, $includeSubfolders)
            """;
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        command.Parameters.AddWithValue("$includeSubfolders", includeSubfolders ? 1 : 0);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<WatchedFolder>> GetAllAsync()
    {
        using var connection = database.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, path, include_sub FROM watched_folders ORDER BY path";
        using var reader = await command.ExecuteReaderAsync();
        var folders = new List<WatchedFolder>();
        while (await reader.ReadAsync())
        {
            folders.Add(new WatchedFolder
            {
                Id = reader.GetInt64(0),
                Path = reader.GetString(1),
                IncludeSubfolders = reader.GetInt64(2) == 1
            });
        }
        return folders;
    }

    /// <summary>Detach a root, retaining all cached records and metadata for re-adding it.</summary>
    public async Task RemoveAsync(long id)
    {
        using var connection = database.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM watched_folders WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
    }
}
