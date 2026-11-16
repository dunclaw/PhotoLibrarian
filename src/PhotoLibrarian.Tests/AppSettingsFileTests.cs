// Copyright (c) Duncan Lawler. All Rights Reserved.

using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class AppSettingsFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"PhotoLibrarian-AppSettings-{Guid.NewGuid():N}");

    [Fact]
    public void ReplacingSettings_PreservesPreviousContentsInBackup()
    {
        var path = Path.Combine(_directory, "preferences.json");

        AppSettingsFile.WriteAllText(path, """{"Value":1}""");
        AppSettingsFile.WriteAllText(path, """{"Value":2}""");

        Assert.Equal("""{"Value":2}""", File.ReadAllText(path));
        Assert.Equal("""{"Value":1}""", File.ReadAllText(path + ".bak"));
        Assert.Empty(Directory.GetFiles(_directory, "preferences.json.*.tmp"));
    }

    [Fact]
    public void FailedReplacement_LeavesExistingFileAndCleansTemporaryFile()
    {
        var path = Path.Combine(_directory, "preferences.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "existing");
        Directory.CreateDirectory(path + ".bak");

        var exception = Record.Exception(
            () => AppSettingsFile.WriteAllText(path, "replacement"));

        Assert.True(exception is IOException or UnauthorizedAccessException);
        Assert.Equal("existing", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_directory, "preferences.json.*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
