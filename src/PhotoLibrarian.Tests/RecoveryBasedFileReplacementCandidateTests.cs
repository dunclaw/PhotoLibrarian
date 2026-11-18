// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Services;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class RecoveryBasedFileReplacementCandidateTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"PhotoLibrarian-replace-candidate-{Guid.NewGuid():N}");

    public RecoveryBasedFileReplacementCandidateTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task PublishesWithOriginalAndJournalRetainedAndPreservesExistingNamedStream()
    {
        var source = Path.Combine(_directory, "source.bin");
        var replacement = Path.Combine(_directory, "replacement.bin");
        var backup = Path.Combine(_directory, "recovery", "source.before.bin");
        var manifestPath = Path.Combine(_directory, "recovery", "publication.json");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        byte[] before = [1, 2, 3, 4];
        byte[] after = [5, 6, 7, 8, 9];
        await File.WriteAllBytesAsync(source, before);
        await File.WriteAllBytesAsync(replacement, after);
        await File.WriteAllTextAsync(source + ":review", "review-state");
        var creationTime = File.GetCreationTimeUtc(source);
        File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.Hidden);
        var expectedHash = Convert.ToHexString(SHA256.HashData(before));

        var result = await RecoveryBasedFileReplacementCandidate.PublishAsync(
            source, replacement, backup, manifestPath, expectedHash);

        Assert.Equal(RecoveryBasedPublicationStatus.PublishedWithBackup, result.Status);
        Assert.Equal(after, await File.ReadAllBytesAsync(source));
        Assert.Equal(before, await File.ReadAllBytesAsync(backup));
        Assert.Equal("review-state", (await File.ReadAllTextAsync(source + ":review")).Trim());
        Assert.Equal("review-state", (await File.ReadAllTextAsync(backup + ":review")).Trim());
        Assert.Equal(creationTime, File.GetCreationTimeUtc(source));
        Assert.True(File.GetAttributes(source).HasFlag(FileAttributes.Hidden));
        Assert.False(File.Exists(replacement));
        Assert.True(File.Exists(manifestPath));
        var manifest = JsonSerializer.Deserialize<RecoveryBasedPublicationManifest>(
            await File.ReadAllTextAsync(manifestPath));
        Assert.NotNull(manifest);
        Assert.Equal(source, manifest.SourcePath);
        Assert.Equal(replacement, manifest.ReplacementPath);
        Assert.Equal(backup, manifest.BackupPath);
        Assert.Equal(expectedHash, manifest.ExpectedSourceHash);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(after)), manifest.ReplacementHash);
        Assert.Equal(RecoveryBasedRecoveryStatus.PublishedWithBackup,
            (await RecoveryBasedFileReplacementCandidate.InspectAsync(manifestPath)).Status);

        await using (var changedBackup = new FileStream(
            backup, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            changedBackup.SetLength(0);
            await changedBackup.WriteAsync(new byte[] { 9, 9 });
            await changedBackup.FlushAsync();
        }
        Assert.Equal(RecoveryBasedRecoveryStatus.Conflict,
            (await RecoveryBasedFileReplacementCandidate.InspectAsync(manifestPath)).Status);
    }

    [Fact]
    public async Task ChangedSourceIsRejectedBeforeJournalOrPublication()
    {
        var source = Path.Combine(_directory, "source.bin");
        var replacement = Path.Combine(_directory, "replacement.bin");
        var backup = Path.Combine(_directory, "recovery", "source.before.bin");
        var manifestPath = Path.Combine(_directory, "recovery", "publication.json");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        byte[] before = [1, 2, 3];
        byte[] changed = [1, 2, 4];
        byte[] after = [5, 6];
        await File.WriteAllBytesAsync(source, changed);
        await File.WriteAllBytesAsync(replacement, after);

        var result = await RecoveryBasedFileReplacementCandidate.PublishAsync(
            source, replacement, backup, manifestPath,
            Convert.ToHexString(SHA256.HashData(before)));

        Assert.Equal(RecoveryBasedPublicationStatus.Conflict, result.Status);
        Assert.Equal(changed, await File.ReadAllBytesAsync(source));
        Assert.Equal(after, await File.ReadAllBytesAsync(replacement));
        Assert.False(File.Exists(backup));
        Assert.False(File.Exists(manifestPath));
    }

    [Fact]
    public async Task NamedStreamWriteAcrossReplacementIsReportedAndRetainedForRecovery()
    {
        var source = Path.Combine(_directory, "source.bin");
        var replacement = Path.Combine(_directory, "replacement.bin");
        var backup = Path.Combine(_directory, "recovery", "source.before.bin");
        var manifestPath = Path.Combine(_directory, "recovery", "publication.json");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        byte[] before = [1, 2, 3];
        byte[] after = [4, 5, 6];
        await File.WriteAllBytesAsync(source, before);
        await File.WriteAllBytesAsync(replacement, after);
        await File.WriteAllTextAsync(source + ":review", "before");
        await using var openedStream = new FileStream(
            source + ":review", FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        var result = await RecoveryBasedFileReplacementCandidate.PublishWithPostReplaceObserverAsync(
            source, replacement, backup, manifestPath,
            Convert.ToHexString(SHA256.HashData(before)), () =>
            {
                openedStream.Position = 0;
                openedStream.SetLength(0);
                openedStream.Write("during"u8);
                openedStream.Flush();
            });

        await openedStream.DisposeAsync();
        Assert.Equal(RecoveryBasedPublicationStatus.RecoveryRequired, result.Status);
        Assert.Equal(after, await File.ReadAllBytesAsync(source));
        Assert.Equal(before, await File.ReadAllBytesAsync(backup));
        Assert.Equal("before", (await File.ReadAllTextAsync(source + ":review")).Trim());
        Assert.Equal("during", (await File.ReadAllTextAsync(backup + ":review")).Trim());
        Assert.True(File.Exists(manifestPath));
        Assert.Equal(RecoveryBasedRecoveryStatus.Conflict,
            (await RecoveryBasedFileReplacementCandidate.InspectAsync(manifestPath)).Status);
    }

    [Fact]
    public async Task StagedNamedStreamCannotReplaceDifferentSourceStream()
    {
        var source = Path.Combine(_directory, "source.bin");
        var replacement = Path.Combine(_directory, "replacement.bin");
        var backup = Path.Combine(_directory, "recovery", "source.before.bin");
        var manifestPath = Path.Combine(_directory, "recovery", "publication.json");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        byte[] sourceBytes = [1, 2, 3];
        byte[] replacementBytes = [4, 5, 6];
        await File.WriteAllBytesAsync(source, sourceBytes);
        await File.WriteAllBytesAsync(replacement, replacementBytes);
        await File.WriteAllTextAsync(source + ":review", "source-state");
        await File.WriteAllTextAsync(replacement + ":review", "stale-stage-state");

        var result = await RecoveryBasedFileReplacementCandidate.PublishAsync(
            source, replacement, backup, manifestPath,
            Convert.ToHexString(SHA256.HashData(sourceBytes)));

        Assert.Equal(RecoveryBasedPublicationStatus.Conflict, result.Status);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));
        Assert.Equal(replacementBytes, await File.ReadAllBytesAsync(replacement));
        Assert.Equal("source-state", (await File.ReadAllTextAsync(source + ":review")).Trim());
        Assert.Equal("stale-stage-state", (await File.ReadAllTextAsync(replacement + ":review")).Trim());
        Assert.False(File.Exists(backup));
        Assert.False(File.Exists(manifestPath));
    }

    [Fact]
    public async Task SharingViolationRetainsJournalAndBothRecoverableFiles()
    {
        var source = Path.Combine(_directory, "source.bin");
        var replacement = Path.Combine(_directory, "replacement.bin");
        var backup = Path.Combine(_directory, "recovery", "source.before.bin");
        var manifestPath = Path.Combine(_directory, "recovery", "publication.json");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        byte[] before = [1, 2, 3];
        byte[] after = [4, 5, 6];
        await File.WriteAllBytesAsync(source, before);
        await File.WriteAllBytesAsync(replacement, after);
        await using var sharedReader = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = await RecoveryBasedFileReplacementCandidate.PublishAsync(
            source, replacement, backup, manifestPath,
            Convert.ToHexString(SHA256.HashData(before)));

        Assert.Equal(RecoveryBasedPublicationStatus.Busy, result.Status);
        Assert.Equal(before, await File.ReadAllBytesAsync(source));
        Assert.Equal(after, await File.ReadAllBytesAsync(replacement));
        Assert.True(File.Exists(manifestPath));
        Assert.False(File.Exists(backup));
        Assert.Equal(RecoveryBasedRecoveryStatus.NotPublished,
            (await RecoveryBasedFileReplacementCandidate.InspectAsync(manifestPath)).Status);
    }

    [Fact]
    public async Task DuplicateManifestPropertiesAreRejected()
    {
        var source = Path.Combine(_directory, "source.bin");
        var replacement = Path.Combine(_directory, "replacement.bin");
        var backup = Path.Combine(_directory, "recovery", "source.before.bin");
        var manifestPath = Path.Combine(_directory, "recovery", "publication.json");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        byte[] before = [1, 2, 3];
        byte[] after = [4, 5, 6];
        await File.WriteAllBytesAsync(source, before);
        await File.WriteAllBytesAsync(replacement, after);

        var result = await RecoveryBasedFileReplacementCandidate.PublishAsync(
            source, replacement, backup, manifestPath,
            Convert.ToHexString(SHA256.HashData(before)));
        Assert.Equal(RecoveryBasedPublicationStatus.PublishedWithBackup, result.Status);

        var manifest = await File.ReadAllTextAsync(manifestPath);
        var duplicateManifest = manifest.Replace(
            "\"SchemaVersion\": 1,",
            "\"SchemaVersion\": 1,\n  \"SchemaVersion\": 1,",
            StringComparison.Ordinal);
        Assert.NotEqual(manifest, duplicateManifest);
        await File.WriteAllTextAsync(manifestPath, duplicateManifest);

        Assert.Equal(RecoveryBasedRecoveryStatus.Invalid,
            (await RecoveryBasedFileReplacementCandidate.InspectAsync(manifestPath)).Status);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
