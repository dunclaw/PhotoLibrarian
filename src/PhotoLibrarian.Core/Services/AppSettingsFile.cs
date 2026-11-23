// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Text.Json;

namespace PhotoLibrarian.Core.Services;

public enum AppSettingsReadStatus
{
    Success,
    Missing,
    Corrupt,
    RecoveredFromBackup
}

public sealed record AppSettingsReadResult<T>(AppSettingsReadStatus Status, T? Value) where T : class;

public static class AppSettingsFile
{
    public static AppSettingsReadResult<T> ReadJson<T>(string path, Func<T, bool>? isValid = null) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var primary = ReadJsonFile(path, isValid);
        if (primary.Status == AppSettingsReadStatus.Success)
            return primary;

        var backup = ReadJsonFile(path + ".bak", isValid);
        if (backup.Status == AppSettingsReadStatus.Success)
            return new(AppSettingsReadStatus.RecoveredFromBackup, backup.Value);

        return new(
            primary.Status == AppSettingsReadStatus.Corrupt || backup.Status == AppSettingsReadStatus.Corrupt
                ? AppSettingsReadStatus.Corrupt
                : AppSettingsReadStatus.Missing,
            null);
    }

    public static void WriteJson<T>(
        string path, T value, JsonSerializerOptions? options = null, Func<T, bool>? isValid = null) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(value);

        // Do not replace the only good backup with a corrupt primary after recovery.
        var preserveBackup = File.Exists(path + ".bak") &&
            ReadJsonFile(path, isValid).Status == AppSettingsReadStatus.Corrupt;
        WriteAllText(path, JsonSerializer.Serialize(value, options), preserveBackup);
    }

    public static void WriteAllText(string path, string contents)
        => WriteAllText(path, contents, preserveBackup: false);

    private static AppSettingsReadResult<T> ReadJsonFile<T>(string path, Func<T, bool>? isValid) where T : class
    {
        if (!File.Exists(path))
            return new(AppSettingsReadStatus.Missing, null);

        try
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            return value is null || (isValid is not null && !isValid(value))
                ? new(AppSettingsReadStatus.Corrupt, null)
                : new(AppSettingsReadStatus.Success, value);
        }
        catch (JsonException)
        {
            return new(AppSettingsReadStatus.Corrupt, null);
        }
    }

    private static void WriteAllText(string path, string contents, bool preserveBackup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        if (Directory.Exists(path))
            throw new UnauthorizedAccessException($"A directory exists at the settings file path '{path}'.");

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, contents);
            if (File.Exists(path))
            {
                File.Replace(
                    temporaryPath,
                    path,
                    preserveBackup ? null : path + ".bak",
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
