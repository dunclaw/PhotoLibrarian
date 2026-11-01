// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using Windows.Graphics.Imaging;

namespace PhotoLibrarian.Core.Services;

/// <summary>Finds exact and perceptually similar still images in the indexed library.</summary>
public sealed class DuplicateDetectionService
{
    private const int PerceptualHashDimension = 32;
    private const int NearMatchHammingThreshold = 8;
    private const int MaximumNearMatchComparisons = 2_000_000;
    private const int MaximumCandidatesPerImage = 50_000;
    private const int MaximumNearMatchResults = 100_000;
    private const int MaximumDiagnostics = 50;
    private static readonly double[,] DctCosines = CreateDctCosines();
    private readonly ImageRepository _imageRepository;

    public DuplicateDetectionService(ImageRepository imageRepository)
    {
        _imageRepository = imageRepository;
    }

    public async Task<DuplicateScanResult> ScanAsync(
        IProgress<DuplicateScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? seedPaths = null)
    {
        if (seedPaths is { Count: 0 })
            return new DuplicateScanResult([], [], 0, false);

        var images = (await _imageRepository.GetAllAsync(cancellationToken: cancellationToken))
            .Where(image => image.MediaType == MediaType.Image)
            .ToList();
        var snapshots = new List<DuplicateFileSnapshot>(images.Count);
        var snapshotIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<DuplicateScanDiagnostic>();
        var diagnosticCount = 0;
        var decodeTimeouts = 0;
        var incompleteDecode = false;
        var completed = 0;
        var cachedHashes = await _imageRepository.GetDuplicateHashesAsync(cancellationToken);
        var cacheUpdates = new List<DuplicateFileSnapshot>(128);
        var lastProgressTime = Environment.TickCount64 - 100;
        DuplicateScanStage? lastStage = null;

        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var before = new FileInfo(image.FilePath);
                if (!before.Exists)
                    throw new FileNotFoundException("The file is missing.", image.FilePath);

                var index = snapshots.Count;
                var snapshot = new DuplicateFileSnapshot(
                    image,
                    before.Length,
                    before.LastWriteTimeUtc,
                    null,
                    null);
                if (cachedHashes.TryGetValue(image.FilePath, out var cached) &&
                    cached.FileSize == snapshot.FileSize &&
                    cached.ModifiedTicks == snapshot.LastWriteTimeUtc.Ticks)
                {
                    snapshot = snapshot with
                    {
                        Sha256 = cached.Sha256,
                        PerceptualHash = cached.PerceptualHash
                    };
                }
                snapshots.Add(snapshot);
                snapshotIndices.Add(image.FilePath, index);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddDiagnostic(image.FilePath, ex.Message);
            }
            completed++;
            if (completed % 128 == 0 || completed == images.Count)
                Report(new DuplicateScanProgress(
                    completed, images.Count, image.FilePath, DuplicateScanStage.Inspecting));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (seedPaths is not null && !snapshots.Any(file => seedPaths.Contains(file.Entry.FilePath)))
            return new DuplicateScanResult([], diagnostics, diagnosticCount, false);
        var exactSizeCandidates = snapshots
            .GroupBy(file => file.FileSize)
            .Where(group => group.Count() > 1 &&
                (seedPaths is null || group.Any(file => seedPaths.Contains(file.Entry.FilePath))))
            .SelectMany(group => group)
            .Select(file => file.Entry.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exactHashCandidates = snapshots
            .Where(file => exactSizeCandidates.Contains(file.Entry.FilePath))
            .Where(file => file.Sha256 is null)
            .ToArray();
        await HashCandidatesAsync(exactHashCandidates, DuplicateScanStage.ExactHash);

        completed = 0;
        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(new DuplicateScanProgress(
                completed, images.Count, image.FilePath, DuplicateScanStage.PerceptualHash));
            if (!snapshotIndices.TryGetValue(image.FilePath, out var index))
            {
                completed++;
                continue;
            }

            var snapshot = snapshots[index];
            try
            {
                var before = new FileInfo(image.FilePath);
                if (!before.Exists ||
                    before.Length != snapshot.FileSize ||
                    before.LastWriteTimeUtc != snapshot.LastWriteTimeUtc)
                {
                    snapshots[index] = snapshot with { Sha256 = null, PerceptualHash = null };
                    throw new IOException("The file changed while it was being scanned.");
                }

                if (snapshot.PerceptualHash.HasValue)
                {
                    completed++;
                    continue;
                }
                using var decodeCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                decodeCancellation.CancelAfter(TimeSpan.FromSeconds(15));
                var perceptualHash = await Task.Run(
                    () => ComputePerceptualHashAsync(image.FilePath, decodeCancellation.Token),
                    decodeCancellation.Token)
                    .WaitAsync(decodeCancellation.Token);
                var after = new FileInfo(image.FilePath);
                if (!after.Exists ||
                    after.Length != snapshot.FileSize ||
                    after.LastWriteTimeUtc != snapshot.LastWriteTimeUtc)
                {
                    snapshots[index] = snapshot with { Sha256 = null, PerceptualHash = null };
                    throw new IOException("The file changed while it was being scanned.");
                }

                snapshots[index] = snapshot with { PerceptualHash = perceptualHash };
                await CacheUpdatedSnapshotAsync(snapshots[index]);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                AddDiagnostic(image.FilePath, "Windows image decoding exceeded the 15-second limit.");
                if (++decodeTimeouts >= 2)
                {
                    incompleteDecode = true;
                    AddDiagnostic(image.FilePath,
                        "Similarity decoding stopped after two timeouts; remaining files were not decoded.");
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                snapshots[index] = snapshots[index] with { PerceptualHash = null };
                AddDiagnostic(image.FilePath, ex.Message);
            }

            completed++;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Report(new DuplicateScanProgress(
            0, 0, "", DuplicateScanStage.Comparing));
        var provisionalGroups = FindGroups(
            snapshots, out var nearMatchLimitReached, seedPaths, cancellationToken);
        var nearHashCandidates = provisionalGroups
            .Where(group => group.Kind == DuplicateMatchKind.Near)
            .SelectMany(group => group.Files)
            .Where(file => file.Sha256 is null)
            .Select(file => file.Entry.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => snapshots[snapshotIndices[path]])
            .ToArray();
        await HashCandidatesAsync(nearHashCandidates, DuplicateScanStage.Verifying);

        var groups = FindGroups(
            snapshots.Where(snapshot => snapshot.Sha256 is not null).ToArray(),
            out var finalNearMatchLimitReached, seedPaths, cancellationToken);
        await FlushCacheUpdatesAsync();
        nearMatchLimitReached |= finalNearMatchLimitReached || incompleteDecode;
        return new DuplicateScanResult(
            groups,
            diagnostics,
            diagnosticCount,
            nearMatchLimitReached);

        void AddDiagnostic(string path, string message)
        {
            diagnosticCount++;
            if (diagnostics.Count < MaximumDiagnostics)
                diagnostics.Add(new DuplicateScanDiagnostic(path, message));
        }

        async Task HashCandidatesAsync(
            IReadOnlyList<DuplicateFileSnapshot> candidates,
            DuplicateScanStage stage)
        {
            completed = 0;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report(new DuplicateScanProgress(
                    completed, candidates.Count, candidate.Entry.FilePath, stage));
                try
                {
                    var before = new FileInfo(candidate.Entry.FilePath);
                    if (!before.Exists ||
                        before.Length != candidate.FileSize ||
                        before.LastWriteTimeUtc != candidate.LastWriteTimeUtc)
                    {
                        throw new IOException("The file changed while it was being scanned.");
                    }

                    var hash = await ComputeSha256Async(
                        candidate.Entry.FilePath,
                        cancellationToken);
                    var after = new FileInfo(candidate.Entry.FilePath);
                    if (!after.Exists ||
                        after.Length != candidate.FileSize ||
                        after.LastWriteTimeUtc != candidate.LastWriteTimeUtc)
                    {
                        throw new IOException("The file changed while it was being scanned.");
                    }

                    var index = snapshotIndices[candidate.Entry.FilePath];
                    snapshots[index] = snapshots[index] with { Sha256 = hash };
                    await CacheUpdatedSnapshotAsync(snapshots[index]);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AddDiagnostic(candidate.Entry.FilePath, ex.Message);
                }

                completed++;
            }
            Report(new DuplicateScanProgress(completed, candidates.Count, "", stage));
        }

        async Task CacheUpdatedSnapshotAsync(DuplicateFileSnapshot snapshot)
        {
            cacheUpdates.Add(snapshot);
            if (cacheUpdates.Count >= 128)
                await FlushCacheUpdatesAsync();
        }

        async Task FlushCacheUpdatesAsync()
        {
            if (cacheUpdates.Count == 0)
                return;
            await _imageRepository.SaveDuplicateHashesAsync(cacheUpdates, cancellationToken);
            cacheUpdates.Clear();
        }

        void Report(DuplicateScanProgress update)
        {
            var now = Environment.TickCount64;
            if (lastStage != update.Stage ||
                update.CompletedFiles == update.TotalFiles ||
                now - lastProgressTime >= 100)
            {
                progress?.Report(update);
                lastProgressTime = now;
                lastStage = update.Stage;
            }
        }
    }

