// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;

namespace PhotoLibrarian.Services;

/// <summary>
/// One developed source per editor, serialized work, and latest-request-wins cancellation.
/// Called on the editor's UI thread; processing runs off-thread. Never caches display-size pixels.
/// </summary>
internal sealed class RawPreviewSession : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string, CancellationToken, Task<RawDevelopedImage>> _decode;
    private CancellationTokenSource? _request;
    private RawDevelopedImage? _source;
    private string? _sourcePath;
    private bool _disposed;

    public RawPreviewSession() : this(RawDevelopmentService.DecodeAsync) { }

    internal RawPreviewSession(Func<string, CancellationToken, Task<RawDevelopedImage>> decode) =>
        _decode = decode;

    public async Task<RawSdrImage> RenderAsync(string path, EditParameters parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _request?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _request = request;
        var token = request.Token;
        var snapshot = parameters.Clone();
        try
        {
            await _gate.WaitAsync(token);
            try
            {
                if (_source is null || !string.Equals(_sourcePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _source = null;
                    _sourcePath = null;
                    var source = await _decode(path, token);
                    token.ThrowIfCancellationRequested();
                    _source = source;
                    _sourcePath = path;
                }
                var result = await RawToneMappingService.RenderAsync(_source, snapshot, token);
                token.ThrowIfCancellationRequested();
                return result;
            }
            finally { _gate.Release(); }
        }
        finally
        {
            if (_request == request) _request = null;
            request.Dispose();
        }
    }

    public void Invalidate()
    {
        _request?.Cancel();
        _source = null;
        _sourcePath = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Invalidate();
        // A cancelled decoder may still hold the gate; it releases it when it exits.
    }
}
