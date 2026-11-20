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
}
