// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Services;

public enum EditHistoryStatus { Applied, Empty, Busy, Conflict }

public sealed record EditHistoryResult(EditHistoryStatus Status, string? Message = null);

/// <summary>
/// One chronological, session-only history. Commands validate their expected state before
/// changing it and return Conflict without writing when another editor has changed that state.
/// A failed command stays at the head of its stack, so it can be retried.
/// </summary>
public sealed class EditHistoryService
{
    private sealed record Entry(
        string Label,
        Func<bool, Task<EditHistoryResult>> Move,
        Action? Release);

    private readonly List<Entry> _undo = [];
    private readonly List<Entry> _redo = [];
    private TaskCompletionSource? _idle;

    public event EventHandler? Changed;
    public bool IsBusy { get; private set; }
    public bool CanUndo => !IsBusy && _undo.Count > 0;
    public bool CanRedo => !IsBusy && _redo.Count > 0;
    public string UndoLabel => _undo.Count == 0 ? "Undo" : $"Undo {_undo[^1].Label}";
    public string RedoLabel => _redo.Count == 0 ? "Redo" : $"Redo {_redo[^1].Label}";
    public Task WaitForIdleAsync() => _idle?.Task ?? Task.CompletedTask;

    public async Task<EditHistoryResult> ExecuteAsync(
        string label,
        Func<Task<EditHistoryResult>> apply,
        Func<bool, Task<EditHistoryResult>> move,
        Action? release = null)
    {
        if (IsBusy) return new(EditHistoryStatus.Busy, "An edit is already in progress.");
        IsBusy = true;
        _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NotifyChanged();
        try
        {
            var result = await apply();
            if (result.Status != EditHistoryStatus.Applied) return result;
            ReleaseEntries(_redo);
            _undo.Add(new(label, move, release));
            return result;
        }
        finally
        {
            IsBusy = false;
            _idle.TrySetResult();
            NotifyChanged();
        }
    }

    public Task<EditHistoryResult> UndoAsync() => MoveAsync(undo: true);
    public Task<EditHistoryResult> RedoAsync() => MoveAsync(undo: false);

    private async Task<EditHistoryResult> MoveAsync(bool undo)
    {
        if (IsBusy) return new(EditHistoryStatus.Busy, "An edit is already in progress.");
        var source = undo ? _undo : _redo;
        var target = undo ? _redo : _undo;
        if (source.Count == 0) return new(EditHistoryStatus.Empty);
        IsBusy = true;
        _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NotifyChanged();
        try
        {
            var entry = source[^1];
            var result = await entry.Move(undo);
            if (result.Status != EditHistoryStatus.Applied) return result;
            source.RemoveAt(source.Count - 1);
            target.Add(entry);
            return result;
        }
        finally
        {
            IsBusy = false;
            _idle.TrySetResult();
            NotifyChanged();
        }
    }

    /// <summary>Call only at an explicit library/session boundary, never on a failed edit.</summary>
    public EditHistoryResult Clear()
    {
        if (IsBusy) return new(EditHistoryStatus.Busy, "An edit is already in progress.");
        ReleaseEntries(_undo);
        ReleaseEntries(_redo);
        NotifyChanged();
        return new(EditHistoryStatus.Applied);
    }

    private static void ReleaseEntries(List<Entry> entries)
    {
        foreach (var entry in entries)
        {
            try { entry.Release?.Invoke(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Diagnostics.DebugLog.WriteLine($"[HISTORY] Could not remove history checkpoints: {exception.Message}");
            }
        }
        entries.Clear();
    }

    private void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
