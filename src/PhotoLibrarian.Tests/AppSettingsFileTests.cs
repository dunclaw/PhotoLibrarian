// Copyright (c) Duncan Lawler. All Rights Reserved.

using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class AppSettingsFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"PhotoLibrarian-AppSettings-{Guid.NewGuid():N}");

    private sealed record Preferences(int Value);

    [Fact]
    public void JsonSettings_RoundTripAndReportMissing()
    {
        var path = Path.Combine(_directory, "preferences.json");

        Assert.Equal(AppSettingsReadStatus.Missing, AppSettingsFile.ReadJson<Preferences>(path).Status);
        AppSettingsFile.WriteJson(path, new Preferences(42));
        var result = AppSettingsFile.ReadJson<Preferences>(path);

        Assert.Equal(AppSettingsReadStatus.Success, result.Status);
        Assert.Equal(new Preferences(42), result.Value);
        Assert.Equal("""{"Value":42}""", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"Value":"wrong type"}""")]
    public void CorruptJson_IsReportedWithoutModifyingFiles(string json)
    {
        var path = Path.Combine(_directory, "preferences.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, json);

        var result = AppSettingsFile.ReadJson<Preferences>(path);

        Assert.Equal(AppSettingsReadStatus.Corrupt, result.Status);
        Assert.Null(result.Value);
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackupRecoversMissingOrCorruptPrimary_AndSurvivesNextSave(bool missing)
    {
        var path = Path.Combine(_directory, "preferences.json");
        AppSettingsFile.WriteJson(path, new Preferences(1));
        AppSettingsFile.WriteJson(path, new Preferences(2));
        if (missing)
            File.Delete(path);
        else
            File.WriteAllText(path, "corrupt");

        var result = AppSettingsFile.ReadJson<Preferences>(path);
        Assert.Equal(AppSettingsReadStatus.RecoveredFromBackup, result.Status);
        Assert.Equal(new Preferences(1), result.Value);
        AppSettingsFile.WriteJson(path, new Preferences(3));
        Assert.Equal(new Preferences(3), AppSettingsFile.ReadJson<Preferences>(path).Value);
        Assert.Equal("""{"Value":1}""", File.ReadAllText(path + ".bak"));
        Assert.Empty(Directory.GetFiles(_directory, "preferences.json.*.tmp"));
    }

    [Fact]
    public void CorruptBackup_DoesNotTurnMissingOrCorruptPrimaryIntoSuccess()
    {
        var path = Path.Combine(_directory, "preferences.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path + ".bak", "corrupt");

        Assert.Equal(AppSettingsReadStatus.Corrupt, AppSettingsFile.ReadJson<Preferences>(path).Status);
        File.WriteAllText(path, "also corrupt");
        Assert.Equal(AppSettingsReadStatus.Corrupt, AppSettingsFile.ReadJson<Preferences>(path).Status);
    }

    [Fact]
    public void SemanticValidation_AppliesToPrimaryBackupAndBackupPreservation()
    {
        var path = Path.Combine(_directory, "preferences.json");
        static bool IsValid(Preferences value) => value.Value > 0;
        AppSettingsFile.WriteJson(path, new Preferences(1), isValid: IsValid);
        AppSettingsFile.WriteJson(path, new Preferences(2), isValid: IsValid);
        File.WriteAllText(path, """{"Value":0}""");

        var recovered = AppSettingsFile.ReadJson<Preferences>(path, IsValid);
        Assert.Equal(AppSettingsReadStatus.RecoveredFromBackup, recovered.Status);
        Assert.Equal(new Preferences(1), recovered.Value);
        AppSettingsFile.WriteJson(path, new Preferences(3), isValid: IsValid);
        Assert.Equal("""{"Value":1}""", File.ReadAllText(path + ".bak"));

        File.WriteAllText(path, """{"Value":0}""");
        File.WriteAllText(path + ".bak", """{"Value":-1}""");
        var invalid = AppSettingsFile.ReadJson<Preferences>(path, IsValid);
        Assert.Equal(AppSettingsReadStatus.Corrupt, invalid.Status);
        Assert.Null(invalid.Value);
    }

    [Fact]
    public void InaccessiblePrimary_ReportsFailureInsteadOfRecoveringBackup()
    {
        var path = Path.Combine(_directory, "preferences.json");
        AppSettingsFile.WriteJson(path, new Preferences(1));
        AppSettingsFile.WriteJson(path, new Preferences(2));
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.ThrowsAny<IOException>(() => AppSettingsFile.ReadJson<Preferences>(path));
        Assert.ThrowsAny<IOException>(() => AppSettingsFile.WriteJson(path, new Preferences(3)));
        Assert.Equal("""{"Value":1}""", File.ReadAllText(path + ".bak"));
    }

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
