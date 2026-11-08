// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;

namespace PhotoLibrarian.Services;

/// <summary>Keeps effects as portable pending recipes; never bakes over viewer photo pixels.</summary>
internal sealed class ViewerEffectService(OriginalBackupService backups)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record EffectEdit(PhotoEffect Before, PhotoEffect After);
    private sealed class EffectHistory
    {
        internal Stack<EffectEdit> Undo { get; } = new();
        internal Stack<EffectEdit> Redo { get; } = new();
    }
    private readonly Dictionary<string, EffectHistory> _history = new(StringComparer.OrdinalIgnoreCase);

    internal bool CanUndo(string path) => _history.TryGetValue(path, out var history) && history.Undo.Count > 0;
    internal bool CanRedo(string path) => _history.TryGetValue(path, out var history) && history.Redo.Count > 0;

    internal async Task<ImageEditRenderer.RenderedPixels> SelectAsync(string path, PhotoEffect effect)
    {
        if (!ImageEditRenderer.IsSupported(path))
            throw new NotSupportedException("Photo effects require a supported raster photo.");
        if (!Enum.IsDefined(effect)) throw new ArgumentOutOfRangeException(nameof(effect));
        await _gate.WaitAsync();
        try
        {
            var sidecar = Path.ChangeExtension(path, ".xmp");
            if (File.Exists(sidecar))
            {
                using var stream = File.OpenRead(sidecar);
                _ = XmpCore.XmpMetaFactory.Parse(stream);
            }
            var parameters = ImageEditService.ReadEditParameters(path);
            var before = parameters.Effect;
            parameters.Effect = effect;
            var rendered = await RenderAsync(path, parameters);
            if (before == effect) return rendered;
            await new ImageEditService(backups).SaveEditParametersAsync(path, parameters);
            if (!_history.TryGetValue(path, out var history))
                _history[path] = history = new EffectHistory();
            var expected = history.Undo.TryPeek(out var previous) ? previous.After :
                history.Redo.TryPeek(out var next) ? next.Before : before;
            if (expected != before) history.Undo.Clear();
            history.Undo.Push(new EffectEdit(before, effect));
            history.Redo.Clear();
            return rendered;
        }
        finally { _gate.Release(); }
    }

    internal async Task<ImageEditRenderer.RenderedPixels> MoveHistoryAsync(string path, bool undo)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_history.TryGetValue(path, out var history))
                throw new InvalidOperationException("No effect history is available for this photo.");
            var source = undo ? history.Undo : history.Redo;
            var target = undo ? history.Redo : history.Undo;
            if (!source.TryPeek(out var edit))
                throw new InvalidOperationException("No effect history is available for this action.");
            var sidecar = Path.ChangeExtension(path, ".xmp");
            if (File.Exists(sidecar))
            {
                using var stream = File.OpenRead(sidecar);
                _ = XmpCore.XmpMetaFactory.Parse(stream);
            }
            var parameters = ImageEditService.ReadEditParameters(path);
            if (parameters.Effect != (undo ? edit.After : edit.Before))
                throw new InvalidOperationException("The photo effect changed outside this history. Select an effect to start a new edit.");
            parameters.Effect = undo ? edit.Before : edit.After;
            var rendered = await RenderAsync(path, parameters);
            await new ImageEditService(backups).SaveEditParametersAsync(path, parameters);
            source.Pop();
            target.Push(edit);
            return rendered;
        }
        finally { _gate.Release(); }
    }

    internal static Task<ImageEditRenderer.RenderedPixels> RenderAsync(string path, EditParameters parameters) =>
        Task.Run(async () =>
        {
            using var device = new CanvasDevice();
            return await ImageEditRenderer.RenderToPixelsAsync(device, path, parameters);
        });
}
