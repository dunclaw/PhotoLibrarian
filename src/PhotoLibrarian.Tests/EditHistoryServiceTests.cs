// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class EditHistoryServiceTests
{
    private static readonly EditHistoryResult Applied = new(EditHistoryStatus.Applied);

    [Fact]
    public async Task CrossFeatureHistoryIsChronologicalAndNewEditsInvalidateRedo()
    {
        var history = new EditHistoryService();
        var moves = new List<string>();
        await AddAsync("Rating");
        await AddAsync("Crop");
        await AddAsync("Tags on 3 photos");
        Assert.Equal("Undo Tags on 3 photos", history.UndoLabel);
        await history.UndoAsync();
        await history.UndoAsync();
        await history.RedoAsync();
        Assert.Equal(["undo Tags on 3 photos", "undo Crop", "redo Crop"], moves);
        await AddAsync("Caption");
        Assert.False(history.CanRedo);
        Assert.Equal("Undo Caption", history.UndoLabel);

        Task<EditHistoryResult> AddAsync(string label) => history.ExecuteAsync(
            label, () => Task.FromResult(Applied), undo =>
            {
                moves.Add($"{(undo ? "undo" : "redo")} {label}");
                return Task.FromResult(Applied);
            });
    }

    [Fact]
    public async Task FailureAndConflictPreserveHistoryForRetry()
    {
        var history = new EditHistoryService();
        var attempt = 0;
        await history.ExecuteAsync("Rating", () => Task.FromResult(Applied), _ =>
        {
            attempt++;
            if (attempt == 1) throw new IOException("Disk unavailable");
            return Task.FromResult(attempt == 2
                ? new EditHistoryResult(EditHistoryStatus.Conflict, "Changed externally")
                : Applied);
        });
        await Assert.ThrowsAsync<IOException>(() => history.UndoAsync());
        Assert.True(history.CanUndo);
        Assert.False(history.IsBusy);
        Assert.Equal(EditHistoryStatus.Conflict, (await history.UndoAsync()).Status);
        Assert.Equal("Undo Rating", history.UndoLabel);
        Assert.Equal(EditHistoryStatus.Applied, (await history.UndoAsync()).Status);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public async Task BusyOperationsCannotInterleaveOrClearHistory()
    {
        var history = new EditHistoryService();
        var gate = new TaskCompletionSource<EditHistoryResult>();
        var edit = history.ExecuteAsync("Batch", () => gate.Task, _ => Task.FromResult(Applied));
        Assert.True(history.IsBusy);
        Assert.Equal(EditHistoryStatus.Busy, (await history.UndoAsync()).Status);
        Assert.Equal(EditHistoryStatus.Busy, history.Clear().Status);
        Assert.Equal(EditHistoryStatus.Busy,
            (await history.ExecuteAsync("Other", () => Task.FromResult(Applied), _ => Task.FromResult(Applied))).Status);
        gate.SetResult(Applied);
        await edit;
        Assert.True(history.CanUndo);
    }

    [Fact]
    public async Task SimultaneousCommandsOnlyAdmitOneEdit()
    {
        const int count = 16;
        var history = new EditHistoryService();
        using var start = new ManualResetEventSlim();
        using var entered = new CountdownEvent(count);
        var gate = new TaskCompletionSource<EditHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyCount = 0;
        var commands = Enumerable.Range(0, count).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            var command = history.ExecuteAsync("Concurrent", () =>
            {
                Interlocked.Increment(ref applyCount);
                return gate.Task;
            }, _ => Task.FromResult(Applied));
            entered.Signal();
            return await command;
        })).ToArray();

        start.Set();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            gate.TrySetResult(Applied);
        }
        var results = await Task.WhenAll(commands);

        Assert.Equal(1, applyCount);
        Assert.Single(results, result => result.Status == EditHistoryStatus.Applied);
        Assert.Equal(count - 1, results.Count(result => result.Status == EditHistoryStatus.Busy));
        Assert.True(history.CanUndo);
    }

    [Fact]
    public async Task ReleaseCallbackCanInspectHistoryFromAnotherThread()
    {
        var history = new EditHistoryService();
        var released = false;
        await history.ExecuteAsync("Effect", () => Task.FromResult(Applied),
            _ => Task.FromResult(Applied), () =>
            {
                var inspection = Task.Run(() => history.CanUndo);
                Assert.True(inspection.Wait(TimeSpan.FromSeconds(5)));
                released = true;
            });

        Assert.Equal(EditHistoryStatus.Applied, history.Clear().Status);
        Assert.True(released);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public async Task FailedNewEditDoesNotInvalidateRedoOrCreateHistory()
    {
        var history = new EditHistoryService();
        var released = 0;
        await history.ExecuteAsync("Effect", () => Task.FromResult(Applied),
            _ => Task.FromResult(Applied), () => released++);
        await history.UndoAsync();
        await Assert.ThrowsAsync<IOException>(() => history.ExecuteAsync("Crop",
            () => throw new IOException(), _ => Task.FromResult(Applied)));
        Assert.True(history.CanRedo);
        Assert.Equal(0, released);
        history.Clear();
        Assert.Equal(1, released);
        Assert.False(history.CanRedo);
        Assert.Equal(EditHistoryStatus.Empty, (await history.UndoAsync()).Status);
    }
}