    internal static IReadOnlyList<DuplicateCandidateGroup> FindGroups(
        IReadOnlyList<DuplicateFileSnapshot> files,
        out bool nearMatchLimitReached,
        IReadOnlySet<string>? seedPaths = null,
        CancellationToken cancellationToken = default)
    {
        var groups = files
            .Where(file => file.Sha256 is not null)
            .GroupBy(file => file.Sha256!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1 &&
                (seedPaths is null || group.Any(file => seedPaths.Contains(file.Entry.FilePath))))
            .Select(group => new DuplicateCandidateGroup(
                DuplicateMatchKind.Exact,
                group.OrderBy(file => file.Entry.FilePath, StringComparer.OrdinalIgnoreCase).ToArray(),
                null))
            .ToList();

        var nearFiles = files
            .Where(file => file.PerceptualHash.HasValue)
            .GroupBy(
                file => file.Sha256 is null
                    ? $"path:{file.Entry.FilePath}"
                    : $"sha:{file.Sha256}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(file => seedPaths is null || seedPaths.Contains(file.Entry.FilePath))
                .ThenBy(file => file.Entry.FilePath, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(file => seedPaths is null || seedPaths.Contains(file.Entry.FilePath))
            .ToList();
        var buckets = new Dictionary<(int Segment, ulong Value), List<int>>();
        var pairDistances = new List<(int First, int Second, int Distance)>();
        var comparisons = 0;
        nearMatchLimitReached = false;

        for (var currentIndex = 0; currentIndex < nearFiles.Count; currentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentHash = nearFiles[currentIndex].PerceptualHash!.Value;
            var possibleMatches = new HashSet<int>();
            foreach (var (segment, value) in GetSegments(currentHash))
            {
                if (!buckets.TryGetValue((segment, value), out var candidates))
                    continue;

                foreach (var candidateIndex in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    possibleMatches.Add(candidateIndex);
                    if (possibleMatches.Count > MaximumCandidatesPerImage)
                    {
                        nearMatchLimitReached = true;
                        break;
                    }
                }
                if (nearMatchLimitReached)
                    break;
            }

            if (nearMatchLimitReached)
                break;

            foreach (var candidateIndex in possibleMatches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++comparisons > MaximumNearMatchComparisons)
                {
                    nearMatchLimitReached = true;
                    break;
                }

                var distance = BitOperations.PopCount(
                    currentHash ^ nearFiles[candidateIndex].PerceptualHash!.Value);
                if (distance <= NearMatchHammingThreshold)
                {
                    if (pairDistances.Count >= MaximumNearMatchResults)
                    {
                        nearMatchLimitReached = true;
                        break;
                    }
                    pairDistances.Add((candidateIndex, currentIndex, distance));
                }
            }

            if (nearMatchLimitReached)
                break;

            if (seedPaths is not null && !seedPaths.Contains(nearFiles[currentIndex].Entry.FilePath))
                continue;

            foreach (var (segment, value) in GetSegments(currentHash))
            {
                if (!buckets.TryGetValue((segment, value), out var candidates))
                {
                    candidates = [];
                    buckets.Add((segment, value), candidates);
                }
                candidates.Add(currentIndex);
            }
        }

        foreach (var candidate in pairDistances
            .OrderBy(pair => pair.Distance)
            .ThenBy(pair => nearFiles[pair.First].Entry.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            groups.Add(new DuplicateCandidateGroup(
                DuplicateMatchKind.Near,
                [
                    nearFiles[candidate.First],
                    nearFiles[candidate.Second]
                ],
                candidate.Distance));
        }

        return groups;
    }

    internal static ulong ComputePerceptualHash(byte[] bgraPixels, int width, int height)
    {
        if (width <= 0 || height <= 0 ||
            bgraPixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException("Expected a complete BGRA pixel buffer.", nameof(bgraPixels));
        }

        var grayscale = new double[PerceptualHashDimension * PerceptualHashDimension];
        for (var y = 0; y < PerceptualHashDimension; y++)
        {
            var sourceY = Math.Min(height - 1, y * height / PerceptualHashDimension);
            for (var x = 0; x < PerceptualHashDimension; x++)
            {
                var sourceX = Math.Min(width - 1, x * width / PerceptualHashDimension);
                var offset = (sourceY * width + sourceX) * 4;
                var blue = bgraPixels[offset];
                var green = bgraPixels[offset + 1];
                var red = bgraPixels[offset + 2];
                grayscale[y * PerceptualHashDimension + x] =
                    0.299 * red + 0.587 * green + 0.114 * blue;
            }
        }

        var rowDct = new double[PerceptualHashDimension * 8];
        for (var y = 0; y < PerceptualHashDimension; y++)
        {
            for (var frequency = 0; frequency < 8; frequency++)
            {
                var sum = 0d;
                for (var x = 0; x < PerceptualHashDimension; x++)
                {
                    sum += grayscale[y * PerceptualHashDimension + x] * DctCosines[frequency, x];
                }
                rowDct[y * 8 + frequency] = sum;
            }
        }

        var coefficients = new List<double>(63);
        for (var yFrequency = 0; yFrequency < 8; yFrequency++)
        {
            for (var xFrequency = 0; xFrequency < 8; xFrequency++)
            {
                if (xFrequency == 0 && yFrequency == 0)
                    continue;

                var sum = 0d;
                for (var y = 0; y < PerceptualHashDimension; y++)
                {
                    sum += rowDct[y * 8 + xFrequency] * DctCosines[yFrequency, y];
                }
                coefficients.Add(sum);
            }
        }

        var ordered = coefficients.Order().ToArray();
        var median = ordered[ordered.Length / 2];
        ulong hash = 0;
        for (var index = 0; index < coefficients.Count; index++)
        {
            if (coefficients[index] > median)
                hash |= 1UL << index;
        }
        return hash;
    }

    private static double[,] CreateDctCosines()
    {
        var cosines = new double[8, PerceptualHashDimension];
        for (var frequency = 0; frequency < 8; frequency++)
        {
            for (var coordinate = 0; coordinate < PerceptualHashDimension; coordinate++)
            {
                cosines[frequency, coordinate] = Math.Cos(
                    (2 * coordinate + 1) * frequency * Math.PI / (2 * PerceptualHashDimension));
            }
        }
        return cosines;
    }

    private static async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task<ulong> ComputePerceptualHashAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var fileStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var randomAccessStream = fileStream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream)
            .AsTask(cancellationToken);
        var transform = new BitmapTransform
        {
            ScaledWidth = PerceptualHashDimension,
            ScaledHeight = PerceptualHashDimension,
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
        return ComputePerceptualHash(
            pixelData.DetachPixelData(),
            PerceptualHashDimension,
            PerceptualHashDimension);
    }

    private static IEnumerable<(int Segment, ulong Value)> GetSegments(ulong hash)
    {
        var shift = 0;
        for (var segment = 0; segment < 9; segment++)
        {
            var bits = segment == 0 ? 8 : 7;
            var mask = (1UL << bits) - 1;
            yield return (segment, (hash >> shift) & mask);
            shift += bits;
        }
    }
}
