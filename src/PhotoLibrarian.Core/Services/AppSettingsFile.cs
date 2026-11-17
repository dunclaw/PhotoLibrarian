// Copyright (c) Duncan Lawler. All Rights Reserved.

namespace PhotoLibrarian.Core.Services;

public static class AppSettingsFile
{
    public static void WriteAllText(string path, string contents)
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
                    path + ".bak",
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
