// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.Services;
using PhotoLibrarian.ViewModels;
using System.Security.Cryptography;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class DuplicateDetectionServiceTests
{
    [Fact]
    public async Task ScanDiagnostics_DistinguishSameNameAtDifferentIndexedPaths()
    {
        var directory = CreateTestDirectory();
        try
        {
            var existing = Path.Combine(directory, "photo.png");
            var missing = Path.Combine(directory, "old-folder", "photo.png");
            await WicTestImage.CreateAsync(existing, 32, 24);
            using var database = new CacheDatabase(Path.Combine(directory, "cache.db"));
            await database.InitializeAsync();
            var repository = new ImageRepository(database);
            var entry = CreateEntry(existing);
            await repository.UpsertImageAsync(entry);
            await repository.UpsertImageAsync(new ImageEntry
            {
                FilePath = missing,
                FileName = "photo.png",
                FileSize = entry.FileSize,
                DateModified = entry.DateModified
            });
            var review = new DuplicateReviewViewModel(
                new DuplicateDetectionService(repository),
                new PhotoOperationsService(repository,
                    new OriginalBackupService(Path.Combine(directory, "backups"))));
            await review.OpenForImagesAsync([entry]);

            Assert.True(review.HasDiagnostics);
            Assert.Contains(missing, review.DiagnosticsDetails, StringComparison.Ordinal);
            Assert.DoesNotContain(existing + ":", review.DiagnosticsDetails, StringComparison.Ordinal);
            Assert.Contains("outside", review.DiagnosticsMessage, StringComparison.Ordinal);
            Assert.True(File.Exists(existing));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_MeasuresColdAndWarmSyntheticThumbnailThroughput()
    {
        var directory = CreateTestDirectory();
        try
        {
            var source = Path.Combine(directory, "source.png");
            await WicTestImage.CreateAsync(source, 192, 128);
            using var database = new CacheDatabase(Path.Combine(directory, "cache.db"));
            await database.InitializeAsync();
            var repository = new ImageRepository(database);
            var seeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < 256; index++)
            {
                var path = Path.Combine(directory, $"{index}.png");
                File.Copy(source, path);
                await repository.UpsertImageAsync(CreateEntry(path));
                if (index == 0)
                    seeds.Add(path);
            }
            var service = new DuplicateDetectionService(repository);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var cold = await service.ScanAsync(seedPaths: seeds);
            var coldTime = stopwatch.Elapsed;
            stopwatch.Restart();
            var warm = await service.ScanAsync(seedPaths: seeds);
            Console.WriteLine(
                $"256 synthetic 192x128 PNGs: cold {coldTime.TotalMilliseconds:N0} ms, " +
                $"warm {stopwatch.Elapsed.TotalMilliseconds:N0} ms. Not real-library throughput.");
            Assert.Empty(cold.Diagnostics);
            Assert.Empty(warm.Diagnostics);
            Assert.Equal(256, Assert.Single(cold.Groups).Files.Count);
            Assert.Equal(256, Assert.Single(warm.Groups).Files.Count);

            var hookCalls = new List<string>();
            var viewModel = new DuplicateReviewViewModel(service,
                new PhotoOperationsService(repository, new OriginalBackupService(Path.Combine(directory, "backups"))))
            {
                BeforeScanAsync = () =>
                {
                    hookCalls.Add("pause");
                    return Task.CompletedTask;
                },
                AfterScan = () => hookCalls.Add("resume")
            };
            await viewModel.OpenForImagesAsync([CreateEntry(seeds.Single())]);
            Assert.Equal(new[] { "pause", "resume" }, hookCalls);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsync_SeedScopeFindsOutsideMatchesAndReusesOnlyUnchangedCacheEntries()
    {
        var directory = CreateTestDirectory();
        try
        {
            var seedPath = Path.Combine(directory, "seed.png");
            var outsidePath = Path.Combine(directory, "outside.png");
            var otherPath = Path.Combine(directory, "unrelated.png");
            var otherCopyPath = Path.Combine(directory, "unrelated-copy.png");
            await WicTestImage.CreateAsync(seedPath, 96, 64, jpeg: false,
                color: (x, y) => SmoothPhotoColor(x, y, 96, 64));
            File.Copy(seedPath, outsidePath);
            await WicTestImage.CreateAsync(otherPath, 96, 64, jpeg: false,
                color: (x, y) => UnrelatedPhotoColor(x, y, 96, 64));
            File.Copy(otherPath, otherCopyPath);
            using var database = new CacheDatabase(Path.Combine(directory, "cache.db"));
            await database.InitializeAsync();
            var repository = new ImageRepository(database);
            foreach (var path in new[] { seedPath, outsidePath, otherPath, otherCopyPath })
                await repository.UpsertImageAsync(CreateEntry(path));

            var seeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { seedPath };
            var result = await new DuplicateDetectionService(repository).ScanAsync(seedPaths: seeds);
            var exact = Assert.Single(result.Groups.Where(group => group.Kind == DuplicateMatchKind.Exact));
            Assert.Contains(exact.Files, file => file.Entry.FilePath == outsidePath);
            Assert.All(result.Groups, group =>
                Assert.Contains(group.Files, file => seeds.Contains(file.Entry.FilePath)));
            var cache = await repository.GetDuplicateHashesAsync(default);
            Assert.NotNull(cache[seedPath].PerceptualHash);
            Assert.NotNull(cache[seedPath].Sha256);

            // An exclusive handle proves the warm scan does not reopen unchanged files.
            using (var lockedSeed = new FileStream(seedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var lockedOutside = new FileStream(outsidePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var warmResult = await new DuplicateDetectionService(repository).ScanAsync(seedPaths: seeds);
                Assert.Empty(warmResult.Diagnostics);
                Assert.Single(warmResult.Groups.Where(group => group.Kind == DuplicateMatchKind.Exact));
            }

            await File.WriteAllBytesAsync(outsidePath, [1, 2, 3]);
            var changedResult = await new DuplicateDetectionService(repository).ScanAsync(seedPaths: seeds);
            Assert.DoesNotContain(changedResult.Groups, group =>
                group.Kind == DuplicateMatchKind.Exact &&
                group.Files.Any(file => file.Entry.FilePath == outsidePath));
            Assert.Contains(changedResult.Diagnostics, item => item.FilePath == outsidePath);
            Assert.Empty((await new DuplicateDetectionService(repository).ScanAsync(
                seedPaths: new HashSet<string>())).Groups);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FindGroups_SeededSearchScalesToTwoHundredThousandCandidates()
    {
        const int count = 200_000;
        var files = new List<DuplicateFileSnapshot>(count + 1)
        {
            Snapshot("seed.jpg", "seed", 0)
        };
        var random = new Random(41);
        for (var index = 0; index < count; index++)
        {
            var hash = (ulong)random.NextInt64() | 0xFFFF000000000000UL;
            files.Add(Snapshot($"outside-{index}.jpg", $"sha-{index}", hash));
        }
        var seeds = new HashSet<string> { "seed.jpg" };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var groups = DuplicateDetectionService.FindGroups(files, out var limited, seeds);
        stopwatch.Stop();

        Assert.Empty(groups);
        Assert.False(limited);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Seeded comparison took {stopwatch.Elapsed} for {count:N0} candidates.");
        Console.WriteLine($"Seeded duplicate comparison: {count:N0} candidates in {stopwatch.Elapsed.TotalMilliseconds:N0} ms.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            DuplicateDetectionService.FindGroups(files.Take(100).ToArray(), out _, seeds, cancellation.Token));
    }

    [Fact]
    public void ReviewViewModel_LoadsLocalizedStartupStringsWithoutPackageIdentity()
    {
        var directory = CreateTestDirectory();
        try
        {
            using var database = new CacheDatabase(Path.Combine(directory, "cache.db"));
            var repository = new ImageRepository(database);
            var viewModel = new DuplicateReviewViewModel(
                new DuplicateDetectionService(repository),
                new PhotoOperationsService(
                    repository,
                    new OriginalBackupService(Path.Combine(directory, "backups"))));

            Assert.Equal("Run a scan to look for duplicate photos.", viewModel.StatusMessage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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
    public void FindGroups_ReportsThresholdFalsePositivesAsSeparateNearSuggestions()
    {
        var files = new[]
        {
            Snapshot("baseline.jpg", "baseline", 0),
            Snapshot("possible-edit.jpg", "different-content", 0xFF),
            Snapshot("unrelated.jpg", "unrelated-content", 0x1FF00)
        };

        var groups = DuplicateDetectionService.FindGroups(files, out var limitReached);

        Assert.False(limitReached);
        var near = Assert.Single(groups);
        Assert.Equal(DuplicateMatchKind.Near, near.Kind);
        Assert.Equal(8, near.HammingDistance);
        Assert.Contains(near.Files, file => file.Entry.FileName == "baseline.jpg");
        Assert.Contains(near.Files, file => file.Entry.FileName == "possible-edit.jpg");
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteDuplicateCandidatesToRecycleBinAsync_LeavesCandidatesWhenKeeperIsMissingOrReplaced(
        bool replaceKeeper)
    {
        var directory = CreateTestDirectory();
        var keeperPath = Path.Combine(directory, "keeper.jpg");
        var candidatePath = Path.Combine(directory, "candidate.jpg");
        var originalKeeper = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(keeperPath, originalKeeper);
        await File.WriteAllBytesAsync(candidatePath, originalKeeper);
        var keeper = CreateSnapshot(keeperPath);
        var candidate = CreateSnapshot(candidatePath);
        var recycleBin = new TestRecycleBinService();

        try
        {
            using var database = new CacheDatabase(Path.Combine(directory, "cache.db"));
            await database.InitializeAsync();
            var operations = new PhotoOperationsService(
                new ImageRepository(database),
                new OriginalBackupService(Path.Combine(directory, "backups")),
                recycleBin);

            if (replaceKeeper)
            {
                await File.WriteAllBytesAsync(keeperPath, new byte[] { 4, 3, 2, 1 });
                File.SetLastWriteTimeUtc(keeperPath, keeper.LastWriteTimeUtc);
            }
            else
            {
                File.Delete(keeperPath);
            }

            var exception = await Assert.ThrowsAsync<DuplicateCandidateValidationException>(
                () => operations.DeleteDuplicateCandidatesToRecycleBinAsync(
                    keeper,
                    [candidate]));

            Assert.Empty(recycleBin.RecycledPaths);
            Assert.True(File.Exists(candidatePath));
            Assert.Equal(
                replaceKeeper ? "DuplicateContentChanged" : "DuplicateFileChanged",
                exception.ResourceKey);
            if (replaceKeeper)
                Assert.Equal(new byte[] { 4, 3, 2, 1 }, await File.ReadAllBytesAsync(keeperPath));
            else
                Assert.False(File.Exists(keeperPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteDuplicateCandidatesToRecycleBinAsync_LeavesMissingOrReplacedCandidate(
        bool replaceCandidate)
    {
        var directory = CreateTestDirectory();
        var keeperPath = Path.Combine(directory, "keeper.jpg");
        var candidatePath = Path.Combine(directory, "candidate.jpg");
        var original = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(keeperPath, original);
        await File.WriteAllBytesAsync(candidatePath, original);
        var keeper = CreateSnapshot(keeperPath);
        var candidate = CreateSnapshot(candidatePath);
        var recycleBin = new TestRecycleBinService();

        try
        {
            using var database = new CacheDatabase(Path.Combine(directory, "cache.db"));
            await database.InitializeAsync();
            var operations = new PhotoOperationsService(
                new ImageRepository(database),
                new OriginalBackupService(Path.Combine(directory, "backups")),
                recycleBin);

            if (replaceCandidate)
            {
                await File.WriteAllBytesAsync(candidatePath, new byte[] { 4, 3, 2, 1 });
                File.SetLastWriteTimeUtc(candidatePath, candidate.LastWriteTimeUtc);
            }
            else
            {
                File.Delete(candidatePath);
            }

            var result = await operations.DeleteDuplicateCandidatesToRecycleBinAsync(
                keeper,
                [candidate]);

            Assert.Empty(recycleBin.RecycledPaths);
            Assert.Empty(result.RecycledFiles);
            Assert.Equal(
                replaceCandidate ? "DuplicateContentChanged" : "DuplicateFileChanged",
                Assert.Single(result.Failures).ResourceKey);
            if (replaceCandidate)
                Assert.Equal(new byte[] { 4, 3, 2, 1 }, await File.ReadAllBytesAsync(candidatePath));
            else
                Assert.False(File.Exists(candidatePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteDuplicateCandidatesToRecycleBinAsync_ReportsMixedRecycleFailuresWithoutSuccessForFailedFile()
    {
        var directory = CreateTestDirectory();
        var keeperPath = Path.Combine(directory, "keeper.jpg");
        var removedPath = Path.Combine(directory, "removed.jpg");
        var failedPath = Path.Combine(directory, "failed.jpg");
        var canceledPath = Path.Combine(directory, "canceled.jpg");
        var content = new byte[] { 1, 2, 3, 4 };
        foreach (var path in new[] { keeperPath, removedPath, failedPath, canceledPath })
            await File.WriteAllBytesAsync(path, content);
        var snapshots = new[] { keeperPath, removedPath, failedPath, canceledPath }
            .Select(CreateSnapshot)
            .ToArray();
        var recycleBin = new TestRecycleBinService
        {
            ThrowPath = failedPath,
            FailedPath = canceledPath
        };

        try
        {
            using var database = new CacheDatabase(Path.Combine(directory, "cache.db"));
            await database.InitializeAsync();
            var repository = new ImageRepository(database);
            var operations = new PhotoOperationsService(
                repository,
                new OriginalBackupService(Path.Combine(directory, "backups")),
                recycleBin);

            var result = await operations.DeleteDuplicateCandidatesToRecycleBinAsync(
                snapshots[0],
                snapshots[1..]);

            Assert.False(File.Exists(removedPath));
            Assert.True(File.Exists(keeperPath));
            Assert.True(File.Exists(failedPath));
            Assert.True(File.Exists(canceledPath));
            Assert.Single(recycleBin.RecycledPaths);
            Assert.Single(result.RecycledFiles);
            Assert.Equal(2, result.Failures.Count);
            var failure = Assert.Single(result.Failures.Where(item => item.FilePath == failedPath));
            Assert.Equal("Synthetic recycle failure.", failure.Message);
            var canceled = Assert.Single(result.Failures.Where(item => item.FilePath == canceledPath));
            Assert.Equal("DuplicateRecycleCanceled", canceled.ResourceKey);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteDuplicateCandidatesToRecycleBinAsync_CancellationKeepsKeeperAndReportsPartialProgress()
    {
        var directory = CreateTestDirectory();
        var keeperPath = Path.Combine(directory, "keeper.jpg");
        var firstPath = Path.Combine(directory, "first.jpg");
        var secondPath = Path.Combine(directory, "second.jpg");
        var content = new byte[] { 1, 2, 3, 4 };
        foreach (var path in new[] { keeperPath, firstPath, secondPath })
            await File.WriteAllBytesAsync(path, content);
        var snapshots = new[] { keeperPath, firstPath, secondPath }
            .Select(CreateSnapshot)
            .ToArray();
        using var cancellation = new CancellationTokenSource();
        var keeperLockedDuringRemoval = false;
        var recycleBin = new TestRecycleBinService
        {
            AfterRecycle = _ =>
            {
                Assert.Throws<IOException>(() =>
                {
                    using var write = new FileStream(
                        keeperPath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.Read);
                });
                keeperLockedDuringRemoval = true;
                cancellation.Cancel();
            }
        };

        try
        {
            using var database = new CacheDatabase(Path.Combine(directory, "cache.db"));
            await database.InitializeAsync();
            var repository = new ImageRepository(database);
            var operations = new PhotoOperationsService(
                repository,
                new OriginalBackupService(Path.Combine(directory, "backups")),
                recycleBin);

            var result = await operations.DeleteDuplicateCandidatesToRecycleBinAsync(
                snapshots[0],
                snapshots[1..],
                cancellation.Token);

            Assert.True(keeperLockedDuringRemoval);
            Assert.True(File.Exists(keeperPath));
            Assert.False(File.Exists(firstPath));
            Assert.True(File.Exists(secondPath));
            Assert.Single(result.RecycledFiles);
            Assert.True(result.WasCanceled);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DeleteDuplicateCandidateToRecycleBinAsync_ReportsSidecarRecycleResult(
        bool failSidecar,
        bool throwOnSidecar)
    {
        var directory = CreateTestDirectory();
        var path = Path.Combine(directory, "photo.jpg");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4 });
        var sidecar = FaceMetadataStore.GetSidecarPathForImage(path);
        await File.WriteAllTextAsync(sidecar, "synthetic metadata");
        var snapshot = CreateSnapshot(path);
        var recycleBin = new TestRecycleBinService
        {
            FailedPath = failSidecar ? sidecar : null,
            ThrowPath = throwOnSidecar ? sidecar : null
        };

        try
        {
            using var database = new CacheDatabase(Path.Combine(directory, "cache.db"));
            await database.InitializeAsync();
            var operations = new PhotoOperationsService(
                new ImageRepository(database),
                new OriginalBackupService(Path.Combine(directory, "backups")),
                recycleBin);

            var result = await operations.DeleteDuplicateCandidateToRecycleBinAsync(snapshot);

            Assert.True(result.PhotoRecycled);
            Assert.Equal(
                failSidecar || throwOnSidecar
                    ? new[] { path }
                    : new[] { path, sidecar },
                recycleBin.RecycledPaths);
            Assert.False(File.Exists(path));
            Assert.Equal(failSidecar || throwOnSidecar, File.Exists(sidecar));
            if (failSidecar)
            {
                Assert.Equal(sidecar, result.SidecarFailure?.FilePath);
                Assert.Equal("DuplicateSidecarRecycleFailed", result.SidecarFailure?.ResourceKey);
            }
            else if (throwOnSidecar)
            {
                Assert.Equal(sidecar, result.SidecarFailure?.FilePath);
                Assert.Equal("Synthetic recycle failure.", result.SidecarFailure?.Message);
            }
            else
            {
                Assert.Null(result.SidecarFailure);
                Assert.False(File.Exists(sidecar));
            }
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

    private static DuplicateFileSnapshot CreateSnapshot(string path)
    {
        var entry = CreateEntry(path);
        var bytes = File.ReadAllBytes(path);
        return new DuplicateFileSnapshot(
            entry,
            bytes.Length,
            File.GetLastWriteTimeUtc(path),
            Convert.ToHexString(SHA256.HashData(bytes)),
            0);
    }

    private sealed class TestRecycleBinService : IRecycleBinService
    {
        public List<string> RecycledPaths { get; } = [];
        public string? FailedPath { get; init; }
        public string? ThrowPath { get; init; }
        public Action<string>? AfterRecycle { get; init; }

        public bool RecycleFile(string filePath)
        {
            if (string.Equals(filePath, ThrowPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Synthetic recycle failure.");
            if (string.Equals(filePath, FailedPath, StringComparison.OrdinalIgnoreCase))
                return false;

            File.Delete(filePath);
            RecycledPaths.Add(filePath);
            AfterRecycle?.Invoke(filePath);
            return true;
        }
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
