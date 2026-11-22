// Copyright (c) Duncan Lawler. All Rights Reserved.

namespace PhotoLibrarian.Core.Services;

public static class AppDataPaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoLibrarian");

    public static string OriginalBackupDirectory => Path.Combine(Root, "Originals");

    public static string ModelDirectory => Path.Combine(Root, "Models");

    public static string CacheDatabaseFile => Path.Combine(Root, "cache.db");

    public static string PresetCatalogFile => Path.Combine(Root, "presets.json");

    public static string EditHistoryDirectory => Path.Combine(Root, "EditHistory");

    public static string ShareCopiesDirectory => Path.Combine(Root, "ShareCopies");

    public static string SettingsFile(string fileName) =>
        Path.Combine(Root, fileName);
}
