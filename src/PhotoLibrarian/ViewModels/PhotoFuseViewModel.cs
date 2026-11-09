// Copyright (c) Duncan Lawler. All Rights Reserved.
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using System.Collections.ObjectModel;
using PhotoLibrarian.ML.Services;
using PhotoLibrarian.Services;

namespace PhotoLibrarian.ViewModels;

public sealed record PhotoFuseReplacement(ImageEntry Image, FaceRegion Face, string Hash);

/// <summary>
/// A viewer workspace. Discovery leases/decodes one pair at a time; only selected donor pixels
/// are retained for review. All final sources are re-registered and checked against discovery hashes.
/// </summary>
public partial class PhotoFuseViewModel : ObservableObject, IDisposable
{
    private readonly Func<ImageEntry, CancellationToken, Task<List<ImageEntry>>> _nearby;
    private readonly Func<ImageEntry, CancellationToken, Task<IReadOnlyList<FaceRegion>>> _scan;
    private readonly Func<ImageEntry, Task<List<FaceRegion>>> _faces;
    private readonly Dictionary<long, List<PhotoFuseReplacement>> _replacements = [];
    private readonly Dictionary<long, PhotoFuseReplacement> _selected = [];
    private CancellationTokenSource? _cancellation;
    private PhotoFuseSession? _session;
    private PhotoFusePreview? _preview;
    private ImageEntry? _basis;
    private string? _baseHash;
    private Task _operation = Task.CompletedTask;
    private byte[]? _basePixels;
    public ObservableCollection<FaceRegion> Faces { get; } = [];
    public byte[]? DisplayPixels { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool HasChanges => _selected.Count != 0;
    public event EventHandler? DisplayChanged;

    [ObservableProperty]
    public partial bool IsActive { get; set; }
    [ObservableProperty]
    public partial bool IsBusy { get; set; }
    [ObservableProperty]
    public partial string Status { get; set; } = "";

    public PhotoFuseViewModel(
        Func<ImageEntry, CancellationToken, Task<List<ImageEntry>>> nearby,
        Func<ImageEntry, CancellationToken, Task<IReadOnlyList<FaceRegion>>> scan,
        Func<ImageEntry, Task<List<FaceRegion>>> faces) =>
        (_nearby, _scan, _faces) = (nearby, scan, faces);

    public Task StartAsync(ImageEntry basis)
    {
        if (IsActive) throw new InvalidOperationException("Photo Fuse is already active.");
        _basis = basis;
        _cancellation = new CancellationTokenSource();
        IsActive = true;
        return _operation = DiscoverAsync(_cancellation.Token);
    }

    private async Task DiscoverAsync(CancellationToken token)
    {
        IsBusy = true;
        _replacements.Clear();
        try
        {
            Status = PhotoFuseStrings.Get("FuseFindingFaces");
            var basis = _basis ?? throw new InvalidOperationException("Open a photo first.");
            // Validate the base before scanning; no writes to invalid/pending-edit sources.
            using (var initial = await PhotoFuseSession.PrepareAsync([basis.FilePath], token))
            {
                if (_baseHash is not null && initial.SourceHashes[0] != _baseHash)
                    throw new IOException("The original photo changed. Exit Photo Fuse and refresh the library.");
                _baseHash = initial.SourceHashes[0];
                Width = initial.Width;
                Height = initial.Height;
                _basePixels = await initial.GetAlignedFrameAsync(0, token);
            }
            IReadOnlyList<FaceRegion> faces;
            string? scanWarning = null;
            try { faces = await _scan(basis, token); }
            catch (FaceModelsUnavailableException ex)
            {
                // Missing local models still allow explicit manual boxes / already stored faces.
                scanWarning = ex.Message;
                faces = await _faces(basis);
            }
            token.ThrowIfCancellationRequested();
            Faces.Clear();
            foreach (var face in faces.Where(PhotoFuseFaceMatcher.IsValid)) Faces.Add(face);
            DisplayPixels = HasChanges ? DisplayPixels : _basePixels;
            DisplayChanged?.Invoke(this, EventArgs.Empty);
            if (Faces.Count == 0)
            {
                Status = PhotoFuseStrings.Get("FuseNoFaces");
                if (scanWarning is not null) Status += " " + scanWarning;
                return;
            }
            var photos = await _nearby(basis, token);
            int processed = 0, matched = 0, rejected = 0;
            string? lastRejection = null;
            foreach (var image in photos)
            {
                token.ThrowIfCancellationRequested();
                Status = PhotoFuseStrings.Format("FuseSearching", ++processed, photos.Count);
                if (!PhotoFuseSession.IsSupported(image.FilePath)) { rejected++; continue; }
                try
                {
                    IReadOnlyList<FaceRegion> donorFaces;
                    try { donorFaces = await _scan(image, token); }
                    catch (FaceModelsUnavailableException ex)
                    {
                        scanWarning ??= ex.Message;
                        donorFaces = await _faces(image);
                    }
                    using var pair = await PhotoFuseSession.PrepareAsync([basis.FilePath, image.FilePath], token);
                    if (pair.SourceHashes[0] != _baseHash)
                        throw new IOException("The original photo changed during discovery.");
                    bool hasMatch = false;
                    foreach (var face in Faces)
                    {
                        var candidates = PhotoFuseFaceMatcher.Nearby(face, donorFaces, pair);
                        if (!_replacements.TryGetValue(face.Id, out var list))
                            _replacements[face.Id] = list = [];
                        foreach (var candidate in candidates)
                        {
                            list.Add(new PhotoFuseReplacement(image, candidate, pair.SourceHashes[1]));
                            hasMatch = true;
                        }
                    }
                    if (hasMatch) matched++;
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
                {
                    rejected++;
                    lastRejection = $"{image.FileName}: {ex.Message}";
                    Core.Diagnostics.DebugLog.WriteLine($"Photo Fuse rejected {lastRejection}");
                }
            }
            Status = PhotoFuseStrings.Format("FuseDiscoveryComplete", matched, rejected);
            if (lastRejection is not null) Status += " " + PhotoFuseStrings.Format("FuseLastSkip", lastRejection);
            if (scanWarning is not null) Status += $" {scanWarning}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Status = PhotoFuseStrings.Format("FusePreparingFailed", ex.Message);
            Core.Diagnostics.DebugLog.WriteLine($"Photo Fuse: {ex}");
        }
        finally { IsBusy = false; }
    }

    public IReadOnlyList<PhotoFuseReplacement> GetReplacements(FaceRegion face) =>
        _replacements.TryGetValue(face.Id, out var list) ? list : [];

    public Task SelectAsync(FaceRegion face, PhotoFuseReplacement? replacement)
    {
        if (!IsActive || IsBusy) throw new InvalidOperationException("Wait for Photo Fuse to finish preparing.");
        if (!Faces.Contains(face) || (replacement is not null && !GetReplacements(face).Contains(replacement)))
            throw new ArgumentException("Choose a replacement offered for this face.");
        return _operation = SelectCoreAsync(face, replacement, _cancellation!.Token);
    }

    private async Task SelectCoreAsync(FaceRegion face, PhotoFuseReplacement? replacement, CancellationToken token)
    {
        IsBusy = true;
        PhotoFuseSession? next = null;
        try
        {
            var choices = new Dictionary<long, PhotoFuseReplacement>(_selected);
            if (replacement is null) choices.Remove(face.Id);
            else choices[face.Id] = replacement;
            var paths = new List<string> { _basis!.FilePath };
            foreach (var choice in choices.Values)
                if (!paths.Contains(choice.Image.FilePath, StringComparer.OrdinalIgnoreCase))
                    paths.Add(choice.Image.FilePath);
            // Free previous leases before reacquiring; old accepted pixels stay visible on any failure.
            _session?.Dispose();
            _session = null;
            _preview = null;
            next = await PhotoFuseSession.PrepareAsync(paths, token);
            if (next.SourceHashes[0] != _baseHash)
                throw new IOException("The original photo changed. Exit Photo Fuse and refresh the library.");
            var regions = new List<PhotoFuseRegion>();
            foreach (var target in Faces.Where(target => choices.ContainsKey(target.Id)))
            {
                var choice = choices[target.Id];
                int frame = paths.FindIndex(path => string.Equals(path, choice.Image.FilePath,
                    StringComparison.OrdinalIgnoreCase));
                if (next.SourceHashes[frame] != choice.Hash)
                    throw new IOException("A replacement photo changed. Exit Photo Fuse and search again.");
                // Revalidate spatial mapping, not just source identity, before applying a face.
                if (!PhotoFuseFaceMatcher.Nearby(target, [choice.Face], next, frame).Contains(choice.Face))
                    throw new IOException("The replacement no longer aligns with this face.");
                regions.Add(PhotoFuseFaceMatcher.Region(target, frame, Width, Height));
            }
            if (regions.Count != choices.Count)
                throw new IOException("A selected face changed during rescanning. Discard replacements and start again.");
            var preview = regions.Count == 0 ? null : await next.PreviewAsync(regions, token);
            DisplayPixels = preview?.CopyPixels() ?? _basePixels;
            _selected.Clear();
            foreach (var choice in choices) _selected.Add(choice.Key, choice.Value);
            _session = next;
            _preview = preview;
            next = null;
            Status = HasChanges
                ? PhotoFuseStrings.Get("FuseReplacementPreviewed")
                : PhotoFuseStrings.Get("FuseOriginalRestored");
            DisplayChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Status = PhotoFuseStrings.Format("FuseReplacementFailed", ex.Message);
            Core.Diagnostics.DebugLog.WriteLine($"Photo Fuse replacement: {ex}");
        }
        finally { next?.Dispose(); IsBusy = false; }
    }

    public void ReleaseForManualFace()
    {
        if (IsBusy) throw new InvalidOperationException("Wait for the nearby-photo search to finish before adding a face.");
        _session?.Dispose();
        _session = null;
        _preview = null;
    }

    public Task RefreshFacesAsync()
    {
        if (!IsActive || IsBusy) return Task.CompletedTask;
        return _operation = DiscoverAsync(_cancellation!.Token);
    }

    public async Task<string?> FinishAsync()
    {
        if (IsBusy)
        {
            _cancellation?.Cancel();
            await _operation;
        }
        // Failed replacements / manual tagging may have released leases. Rebuild accepted choices
        // so save can never silently discard changes or publish a stale preview.
        if (HasChanges && _preview is null)
        {
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            var face = Faces.First(target => _selected.ContainsKey(target.Id));
            await SelectCoreAsync(face, _selected[face.Id], _cancellation.Token);
            if (_preview is null) throw new IOException(Status);
        }
        if (_preview is null) { Dispose(); return null; }
        IsBusy = true;
        try
        {
            var saved = await _session!.ExportAsync(_preview, Path.GetDirectoryName(_basis!.FilePath)!,
                confirmed: true);
            Dispose();
            return saved;
        }
        finally { IsBusy = false; }
    }

    public async Task DiscardAsync()
    {
        _cancellation?.Cancel();
        await _operation;
        Dispose();
    }

    public void Dispose()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _session?.Dispose();
        _session = null;
        _preview = null;
        _selected.Clear();
        _replacements.Clear();
        Faces.Clear();
        DisplayPixels = null;
        _basePixels = null;
        _baseHash = null;
        _basis = null;
        IsActive = false;
    }
}
