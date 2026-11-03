// Copyright (c) Duncan Lawler. All Rights Reserved.

namespace PhotoLibrarian.Core.Services;

/// <summary>Lexical watch membership, independent of drive availability.</summary>
public static class WatchedFolderScope
{
    public static bool ContainsFile(string filePath, string folderPath, bool includeSubfolders)
    {
        var root = Path.TrimEndingDirectorySeparator(folderPath);
        var parent = Path.GetDirectoryName(filePath);
        if (parent is null)
            return false;

        if (string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!includeSubfolders)
            return false;

        var relative = Path.GetRelativePath(root, filePath);
        return !Path.IsPathRooted(relative) &&
            relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }
}
