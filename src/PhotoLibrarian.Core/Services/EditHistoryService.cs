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
    private readonly object _sync = new();
    private TaskCompletionSource? _idle;
    private bool _isBusy;

    public event EventHandler? Changed;
    public bool IsBusy { get { lock (_sync) return _isBusy; } }
    public bool CanUndo { get { lock (_sync) return !_isBusy && _undo.Count > 0; } }
    public bool CanRedo { get { lock (_sync) return !_isBusy && _redo.Count > 0; } }
    public string UndoLabel { get { lock (_sync) return _undo.Count == 0 ? "Undo" : $"Undo {_undo[^1].Label}"; } }
    public string RedoLabel { get { lock (_sync) return _redo.Count == 0 ? "Redo" : $"Redo {_redo[^1].Label}"; } }
    public Task WaitForIdleAsync() { lock (_sync) return _idle?.Task ?? Task.CompletedTask; }

    public async Task<EditHistoryResult> ExecuteAsync(
        string label,
        Func<Task<EditHistoryResult>> apply,
        Func<bool, Task<EditHistoryResult>> move,
        Action? release = null)
    {
        lock (_sync)
        {
            if (_isBusy) return new(EditHistoryStatus.Busy, "An edit is already in progress.");
            _isBusy = true;
            _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            NotifyChanged();
            var result = await apply();
            if (result.Status != EditHistoryStatus.Applied) return result;
            List<Entry> discarded;
            lock (_sync)
            {
                discarded = TakeEntries(_redo);
                _undo.Add(new(label, move, release));
            }
            ReleaseEntries(discarded);
            return result;
        }
        finally
        {
            Finish();
        }
    }

    public Task<EditHistoryResult> UndoAsync() => MoveAsync(undo: true);
    public Task<EditHistoryResult> RedoAsync() => MoveAsync(undo: false);

    private async Task<EditHistoryResult> MoveAsync(bool undo)
    {
        List<Entry> source;
        List<Entry> target;
        Entry entry;
        lock (_sync)
        {
            if (_isBusy) return new(EditHistoryStatus.Busy, "An edit is already in progress.");
            source = undo ? _undo : _redo;
            target = undo ? _redo : _undo;
            if (source.Count == 0) return new(EditHistoryStatus.Empty);
            entry = source[^1];
            _isBusy = true;
            _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            NotifyChanged();
            var result = await entry.Move(undo);
            if (result.Status != EditHistoryStatus.Applied) return result;
            lock (_sync)
            {
                source.RemoveAt(source.Count - 1);
                target.Add(entry);
            }
            return result;
        }
        finally
        {
            Finish();
        }
    }

    /// <summary>Call only at an explicit library/session boundary, never on a failed edit.</summary>
    public EditHistoryResult Clear()
    {
        List<Entry> discarded;
        lock (_sync)
        {
            if (_isBusy) return new(EditHistoryStatus.Busy, "An edit is already in progress.");
            discarded = TakeEntries(_undo);
            discarded.AddRange(TakeEntries(_redo));
        }
        ReleaseEntries(discarded);
        NotifyChanged();
        return new(EditHistoryStatus.Applied);
    }

    private void Finish()
    {
        lock (_sync)
        {
            _isBusy = false;
            _idle!.TrySetResult();
        }
        NotifyChanged();
    }

    private static List<Entry> TakeEntries(List<Entry> entries)
    {
        var result = entries.ToList();
        entries.Clear();
        return result;
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
