// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using XmpCore;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class FileEditRecoveryTests : IDisposable
{
    private readonly string _directory = Environment.GetEnvironmentVariable("PHOTO_LIBRARIAN_RECOVERY_TEST_DIRECTORY")
        ?? Path.Combine(Path.GetTempPath(), $"PhotoLibrarian-recovery-{Guid.NewGuid():N}");

    public FileEditRecoveryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task DiscoveryReportsAFileAtTheHistoryRootInsteadOfTreatingItAsEmpty()
    {
        var root = Path.Combine(_directory, "history");
        await File.WriteAllTextAsync(root, "not a directory");

        var candidate = Assert.Single(await FileEditRecovery.FindAsync(root));
        Assert.Equal(root, candidate.TransactionDirectory);
        Assert.Equal(FileEditRecoveryStatus.Invalid, candidate.Inspection.Status);
        Assert.Equal("not a directory", await File.ReadAllTextAsync(root));
    }

    [Fact]
    public async Task DiscoveryAllowsMissingHistoryUnderAReparsePointButRejectsExistingHistory()
    {
        var target = Path.Combine(_directory, "target");
        var link = Path.Combine(_directory, "link");
        Directory.CreateDirectory(target);
        CreateSymbolicLink(link, target, directory: true);
        var root = Path.Combine(link, "history");

        Assert.Empty(await FileEditRecovery.FindAsync(root));
        Directory.CreateDirectory(Path.Combine(target, "history"));
        var candidate = Assert.Single(await FileEditRecovery.FindAsync(root));
        Assert.Equal(FileEditRecoveryStatus.Invalid, candidate.Inspection.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiscoveryRejectsUnexpectedManifestAndAttemptPathTypes(bool manifestDirectory)
    {
        var root = Path.Combine(_directory, "history");
        var transaction = Path.Combine(root, "session", "transaction");
        Directory.CreateDirectory(transaction);
        if (manifestDirectory)
            Directory.CreateDirectory(Path.Combine(transaction, "recovery.json"));
        else
            await File.WriteAllTextAsync(Path.Combine(transaction, "recovery-attempts"), "not a directory");

        var candidate = Assert.Single(await FileEditRecovery.FindAsync(root));
        Assert.Equal(FileEditRecoveryStatus.Invalid, candidate.Inspection.Status);
    }

    [Fact]
    public async Task RestoreRechecksAllHistoryBeforeApplyingASelection()
    {
        var source = Path.Combine(_directory, "source.cr3");
        var root = Path.Combine(_directory, "history");
        var first = Path.Combine(root, "first", "transaction");
        var second = Path.Combine(root, "second", "transaction");
        byte[] raw = [1, 2, 3];
        await File.WriteAllBytesAsync(source, raw);
        foreach (var directory in new[] { first, second })
        {
            Directory.CreateDirectory(directory);
            new FileEditRecoveryManifest(1, source, [], false,
            [
                new(source, new(null, Hash(raw)), new(null, Hash(raw)), null, null),
                new(Path.ChangeExtension(source, ".xmp"), new(null, null), new(null, null), null, null),
                new(source + ".xmp", new(null, null), new(null, null), null, null)
            ]).Write(directory);
        }

        var candidates = await FileEditRecovery.FindAsync(root);
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.Equal(FileEditRecoveryStatus.Ready, candidate.Inspection.Status));
        using (var historyLease = new FileStream(
            Path.Combine(root, ".recovery.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(FileEditRecoveryStatus.Busy,
                (await FileEditRecovery.RestoreAsync(root, first, source, FileEditRecoveryChoice.Before)).Status);
        }
        var result = await FileEditRecovery.RestoreAsync(root, first, source, FileEditRecoveryChoice.Before);
        Assert.Equal(FileEditRecoveryStatus.Conflict, result.Status);
        Assert.Equal(raw, await File.ReadAllBytesAsync(source));
        Assert.False(File.Exists(Path.Combine(first, "recovery.completed")));
        Directory.CreateDirectory(source + ".xmp");
        Assert.Equal(FileEditRecoveryStatus.Unavailable,
            (await FileEditRecovery.InspectAsync(first, source)).Status);
    }

    [Fact]
    public async Task DurablePublicationRefusesBusyOrUnresolvedHistory()
    {
        var source = Path.Combine(_directory, "source.cr3");
        var root = Path.Combine(_directory, "history");
        var session = Path.Combine(root, "session");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        using var transaction = await FileEditTransaction.PrepareAsync(session, [source], copies =>
        {
            File.WriteAllText(Path.ChangeExtension(copies[source], ".xmp"), Packet("after"));
            return Task.CompletedTask;
        }, durableRecovery: true, recoveryRoot: root);

        using (var historyLease = new FileStream(
            Path.Combine(root, ".recovery.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(EditHistoryStatus.Busy,
                (await transaction.MoveAsync(false, () => Task.CompletedTask)).Status);
        }
        var interrupted = Path.Combine(root, "other-session", "transaction");
        Directory.CreateDirectory(interrupted);
        await File.WriteAllTextAsync(Path.Combine(interrupted, "recovery.json"), "invalid");
        Assert.Equal(EditHistoryStatus.Conflict,
            (await transaction.MoveAsync(false, () => Task.CompletedTask)).Status);
        Assert.False(File.Exists(Path.ChangeExtension(source, ".xmp")));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task HistoryLeaseExcludesAnotherProcess()
    {
        var root = Path.Combine(_directory, "history");
        if (Environment.GetEnvironmentVariable("PHOTO_LIBRARIAN_RECOVERY_LEASE_PROBE") == "1")
        {
            var status = FileEditRecovery.TryAcquireHistoryLease(root, out var childLease);
            childLease?.Dispose();
            Environment.Exit(status.Status == FileEditRecoveryStatus.Busy ? 0 : 1);
        }

        Directory.CreateDirectory(root);
        Assert.Equal(FileEditRecoveryStatus.Ready,
            FileEditRecovery.TryAcquireHistoryLease(root, out var lease).Status);
        using (lease)
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "PhotoLibrarian.Tests.exe"))
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Environment["PHOTO_LIBRARIAN_RECOVERY_TEST_DIRECTORY"] = _directory;
            start.Environment["PHOTO_LIBRARIAN_RECOVERY_LEASE_PROBE"] = "1";
            start.ArgumentList.Add("-method");
            start.ArgumentList.Add("*FileEditRecoveryTests.HistoryLeaseExcludesAnotherProcess");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, $"{await output}\n{await errors}");
        }
        Assert.Equal(FileEditRecoveryStatus.Ready,
            FileEditRecovery.TryAcquireHistoryLease(root, out var reacquired).Status);
        reacquired!.Dispose();
    }

    [Fact]
    public async Task VersionPublicationCannotPassAnotherProcessHistoryLease()
    {
        var root = Path.Combine(_directory, "history");
        var path = Path.Combine(_directory, "source.cr3");
        if (Environment.GetEnvironmentVariable("PHOTO_LIBRARIAN_VERSION_LEASE_PROBE") == "1")
        {
            var history = new EditHistoryService();
            var store = new PhotoVersionRecipeStore(Path.Combine(root, "session"), history, () => Task.CompletedTask);
            var result = await store.CreateFamilyAsync(path, "Original", "Copy");
            Environment.Exit(result.Status == PhotoVersionRecipeStatus.Busy ? 0 : 1);
        }

        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        Assert.Equal(FileEditRecoveryStatus.Ready,
            FileEditRecovery.TryAcquireHistoryLease(root, out var lease).Status);
        using (lease)
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "PhotoLibrarian.Tests.exe"))
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Environment["PHOTO_LIBRARIAN_RECOVERY_TEST_DIRECTORY"] = _directory;
            start.Environment["PHOTO_LIBRARIAN_VERSION_LEASE_PROBE"] = "1";
            start.ArgumentList.Add("-method");
            start.ArgumentList.Add("*FileEditRecoveryTests.VersionPublicationCannotPassAnotherProcessHistoryLease");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, $"{await output}\n{await errors}");
        }
        Assert.False(File.Exists(Path.ChangeExtension(path, ".xmp")));
        Assert.False(File.Exists(path + ".xmp"));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task InspectionRecognizesInterruptedRenameAndRejectsExternalChanges()
    {
        var source = Path.Combine(_directory, "source.cr3");
        var sidecar = Path.ChangeExtension(source, ".xmp");
        var history = Path.Combine(_directory, "transaction");
        Directory.CreateDirectory(Path.Combine(history, "before"));
        Directory.CreateDirectory(Path.Combine(history, "checkpoints"));
        byte[] raw = [1, 2, 3];
        var before = Encoding.UTF8.GetBytes(Packet("before"));
        var after = Encoding.UTF8.GetBytes(Packet("after"));
        await File.WriteAllBytesAsync(source, raw);
        var beforePath = Path.Combine(history, "before", "1");
        var afterPath = Path.Combine(history, "checkpoints", "1");
        await File.WriteAllBytesAsync(beforePath, before);
        await File.WriteAllBytesAsync(afterPath, after);
        await File.WriteAllBytesAsync(sidecar, before);
        var recovery = Path.Combine(_directory, $".PhotoLibrarian-edit-{Guid.NewGuid():N}.tmp");
        var replacement = Path.Combine(_directory, $".PhotoLibrarian-edit-{Guid.NewGuid():N}.tmp");
        var manifest = new FileEditRecoveryManifest(1, source, [], false,
        [
            new(source, new(null, Hash(raw)), new(null, Hash(raw)), null, null),
            new(sidecar, new(beforePath, Hash(before)), new(afterPath, Hash(after)), recovery, replacement),
            new(source + ".xmp", new(null, null), new(null, null), null, null)
        ]);
        manifest.Write(history);

        Assert.Equal(FileEditRecoveryStatus.Ready, (await FileEditRecovery.InspectAsync(history, source)).Status);
        using (var locked = new FileStream(Path.Combine(history, "recovery.json"),
            FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(FileEditRecoveryStatus.Unavailable,
                (await FileEditRecovery.RestoreAsync(history, source, FileEditRecoveryChoice.Before)).Status);
        }
        File.Move(sidecar, recovery);
        Assert.Equal(FileEditRecoveryStatus.Ready, (await FileEditRecovery.InspectAsync(history, source)).Status);
        await File.WriteAllBytesAsync(sidecar, after);
        Assert.Equal(FileEditRecoveryStatus.Ready, (await FileEditRecovery.InspectAsync(history, source)).Status);
        await File.WriteAllBytesAsync(sidecar, [10]);
        Assert.Equal(FileEditRecoveryStatus.Conflict, (await FileEditRecovery.InspectAsync(history, source)).Status);
        Assert.Equal(FileEditRecoveryStatus.Conflict,
            (await FileEditRecovery.RestoreAsync(history, source, FileEditRecoveryChoice.Before)).Status);
        Assert.Equal(new byte[] { 10 }, await File.ReadAllBytesAsync(sidecar));
        Assert.Equal(raw, await File.ReadAllBytesAsync(source));

        await File.WriteAllBytesAsync(sidecar, after);
        await File.WriteAllBytesAsync(beforePath, [11]);
        Assert.Equal(FileEditRecoveryStatus.Conflict, (await FileEditRecovery.InspectAsync(history, source)).Status);
        await File.WriteAllBytesAsync(beforePath, before);
        Assert.Equal(FileEditRecoveryStatus.Applied,
            (await FileEditRecovery.RestoreAsync(history, source, FileEditRecoveryChoice.Before)).Status);
        Assert.Equal(before, await File.ReadAllBytesAsync(sidecar));
        Assert.Equal(FileEditRecoveryStatus.Resolved, (await FileEditRecovery.InspectAsync(history, source)).Status);
        await File.WriteAllTextAsync(Path.Combine(history, "recovery.completed"), "truncated");
        Assert.Equal(FileEditRecoveryStatus.Invalid, (await FileEditRecovery.InspectAsync(history, source)).Status);
        File.Delete(Path.Combine(history, "recovery.completed"));
        Directory.CreateDirectory(Path.Combine(history, "recovery.completed"));
        Assert.Equal(FileEditRecoveryStatus.Invalid, (await FileEditRecovery.InspectAsync(history, source)).Status);
    }

    [Fact]
    public async Task InspectionRejectsCheckpointPathsOutsideTheTransaction()
    {
        var source = Path.Combine(_directory, "source.cr3");
        var history = Path.Combine(_directory, "transaction");
        Directory.CreateDirectory(history);
        byte[] raw = [1];
        await File.WriteAllBytesAsync(source, raw);
        new FileEditRecoveryManifest(1, source, [], false,
        [
            new(source, new(null, Hash(raw)), new(null, Hash(raw)), null, null),
            new(Path.ChangeExtension(source, ".xmp"), new(null, null),
                new(source, Hash(raw)), null, null),
            new(source + ".xmp", new(null, null), new(null, null), null, null)
        ]).Write(history);

        Assert.Equal(FileEditRecoveryStatus.Invalid, (await FileEditRecovery.InspectAsync(history, source)).Status);
        Assert.Equal(raw, await File.ReadAllBytesAsync(source));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("checkpoint")]
    [InlineData("attempt")]
    [InlineData("manifest")]
    [InlineData("marker")]
    public async Task RecoveryRejectsReparsePointsWithoutChangingFiles(string linkedPath)
    {
        var source = Path.Combine(_directory, "source.cr3");
        var sidecar = Path.ChangeExtension(source, ".xmp");
        var history = Path.Combine(_directory, "transaction");
        Directory.CreateDirectory(Path.Combine(history, "before"));
        Directory.CreateDirectory(Path.Combine(history, "checkpoints"));
        byte[] raw = [1, 2, 3];
        var before = Encoding.UTF8.GetBytes(Packet("before"));
        var after = Encoding.UTF8.GetBytes(Packet("after"));
        var rawPath = linkedPath == "source" ? Path.Combine(_directory, "original.bin") : source;
        await File.WriteAllBytesAsync(rawPath, raw);
        if (linkedPath == "source") CreateSymbolicLink(source, rawPath, directory: false);
        await File.WriteAllBytesAsync(sidecar, after);
        var beforePath = Path.Combine(history, "before", "1");
        var afterPath = Path.Combine(history, "checkpoints", "1");
        var actualBefore = linkedPath == "checkpoint" ? Path.Combine(_directory, "before.bin") : beforePath;
        await File.WriteAllBytesAsync(actualBefore, before);
        if (linkedPath == "checkpoint") CreateSymbolicLink(beforePath, actualBefore, directory: false);
        await File.WriteAllBytesAsync(afterPath, after);
        if (linkedPath == "attempt")
        {
            var target = Path.Combine(_directory, "attempt-target");
            Directory.CreateDirectory(target);
            CreateSymbolicLink(Path.Combine(history, "recovery-attempts"), target, directory: true);
        }
        new FileEditRecoveryManifest(1, source, [], false,
        [
            new(source, new(null, Hash(raw)), new(null, Hash(raw)), null, null),
            new(sidecar, new(beforePath, Hash(before)), new(afterPath, Hash(after)), null, null),
            new(source + ".xmp", new(null, null), new(null, null), null, null)
        ]).Write(history);
        if (linkedPath == "manifest")
        {
            var manifest = Path.Combine(history, "recovery.json");
            var actualManifest = Path.Combine(_directory, "manifest.json");
            File.Move(manifest, actualManifest);
            CreateSymbolicLink(manifest, actualManifest, directory: false);
        }
        if (linkedPath == "marker")
        {
            var marker = Path.Combine(_directory, "marker");
            await File.WriteAllTextAsync(marker, "unrelated");
            CreateSymbolicLink(Path.Combine(history, "recovery.completed"), marker, directory: false);
        }

        Assert.Equal(FileEditRecoveryStatus.Invalid, (await FileEditRecovery.InspectAsync(history, source)).Status);
        Assert.Equal(FileEditRecoveryStatus.Invalid,
            (await FileEditRecovery.RestoreAsync(history, source, FileEditRecoveryChoice.Before)).Status);
        Assert.Equal(raw, await File.ReadAllBytesAsync(rawPath));
        Assert.Equal(after, await File.ReadAllBytesAsync(sidecar));
        Assert.Equal(before, await File.ReadAllBytesAsync(actualBefore));
    }

    [Fact]
    public async Task DurablePublicationRejectsSidecarReparsePointCreatedAfterPreparation()
    {
        var source = Path.Combine(_directory, "source.cr3");
        var sidecar = Path.ChangeExtension(source, ".xmp");
        var originalSidecar = Path.Combine(_directory, "original.xmp");
        byte[] raw = [1, 2, 3];
        await File.WriteAllBytesAsync(source, raw);
        await File.WriteAllTextAsync(sidecar, Packet("before"));
        using var transaction = await FileEditTransaction.PrepareAsync(
            Path.Combine(_directory, "history"), [source], copies =>
            {
                File.WriteAllText(Path.ChangeExtension(copies[source], ".xmp"), Packet("after"));
                return Task.CompletedTask;
            }, durableRecovery: true);
        File.Move(sidecar, originalSidecar);
        CreateSymbolicLink(sidecar, originalSidecar, directory: false);

        var result = await transaction.MoveAsync(false, () => Task.CompletedTask);
        Assert.Equal(EditHistoryStatus.Conflict, result.Status);
        Assert.Equal("before", ReadLabel(originalSidecar));
        Assert.Equal(raw, await File.ReadAllBytesAsync(source));
        Assert.True((File.GetAttributes(sidecar) & FileAttributes.ReparsePoint) != 0);
    }

    [Fact]
    public async Task FamilyCreationRejectsReparsePointBeforeStaging()
    {
        var source = Path.Combine(_directory, "source.cr3");
        var original = Path.Combine(_directory, "original.bin");
        byte[] raw = [1, 2, 3];
        await File.WriteAllBytesAsync(original, raw);
        CreateSymbolicLink(source, original, directory: false);
        var history = new EditHistoryService();
        var store = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history"), history, () => Task.CompletedTask);

        var result = await store.CreateFamilyAsync(source, "Original", "Copy");
        Assert.Equal(PhotoVersionRecipeStatus.Conflict, result.Status);
        Assert.NotNull(result.Message);
        Assert.False(history.CanUndo);
        Assert.Equal(raw, await File.ReadAllBytesAsync(original));
        Assert.False(File.Exists(Path.ChangeExtension(source, ".xmp")));
        Assert.False(File.Exists(source + ".xmp"));
    }

    [Fact]
    public async Task FamilySaveRejectsSourceReparsePointWithoutChangingItsRecipe()
    {
        var source = Path.Combine(_directory, "source.cr3");
        var original = Path.Combine(_directory, "original.bin");
        byte[] raw = [1, 2, 3];
        await File.WriteAllBytesAsync(source, raw);
        var history = new EditHistoryService();
        var store = new PhotoVersionRecipeStore(
            Path.Combine(_directory, "history"), history, () => Task.CompletedTask);
        var created = await store.CreateFamilyAsync(source, "Original", "Copy");
        Assert.Equal(PhotoVersionRecipeStatus.Success, created.Status);
        var sidecar = Path.ChangeExtension(source, ".xmp");
        var before = await File.ReadAllBytesAsync(sidecar);
        File.Move(source, original);
        CreateSymbolicLink(source, original, directory: false);

        var result = await store.WriteRecipeAsync(created.Snapshot!, created.Snapshot!.RecipeXmp);
        Assert.Equal(PhotoVersionRecipeStatus.Conflict, result.Status);
        Assert.Equal(before, await File.ReadAllBytesAsync(sidecar));
        Assert.Equal(raw, await File.ReadAllBytesAsync(original));
        Assert.Equal(EditHistoryStatus.Applied, history.Clear().Status);
    }

    [Fact]
    public async Task RecoveryDoesNotChangeSharedLegacySidecar()
    {
        var source = Path.Combine(_directory, "source.cr3");
        var companion = Path.ChangeExtension(source, ".jpg");
        var sidecar = Path.ChangeExtension(source, ".xmp");
        var history = Path.Combine(_directory, "transaction");
        Directory.CreateDirectory(Path.Combine(history, "before"));
        Directory.CreateDirectory(Path.Combine(history, "checkpoints"));
        byte[] raw = [1, 2, 3];
        var before = Encoding.UTF8.GetBytes(Packet("before"));
        var after = Encoding.UTF8.GetBytes(Packet("after"));
        await File.WriteAllBytesAsync(source, raw);
        await File.WriteAllBytesAsync(companion, [4, 5]);
        await File.WriteAllBytesAsync(sidecar, after);
        var beforePath = Path.Combine(history, "before", "1");
        var afterPath = Path.Combine(history, "checkpoints", "1");
        await File.WriteAllBytesAsync(beforePath, before);
        await File.WriteAllBytesAsync(afterPath, after);
        new FileEditRecoveryManifest(1, source, [companion], false,
        [
            new(source, new(null, Hash(raw)), new(null, Hash(raw)), null, null),
            new(sidecar, new(beforePath, Hash(before)), new(afterPath, Hash(after)), null, null),
            new(source + ".xmp", new(null, null), new(null, null), null, null)
        ]).Write(history);

        Assert.Equal(FileEditRecoveryStatus.Conflict,
            (await FileEditRecovery.RestoreAsync(history, source, FileEditRecoveryChoice.Before)).Status);
        Assert.Equal(after, await File.ReadAllBytesAsync(sidecar));
        Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(companion));
    }

    [Theory]
    [InlineData("""{"SchemaVersion":2,"future":true}""", FileEditRecoveryStatus.Unsupported)]
    [InlineData("""{"SchemaVersion":1,"SchemaVersion":1}""", FileEditRecoveryStatus.Invalid)]
    [InlineData("""{"SchemaVersion":"1"}""", FileEditRecoveryStatus.Invalid)]
    [InlineData("""{"SchemaVersion":1,"Changes":[]}""", FileEditRecoveryStatus.Invalid)]
    public async Task MalformedAndFutureManifestsNeverChangeTheSource(
        string json, FileEditRecoveryStatus expected)
    {
        var source = Path.Combine(_directory, "source.cr3");
        var history = Path.Combine(_directory, "transaction");
        Directory.CreateDirectory(history);
        byte[] raw = [1, 2, 3];
        await File.WriteAllBytesAsync(source, raw);
        await File.WriteAllTextAsync(Path.Combine(history, "recovery.json"), json);
        Assert.Equal(expected, (await FileEditRecovery.InspectAsync(history, source)).Status);
        Assert.Equal(expected,
            (await FileEditRecovery.RestoreAsync(history, source, FileEditRecoveryChoice.Before)).Status);
        Assert.Equal(raw, await File.ReadAllBytesAsync(source));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task PublicationFailureRollsBackAtEachRename(int failAt)
    {
        var source = Path.Combine(_directory, "source.cr3");
        var history = Path.Combine(_directory, "history");
        var legacy = Path.ChangeExtension(source, ".xmp");
        var qualified = source + ".xmp";
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        await File.WriteAllTextAsync(legacy, Packet("before-legacy"));
        await File.WriteAllTextAsync(qualified, Packet("before-qualified"));
        using var transaction = await FileEditTransaction.PrepareAsync(history, [source], copies =>
        {
            File.WriteAllText(Path.ChangeExtension(copies[source], ".xmp"), Packet("after-legacy"));
            File.WriteAllText(copies[source] + ".xmp", Packet("after-qualified"));
            return Task.CompletedTask;
        }, durableRecovery: true);
        var count = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transaction.MoveWithPublicationObserverAsync(false, () => Task.CompletedTask, () =>
            {
                if (++count == failAt) throw new InvalidOperationException("Injected publication failure.");
            }));

        Assert.Equal("before-legacy", ReadLabel(legacy));
        Assert.Equal("before-qualified", ReadLabel(qualified));
        Assert.Empty(Directory.GetFiles(history, "recovery.json", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(1, FileEditRecoveryChoice.Before)]
    [InlineData(2, FileEditRecoveryChoice.Before)]
    [InlineData(3, FileEditRecoveryChoice.Before)]
    [InlineData(4, FileEditRecoveryChoice.Before)]
    [InlineData(1, FileEditRecoveryChoice.After)]
    [InlineData(2, FileEditRecoveryChoice.After)]
    [InlineData(3, FileEditRecoveryChoice.After)]
    [InlineData(4, FileEditRecoveryChoice.After)]
    public async Task InterruptedPublicationLeavesAnInspectableManifest(int crashStep, FileEditRecoveryChoice choice)
    {
        var source = Path.Combine(_directory, "interrupted.cr3");
        var history = Path.Combine(_directory, "history", "session");
        var childMode = Environment.GetEnvironmentVariable("PHOTO_LIBRARIAN_RECOVERY_TEST_MODE");
        if (childMode == "recover")
        {
            var parent = Assert.Single(Directory.GetDirectories(history));
            await FileEditRecovery.RestoreWithPublicationObserverAsync(parent, source,
                FileEditRecoveryChoice.After, () => Environment.Exit(92));
            Assert.Fail("The child did not stop during recovery.");
            return;
        }
        if (childMode == "publish")
        {
            var stopAt = int.Parse(Environment.GetEnvironmentVariable("PHOTO_LIBRARIAN_RECOVERY_TEST_STEP")!);
            var renameCount = 0;
            var transaction = await FileEditTransaction.PrepareAsync(history, [source], copies =>
            {
                var staged = copies[source];
                File.WriteAllText(Path.ChangeExtension(staged, ".xmp"), Packet("after-legacy"));
                File.WriteAllText(staged + ".xmp", Packet("after-qualified"));
                return Task.CompletedTask;
            }, durableRecovery: true, recoveryRoot: Path.Combine(_directory, "history"));
            await transaction.MoveWithPublicationObserverAsync(false, () => Task.CompletedTask,
                () =>
                {
                    if (++renameCount == stopAt) Environment.Exit(91);
                });
            Assert.Fail("The child did not stop during publication.");
            return;
        }

        byte[] raw = [1, 2, 3];
        await File.WriteAllBytesAsync(source, raw);
        await File.WriteAllTextAsync(Path.ChangeExtension(source, ".xmp"), Packet("before-legacy"));
        await File.WriteAllTextAsync(source + ".xmp", Packet("before-qualified"));
        await RunChildAsync("publish", 91, crashStep);

        var transactionDirectory = Assert.Single(Directory.GetDirectories(history));
        Assert.Equal(FileEditRecoveryStatus.Ready,
            (await FileEditRecovery.InspectAsync(transactionDirectory, source)).Status);
        var found = Assert.Single(await FileEditRecovery.FindAsync(Path.Combine(_directory, "history")));
        Assert.Equal(source, found.SourcePath);
        Assert.Equal(FileEditRecoveryStatus.Ready, found.Inspection.Status);
        Assert.Equal(raw, await File.ReadAllBytesAsync(source));
        var companion = Path.ChangeExtension(source, ".jpg");
        await File.WriteAllBytesAsync(companion, [1]);
        Assert.Equal(FileEditRecoveryStatus.Conflict,
            (await FileEditRecovery.RestoreAsync(transactionDirectory, source, FileEditRecoveryChoice.After)).Status);
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(companion));
        File.Delete(companion);

        if (crashStep == 1 && choice == FileEditRecoveryChoice.After)
        {
            await RunChildAsync("recover", 92, crashStep);
            Assert.Equal(FileEditRecoveryStatus.Conflict,
                (await FileEditRecovery.InspectAsync(transactionDirectory, source)).Status);
            var attempt = Assert.Single(Directory.GetDirectories(Path.Combine(transactionDirectory, "recovery-attempts")));
            Assert.Equal(FileEditRecoveryStatus.Ready, (await FileEditRecovery.InspectAsync(attempt, source)).Status);
            Assert.Equal(2, (await FileEditRecovery.FindAsync(Path.Combine(_directory, "history"))).Count);
            Assert.Equal(FileEditRecoveryStatus.Applied,
                (await FileEditRecovery.RestoreAsync(Path.Combine(_directory, "history"),
                    attempt, source, FileEditRecoveryChoice.After)).Status);
        }

        Assert.Equal(FileEditRecoveryStatus.Applied,
            (await FileEditRecovery.RestoreAsync(Path.Combine(_directory, "history"),
                transactionDirectory, source, choice)).Status);
        var prefix = choice == FileEditRecoveryChoice.Before ? "before" : "after";
        Assert.Equal($"{prefix}-legacy", ReadLabel(Path.ChangeExtension(source, ".xmp")));
        Assert.Equal($"{prefix}-qualified", ReadLabel(source + ".xmp"));
        Assert.Equal(raw, await File.ReadAllBytesAsync(source));
        Assert.Equal(FileEditRecoveryStatus.Resolved,
            (await FileEditRecovery.InspectAsync(transactionDirectory, source)).Status);
        Assert.Empty(await FileEditRecovery.FindAsync(Path.Combine(_directory, "history")));
    }

    private async Task RunChildAsync(string mode, int expectedExitCode, int crashStep)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "PhotoLibrarian.Tests.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["PHOTO_LIBRARIAN_RECOVERY_TEST_DIRECTORY"] = _directory;
        start.Environment["PHOTO_LIBRARIAN_RECOVERY_TEST_MODE"] = mode;
        start.Environment["PHOTO_LIBRARIAN_RECOVERY_TEST_STEP"] = crashStep.ToString();
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add("*FileEditRecoveryTests.InterruptedPublicationLeavesAnInspectableManifest");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == expectedExitCode, $"{await output}\n{await errors}");
    }

    private static string Packet(string label)
    {
        var xmp = XmpMetaFactory.Create();
        XmpMetaFactory.SchemaRegistry.RegisterNamespace("https://photolibrarian.test/recovery/", "recover");
        xmp.SetProperty("https://photolibrarian.test/recovery/", "recover:label", label);
        return XmpMetaFactory.SerializeToString(xmp, new XmpCore.Options.SerializeOptions());
    }

    private static string ReadLabel(string path) =>
        XmpMetaFactory.ParseFromString(File.ReadAllText(path))
            .GetPropertyString("https://photolibrarian.test/recovery/", "recover:label");

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void CreateSymbolicLink(string path, string target, bool directory)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(path, target);
            else File.CreateSymbolicLink(path, target);
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Skip("Creating symbolic links requires Windows developer mode or the symlink privilege.");
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) == 1314)
        {
            Assert.Skip("Creating symbolic links requires Windows developer mode or the symlink privilege.");
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
