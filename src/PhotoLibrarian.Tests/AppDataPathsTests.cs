// Copyright (c) Duncan Lawler. All Rights Reserved.

using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class AppDataPathsTests
{
    [Fact]
    public void ModelDirectory_PreservesExistingLocalAppDataLocation()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoLibrarian",
            "Models");

        Assert.Equal(expected, AppDataPaths.ModelDirectory);
    }

    [Fact]
    public void OriginalBackupDirectory_PreservesExistingLocalAppDataLocation()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoLibrarian",
            "Originals");

        Assert.Equal(expected, AppDataPaths.OriginalBackupDirectory);
    }

    [Fact]
    public void CacheDatabaseFile_PreservesExistingLocalAppDataLocation()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoLibrarian",
            "cache.db");

        Assert.Equal(expected, AppDataPaths.CacheDatabaseFile);
    }

    [Fact]
    public void PresetCatalogFile_PreservesExistingLocalAppDataLocation()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoLibrarian",
            "presets.json");

        Assert.Equal(expected, AppDataPaths.PresetCatalogFile);
    }

    [Fact]
    public void EditHistoryDirectory_PreservesExistingLocalAppDataLocation()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoLibrarian",
            "EditHistory");

        Assert.Equal(expected, AppDataPaths.EditHistoryDirectory);
    }

    [Fact]
    public void ShareCopiesDirectory_PreservesExistingLocalAppDataLocation()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoLibrarian",
            "ShareCopies");

        Assert.Equal(expected, AppDataPaths.ShareCopiesDirectory);
    }
}
