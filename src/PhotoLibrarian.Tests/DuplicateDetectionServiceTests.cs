// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using System.Security.Cryptography;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class DuplicateDetectionServiceTests
{
    [Fact]
    public async Task ScanAsync_FindsExactAndResizedReencodedPhotosButNotDissimilarImages()
    {
        var directory = CreateTestDirectory();
        var originalPath = Path.Combine(directory, "original.png");
        var exactCopyPath = Path.Combine(directory, "exact-copy.png");
        var resizedPath = Path.Combine(directory, "resized.jpg");
        var unrelatedPath = Path.Combine(directory, "unrelated.png");

        try
        {
            await WicTestImage.CreateAsync(
                originalPath,
                192,
                128,
                jpeg: false,
                color: (x, y) => SmoothPhotoColor(x, y, 192, 128));
            File.Copy(originalPath, exactCopyPath);
            await WicTestImage.CreateAsync(
                resizedPath,
                96,
                64,
                jpeg: true,
                color: (x, y) => SmoothPhotoColor(x, y, 96, 64));
            await WicTestImage.CreateAsync(
                unrelatedPath,
                96,
                64,
                jpeg: false,
                color: (x, y) => UnrelatedPhotoColor(x, y, 96, 64));

            using var database = new CacheDatabase(Path.Combine(directory, "cache.db"));
            await database.InitializeAsync();
            var repository = new ImageRepository(database);
            foreach (var path in new[] { originalPath, exactCopyPath, resizedPath, unrelatedPath })
                await repository.UpsertImageAsync(CreateEntry(path));

            var result = await new DuplicateDetectionService(repository).ScanAsync();

            var exact = Assert.Single(result.Groups.Where(
                group => group.Kind == DuplicateMatchKind.Exact));
            Assert.Equal(2, exact.Files.Count);
            Assert.Contains(exact.Files, file => file.Entry.FilePath == originalPath);
            Assert.Contains(exact.Files, file => file.Entry.FilePath == exactCopyPath);
            var near = Assert.Single(result.Groups.Where(
                group => group.Kind == DuplicateMatchKind.Near));
            Assert.Equal(2, near.Files.Count);
            Assert.Contains(near.Files, file => file.Entry.FilePath == resizedPath);
            Assert.Contains(near.Files, file =>
                file.Entry.FilePath == originalPath || file.Entry.FilePath == exactCopyPath);
            Assert.DoesNotContain(near.Files, file => file.Entry.FilePath == unrelatedPath);
            Assert.Null(result.Groups.First(group => group.Kind == DuplicateMatchKind.Exact).HammingDistance);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FindGroups_SeparatesExactGroupsFromNearPairCandidates()
    {
        var files = new[]
        {
            Snapshot("first.jpg", "same", 0b0000),
            Snapshot("exact-copy.jpg", "same", 0b0000),
            Snapshot("reencoded.jpg", "different", 0b0111),
            Snapshot("unrelated.jpg", "other", ulong.MaxValue)
        };

        var groups = DuplicateDetectionService.FindGroups(files, out var limitReached);

        Assert.False(limitReached);
        var exact = Assert.Single(groups.Where(group => group.Kind == DuplicateMatchKind.Exact));
        Assert.Equal(2, exact.Files.Count);
        var near = Assert.Single(groups.Where(group => group.Kind == DuplicateMatchKind.Near));
        Assert.Equal(3, near.HammingDistance);
        Assert.Contains(
            near.Files,
            file => file.Entry.FileName is "first.jpg" or "exact-copy.jpg");
        Assert.Contains(near.Files, file => file.Entry.FileName == "reencoded.jpg");
        Assert.DoesNotContain(near.Files, file => file.Entry.FileName == "unrelated.jpg");
    }

    [Fact]
    public async Task ScanAsync_ReportsMissingPhotosAndHonorsCancellation()
    {
        var directory = CreateTestDirectory();
        var existingPath = Path.Combine(directory, "present.png");
        var missingPath = Path.Combine(directory, "missing.png");

        try
        {
            await WicTestImage.CreateAsync(existingPath, 32, 24);
            using var database = new CacheDatabase(Path.Combine(directory, "cache.db"));
            await database.InitializeAsync();
            var repository = new ImageRepository(database);
            await repository.UpsertImageAsync(CreateEntry(existingPath));
            await repository.UpsertImageAsync(new ImageEntry
            {
                FilePath = missingPath,
                FileName = Path.GetFileName(missingPath),
                FileSize = 1,
                DateModified = DateTime.UtcNow
            });

            var result = await new DuplicateDetectionService(repository).ScanAsync();

            Assert.Equal(1, result.DiagnosticCount);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.FilePath == missingPath);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new DuplicateDetectionService(repository).ScanAsync(
                    cancellationToken: cancellation.Token));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteDuplicateCandidateToRecycleBinAsync_RejectsChangedContent()
    {
        var directory = CreateTestDirectory();
        var path = Path.Combine(directory, "photo.jpg");
        var original = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(path, original);
        var entry = CreateEntry(path);
        var snapshot = new DuplicateFileSnapshot(
            entry,
            original.Length,
            File.GetLastWriteTimeUtc(path),
            Convert.ToHexString(SHA256.HashData(original)),
            0);

        try
        {
            using var database = new CacheDatabase(Path.Combine(directory, "cache.db"));
            await database.InitializeAsync();
            var operations = new PhotoOperationsService(
                new ImageRepository(database),
                new OriginalBackupService(Path.Combine(directory, "backups")));

            await File.WriteAllBytesAsync(path, new byte[] { 5, 4, 3, 2, 1 });
            File.SetLastWriteTimeUtc(path, snapshot.LastWriteTimeUtc);

            var exception = await Assert.ThrowsAsync<DuplicateCandidateValidationException>(
                () => operations.DeleteDuplicateCandidateToRecycleBinAsync(snapshot));
            Assert.Equal("DuplicateContentChanged", exception.ResourceKey);
            Assert.True(File.Exists(path));
            Assert.Equal(new byte[] { 5, 4, 3, 2, 1 }, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-Duplicate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static ImageEntry CreateEntry(string path)
    {
        var file = new FileInfo(path);
        return new ImageEntry
        {
            FilePath = path,
            FileName = file.Name,
            FileSize = file.Length,
            DateModified = file.LastWriteTimeUtc,
            Width = 96,
            Height = 64
        };
    }

    private static DuplicateFileSnapshot Snapshot(
        string fileName,
        string sha256,
        ulong perceptualHash)
    {
        var entry = new ImageEntry
        {
            FilePath = fileName,
            FileName = fileName,
            DateModified = DateTime.UnixEpoch
        };
        return new DuplicateFileSnapshot(entry, 1, DateTime.UnixEpoch, sha256, perceptualHash);
    }

    private static (byte B, byte G, byte R) SmoothPhotoColor(uint x, uint y, uint width, uint height)
    {
        var normalizedX = x / (double)width;
        var normalizedY = y / (double)height;
        var spot = Math.Exp(-((normalizedX - 0.68) * (normalizedX - 0.68) +
            (normalizedY - 0.36) * (normalizedY - 0.36)) * 30);
        return (
            (byte)(30 + 100 * normalizedY),
            (byte)(25 + 150 * normalizedX),
            (byte)(40 + 125 * spot));
    }

    private static (byte B, byte G, byte R) UnrelatedPhotoColor(
        uint x,
        uint y,
        uint width,
        uint height)
    {
        var normalizedX = x / (double)width;
        var normalizedY = y / (double)height;
        return (
            (byte)(220 - 160 * normalizedX),
            (byte)(200 - 150 * normalizedY),
            (byte)(25 + 170 * normalizedY));
    }
}
