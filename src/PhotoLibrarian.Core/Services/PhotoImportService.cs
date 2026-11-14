// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using System.Security.Cryptography;

namespace PhotoLibrarian.Core.Services;

public sealed class PhotoImportService
{
    private const int CopyBufferSize = 128 * 1024;
    private readonly MetadataReaderService _metadataReader;
    private readonly LibraryIndexingService _indexer;
    private readonly ImageRepository _imageRepository;

    public PhotoImportService(
        MetadataReaderService metadataReader,
        LibraryIndexingService indexer,
        ImageRepository imageRepository)
    {
        _metadataReader = metadataReader;
        _indexer = indexer;
        _imageRepository = imageRepository;
    }

    public Task<PhotoImportScanResult> ScanSourceAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Choose a camera or card folder.", nameof(sourcePath));

        var sourceRoot = Path.GetFullPath(sourcePath);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"The source folder is not available: {sourceRoot}");

        var dcimPath = Path.GetFileName(
                Path.TrimEndingDirectorySeparator(sourceRoot))
                .Equals("DCIM", StringComparison.OrdinalIgnoreCase)
            ? sourceRoot
            : Path.Combine(sourceRoot, "DCIM");
        if (!Directory.Exists(dcimPath))
            throw new DirectoryNotFoundException(
                $"No DCIM folder was found in '{sourceRoot}'. Choose the mounted device root or its DCIM folder.");

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var mediaPaths = new List<string>();
        foreach (var path in Directory.EnumerateFiles(dcimPath, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FolderScannerService.IsSupportedFile(path))
                mediaPaths.Add(path);
        }
        mediaPaths.Sort(StringComparer.OrdinalIgnoreCase);
        var sameStemCounts = mediaPaths
            .GroupBy(GetStemKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var candidates = new List<PhotoImportCandidate>(mediaPaths.Count);
        var warnings = new List<string>();

        foreach (var mediaPath in mediaPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tagSidecarPath = Path.ChangeExtension(mediaPath, ".xmp");
            if (File.Exists(tagSidecarPath) &&
                sameStemCounts[GetStemKey(mediaPath)] > 1)
            {
                warnings.Add(
                    $"{Path.GetFileName(mediaPath)} was skipped because its XMP sidecar matches more than one media file.");
                continue;
            }

            var fileInfo = new FileInfo(mediaPath);
            if (!fileInfo.Exists)
            {
                warnings.Add($"{Path.GetFileName(mediaPath)} is no longer available.");
                continue;
            }

            var sidecars = GetSourceSidecars(mediaPath);
            var metadata = _metadataReader.ReadMetadata(mediaPath);
            var dateTaken = metadata.DateTaken ?? fileInfo.LastWriteTime;
            candidates.Add(new PhotoImportCandidate(
                mediaPath,
                sourceRoot,
                fileInfo.Name,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc,
                dateTaken,
                metadata.DateTaken is null,
                sidecars));
        }

        return Task.FromResult(new PhotoImportScanResult(candidates, warnings));
    }

    public async Task<PhotoImportResult> ImportAsync(
        IEnumerable<PhotoImportCandidate> candidates,
        string destinationPath,
        string? namingTemplate,
        IEnumerable<string> importTags,
        IProgress<PhotoImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var selected = candidates
            .DistinctBy(candidate => candidate.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selected.Count == 0)
            return new PhotoImportResult([], false);

        var destinationRoot = Path.GetFullPath(destinationPath);
        if (!Directory.Exists(destinationRoot))
            throw new DirectoryNotFoundException(
                $"Choose an existing destination folder: {destinationRoot}");

        var template = string.IsNullOrWhiteSpace(namingTemplate)
            ? "{name}"
            : namingTemplate.Trim();
        ValidateTemplate(template);
        var tags = importTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var totalBytes = selected.Sum(candidate => Math.Max(0, candidate.FileSize));
        foreach (var candidate in selected)
            EnsureDestinationIsOutsideSourceTree(candidate.SourceRoot, destinationRoot);

        var completed = 0;
        var copiedBytes = 0L;
        var results = new List<PhotoImportItemResult>(selected.Count);
        var canceled = false;

        foreach (var candidate in selected)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }
            progress?.Report(new PhotoImportProgress(
                completed, selected.Count, candidate.FileName, PhotoImportStage.Copying,
                copiedBytes, totalBytes));

            try
            {
                var result = await ImportOneAsync(
                    candidate,
                    destinationRoot,
                    template,
                    tags,
                    completed + 1,
                    completed,
                    selected.Count,
                    progress,
                    copiedBytes,
                    totalBytes,
                    cancellationToken);
                copiedBytes += candidate.FileSize;
                results.Add(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }
            catch (Exception exception) when (IsImportFailure(exception))
            {
                results.Add(new PhotoImportItemResult(
                    candidate.SourcePath,
                    null,
                    false,
                    null,
                    $"{candidate.FileName}: {exception.Message}"));
            }

            completed++;
            progress?.Report(new PhotoImportProgress(
                completed, selected.Count, candidate.FileName, PhotoImportStage.Completed,
                copiedBytes, totalBytes));
        }

        return new PhotoImportResult(results, canceled || cancellationToken.IsCancellationRequested);
    }

    public async Task<IReadOnlyList<PhotoSourceDeletionResult>> DeleteVerifiedSourcesAsync(
        IEnumerable<PhotoImportItemResult> importedItems,
        IProgress<PhotoImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(importedItems);
        var items = importedItems.Where(item => item.Succeeded && item.Identity is not null).ToList();
        var results = new List<PhotoSourceDeletionResult>(items.Count);
        var completed = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = item.Identity!;
            progress?.Report(new PhotoImportProgress(
                completed, items.Count, Path.GetFileName(identity.SourcePath),
                PhotoImportStage.VerifyingDeletion, completed, items.Count));
            try
            {
                await VerifyIdentityAsync(
                    identity.SourcePath,
                    identity.SourceSize,
                    identity.SourceLastWriteTimeUtc,
                    identity.SourceHash,
                    cancellationToken);
                await VerifyIdentityAsync(
                    identity.DestinationPath,
                    identity.DestinationSize,
                    identity.DestinationLastWriteTimeUtc,
                    identity.DestinationHash,
                    cancellationToken);

                foreach (var sidecar in identity.SourceSidecars)
                    await VerifyIdentityAsync(
                        sidecar.Path,
                        sidecar.Size,
                        sidecar.LastWriteTimeUtc,
                        sidecar.Hash,
                        cancellationToken);
                foreach (var sidecar in identity.DestinationSidecars)
                    await VerifyIdentityAsync(
                        sidecar.Path,
                        sidecar.Size,
                        sidecar.LastWriteTimeUtc,
                        sidecar.Hash,
                        cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(identity.SourcePath);
                var sidecarErrors = new List<string>();
                foreach (var sidecar in identity.SourceSidecars)
                {
                    try
                    {
                        File.Delete(sidecar.Path);
                    }
                    catch (Exception exception) when (
                        exception is IOException or
                            UnauthorizedAccessException or
                            System.Security.SecurityException)
                    {
                        sidecarErrors.Add($"{Path.GetFileName(sidecar.Path)}: {exception.Message}");
                    }
                }
                results.Add(new PhotoSourceDeletionResult(
                    identity.SourcePath,
                    true,
                    sidecarErrors.Count == 0
                        ? null
                        : $"The photo was deleted, but some source XMP sidecars remain: {string.Join("; ", sidecarErrors)}"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results.Add(new PhotoSourceDeletionResult(
                    identity.SourcePath, false, "Deletion canceled; the source was preserved."));
                break;
            }
            catch (Exception exception) when (IsImportFailure(exception))
            {
                results.Add(new PhotoSourceDeletionResult(
                    identity.SourcePath,
                    false,
                    $"Source was preserved because verification or deletion failed: {exception.Message}"));
            }

            completed++;
        }

        return results;
    }

    public static string GetDateFolder(PhotoImportCandidate candidate) =>
        candidate.DateTaken.ToString("yyyy\\yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static string GetOutputFileName(
        PhotoImportCandidate candidate,
        string? namingTemplate,
        int sequence)
    {
        var template = string.IsNullOrWhiteSpace(namingTemplate) ? "{name}" : namingTemplate.Trim();
        ValidateTemplate(template);
        var date = candidate.DateTaken.ToString(
            "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var expanded = template
            .Replace("{name}", Path.GetFileNameWithoutExtension(candidate.FileName), StringComparison.Ordinal)
            .Replace("{date}", date, StringComparison.Ordinal)
            .Replace("{year}", candidate.DateTaken.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{month}", candidate.DateTaken.ToString("MM", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{day}", candidate.DateTaken.ToString("dd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{counter}", sequence.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var safeBaseName = SanitizeFileName(expanded);
        var extension = Path.GetExtension(candidate.FileName);
        const int maxFileNameLength = 180;
        if (safeBaseName.Length + extension.Length > maxFileNameLength)
            safeBaseName = safeBaseName[..(maxFileNameLength - extension.Length)];
        return safeBaseName + extension;
    }

    private async Task<PhotoImportItemResult> ImportOneAsync(
        PhotoImportCandidate candidate,
        string destinationRoot,
        string namingTemplate,
        IReadOnlyList<string> importTags,
        int sequence,
        int filesCompleted,
        int filesTotal,
        IProgress<PhotoImportProgress>? progress,
        long bytesBefore,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        ValidateCandidate(candidate);
        var destinationDirectory = Path.Combine(destinationRoot, GetDateFolder(candidate));
        Directory.CreateDirectory(destinationDirectory);

        string? destinationPath = null;
        string? temporaryPath = null;
        var temporarySidecarPaths = new List<string>();
        var stagedSidecars = new List<StagedSidecar>();
        string? sourceHash = null;
        var mediaPublished = false;
        var publishedSidecars = new List<StagedSidecar>();
        string? finalMediaHash = null;
        try
        {
            destinationPath = await ChooseDestinationAsync(
                candidate,
                destinationDirectory,
                namingTemplate,
                sequence,
                cancellationToken);
            temporaryPath = Path.Combine(
                destinationDirectory,
                $".{Guid.NewGuid():N}.{Path.GetFileName(destinationPath)}.importing");

            sourceHash = await CopyAndVerifyAsync(
                candidate.SourcePath,
                temporaryPath,
                candidate.FileSize,
                candidate.SourceLastWriteTimeUtc,
                cancellationToken,
                bytesCopied => progress?.Report(new PhotoImportProgress(
                    filesCompleted,
                    filesTotal,
                    candidate.FileName,
                    PhotoImportStage.Copying,
                    bytesBefore + bytesCopied,
                    totalBytes)));
            File.SetLastWriteTimeUtc(temporaryPath, candidate.SourceLastWriteTimeUtc);
            var sourceContentIdentity = await RecognitionSourceIdentity.ComputeAsync(
                temporaryPath,
                cancellationToken,
                destinationPath);

            foreach (var sourceSidecar in candidate.SourceSidecars)
            {
                var temporarySidecarPath = GetMappedSidecarPath(
                    candidate.SourcePath,
                    temporaryPath,
                    sourceSidecar.Path);
                var destinationSidecarPath = GetMappedSidecarPath(
                    candidate.SourcePath,
                    destinationPath,
                    sourceSidecar.Path);
                temporarySidecarPaths.Add(temporarySidecarPath);
                var sourceSidecarHash = await CopyAndVerifyAsync(
                    sourceSidecar.Path,
                    temporarySidecarPath,
                    sourceSidecar.Size,
                    sourceSidecar.LastWriteTimeUtc,
                    cancellationToken);
                File.SetLastWriteTimeUtc(
                    temporarySidecarPath,
                    sourceSidecar.LastWriteTimeUtc);
                stagedSidecars.Add(new StagedSidecar(
                    temporarySidecarPath,
                    destinationSidecarPath,
                    new PhotoImportFileIdentity(
                        sourceSidecar.Path,
                        sourceSidecar.Size,
                        sourceSidecar.LastWriteTimeUtc,
                        sourceSidecarHash)));
            }

            if (importTags.Count > 0)
            {
                var existingTags = _metadataReader.ReadTags(temporaryPath);
                var mergedTags = existingTags
                    .Concat(importTags)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var expectedSidecar = TagWriterService.CaptureSidecarSnapshot(temporaryPath);
                await TagWriterService.WriteTagsToSidecarAsync(
                    temporaryPath,
                    mergedTags,
                    expectedSidecarSnapshot: expectedSidecar,
                    formatPath: destinationPath);
            }

            var stagedContentIdentity = await RecognitionSourceIdentity.ComputeAsync(
                temporaryPath,
                cancellationToken,
                destinationPath);
            if (!string.Equals(
                    sourceContentIdentity,
                    stagedContentIdentity,
                    StringComparison.Ordinal))
                throw new IOException("The imported photo pixels changed while metadata was being written.");

            var tagTemporarySidecarPath = Path.ChangeExtension(temporaryPath, ".xmp");
            if (File.Exists(tagTemporarySidecarPath) &&
                stagedSidecars.All(sidecar =>
                    !string.Equals(
                        sidecar.TemporaryPath,
                        tagTemporarySidecarPath,
                        StringComparison.OrdinalIgnoreCase)))
            {
                temporarySidecarPaths.Add(tagTemporarySidecarPath);
                stagedSidecars.Add(new StagedSidecar(
                    tagTemporarySidecarPath,
                    Path.ChangeExtension(destinationPath, ".xmp"),
                    null));
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (var sidecar in stagedSidecars)
            {
                if (!File.Exists(sidecar.TemporaryPath))
                    continue;

                sidecar.FinalHash = await HashFileAsync(
                    sidecar.TemporaryPath,
                    CancellationToken.None);
                File.Move(sidecar.TemporaryPath, sidecar.DestinationPath);
                publishedSidecars.Add(sidecar);
            }

            File.Move(temporaryPath, destinationPath);
            mediaPublished = true;

            var destinationInfo = new FileInfo(destinationPath);
            finalMediaHash = await HashFileAsync(destinationPath, CancellationToken.None);

            var destinationSidecars = new List<PhotoImportFileIdentity>();
            foreach (var sidecar in publishedSidecars)
            {
                var sidecarInfo = new FileInfo(sidecar.DestinationPath);
                var publishedHash = await HashFileAsync(
                    sidecar.DestinationPath,
                    CancellationToken.None);
                if (!string.Equals(
                        sidecar.FinalHash,
                        publishedHash,
                        StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A destination XMP sidecar changed before indexing.");
                destinationSidecars.Add(new PhotoImportFileIdentity(
                    sidecar.DestinationPath,
                    sidecarInfo.Length,
                    sidecarInfo.LastWriteTimeUtc,
                    publishedHash));
            }

            try
            {
                await _indexer.IndexFileAsync(destinationPath, CancellationToken.None);
                if (await _imageRepository.GetByPathAsync(destinationPath) is null)
                    throw new IOException("The copied photo could not be confirmed in the library index.");
            }
            catch
            {
                await _imageRepository.DeleteByPathAsync(destinationPath);
                throw;
            }

            var identity = new PhotoImportIdentity(
                candidate.SourcePath,
                candidate.FileSize,
                candidate.SourceLastWriteTimeUtc,
                sourceHash,
                stagedSidecars
                    .Where(sidecar => sidecar.SourceIdentity is not null)
                    .Select(sidecar => sidecar.SourceIdentity!)
                    .ToList(),
                destinationPath,
                destinationInfo.Length,
                destinationInfo.LastWriteTimeUtc,
                finalMediaHash,
                destinationSidecars);
            return new PhotoImportItemResult(
                candidate.SourcePath, destinationPath, true, identity, null);
        }
        catch
        {
            foreach (var sidecar in publishedSidecars)
            {
                await DeleteIfHashMatchesAsync(
                    sidecar.DestinationPath,
                    sidecar.FinalHash,
                    CancellationToken.None);
            }

            if (mediaPublished && destinationPath is not null)
            {
                await DeleteIfHashMatchesAsync(
                    destinationPath,
                    finalMediaHash ?? sourceHash,
                    CancellationToken.None);
                await _imageRepository.DeleteByPathAsync(destinationPath);
            }
            throw;
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
            foreach (var sidecarPath in temporarySidecarPaths)
                DeleteTemporaryFile(sidecarPath);
            RemoveDirectoryIfEmpty(destinationDirectory);
        }
    }

    private async Task<string> ChooseDestinationAsync(
        PhotoImportCandidate candidate,
        string destinationDirectory,
        string namingTemplate,
        int sequence,
        CancellationToken cancellationToken)
    {
        for (var collision = 0; collision < 10000; collision++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseName = Path.GetFileNameWithoutExtension(
                GetOutputFileName(candidate, namingTemplate, sequence));
            if (collision > 0)
                baseName = $"{baseName}_{collision + 1}";
            var outputName = baseName + Path.GetExtension(candidate.FileName);
            var mediaPath = Path.Combine(destinationDirectory, outputName);
            if (File.Exists(mediaPath) ||
                Directory.Exists(mediaPath) ||
                GetDestinationSidecarPaths(mediaPath).Any(File.Exists) ||
                GetDestinationSidecarPaths(mediaPath).Any(Directory.Exists) ||
                await _imageRepository.GetByPathAsync(mediaPath) is not null)
            {
                continue;
            }

            return mediaPath;
        }

        throw new IOException($"Could not find an unused destination name for {candidate.FileName}.");
    }

    private static async Task<string> CopyAndVerifyAsync(
        string sourcePath,
        string destinationPath,
        long expectedLength,
        DateTime expectedLastWriteTimeUtc,
        CancellationToken cancellationToken,
        Action<long>? progress = null)
    {
        VerifyFileInfo(sourcePath, expectedLength, expectedLastWriteTimeUtc);
        using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != expectedLength)
            throw new IOException("The source changed after it was scanned.");

        await using (var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[CopyBufferSize];
            long copied = 0;
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                    break;

                sourceHash.AppendData(buffer, 0, bytesRead);
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                copied += bytesRead;
                progress?.Invoke(copied);
            }

            await destination.FlushAsync(cancellationToken);
            destination.Flush(flushToDisk: true);
        }

        VerifyFileInfo(sourcePath, expectedLength, expectedLastWriteTimeUtc);
        var expectedHash = Convert.ToHexString(sourceHash.GetHashAndReset());
        var actualHash = await HashFileAsync(destinationPath, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHash),
                Convert.FromHexString(actualHash)))
            throw new IOException("The destination copy did not match the source bytes.");

        return expectedHash;
    }

    private static async Task VerifyIdentityAsync(
        string path,
        long expectedLength,
        DateTime expectedLastWriteTimeUtc,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        VerifyFileInfo(path, expectedLength, expectedLastWriteTimeUtc);
        var actualHash = await HashFileAsync(path, cancellationToken);
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"The file contents changed: {path}");
    }

    private static void VerifyFileInfo(
        string path,
        long expectedLength,
        DateTime expectedLastWriteTimeUtc)
    {
        var info = new FileInfo(path);
        if (!info.Exists ||
            info.Length != expectedLength ||
            info.LastWriteTimeUtc != expectedLastWriteTimeUtc)
            throw new IOException($"The file changed or is no longer available: {path}");
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task DeleteIfHashMatchesAsync(
        string path,
        string? expectedHash,
        CancellationToken cancellationToken)
    {
        if (expectedHash is null || !File.Exists(path))
            return;

        if (string.Equals(
                expectedHash,
                await HashFileAsync(path, cancellationToken),
                StringComparison.OrdinalIgnoreCase))
            File.Delete(path);
    }

    private static void DeleteTemporaryFile(string? path)
    {
        if (path is not null && File.Exists(path))
            File.Delete(path);
    }

    private static void RemoveDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            Directory.Delete(path);
    }

    private static void ValidateCandidate(PhotoImportCandidate candidate)
    {
        var sourcePath = Path.GetFullPath(candidate.SourcePath);
        var sourceRoot = Path.GetFullPath(candidate.SourceRoot);
        var relativeSourcePath = Path.GetRelativePath(sourceRoot, sourcePath);
        if (Path.IsPathRooted(relativeSourcePath) ||
            relativeSourcePath.Equals("..", StringComparison.Ordinal) ||
            relativeSourcePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            !FolderScannerService.IsSupportedFile(sourcePath))
            throw new IOException("The selected source file is outside the scanned DCIM folder or is unsupported.");

        VerifyFileInfo(
            sourcePath,
            candidate.FileSize,
            candidate.SourceLastWriteTimeUtc);
        foreach (var sidecar in candidate.SourceSidecars)
        {
            var expectedSidecarPaths = GetSourceSidecarPaths(sourcePath);
            var fullSidecarPath = Path.GetFullPath(sidecar.Path);
            if (!expectedSidecarPaths.Contains(
                    fullSidecarPath,
                    StringComparer.OrdinalIgnoreCase))
                throw new IOException("The selected XMP sidecar does not match its photo.");

            VerifyFileInfo(
                fullSidecarPath,
                sidecar.Size,
                sidecar.LastWriteTimeUtc);
        }
    }

    private static void EnsureDestinationIsOutsideSourceTree(
        string sourceRoot,
        string destinationRoot)
    {
        var fullSourceRoot = Path.GetFullPath(sourceRoot);
        var dcimRoot = Path.GetFileName(
                Path.TrimEndingDirectorySeparator(fullSourceRoot))
                .Equals("DCIM", StringComparison.OrdinalIgnoreCase)
            ? fullSourceRoot
            : Path.Combine(fullSourceRoot, "DCIM");
        var relativeDestination = Path.GetRelativePath(
            dcimRoot,
            Path.GetFullPath(destinationRoot));
        if (!Path.IsPathRooted(relativeDestination) &&
            (relativeDestination.Equals(".", StringComparison.Ordinal) ||
             (!relativeDestination.Equals("..", StringComparison.Ordinal) &&
              !relativeDestination.StartsWith(
                  $"..{Path.DirectorySeparatorChar}",
                  StringComparison.Ordinal))))
        {
            throw new IOException("Choose a destination outside the source DCIM folder.");
        }
    }

    private static string GetStemKey(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));

    private static IReadOnlyList<PhotoImportSidecar> GetSourceSidecars(string mediaPath) =>
        GetSourceSidecarPaths(mediaPath)
            .Where(File.Exists)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new PhotoImportSidecar(
                    info.FullName,
                    info.Length,
                    info.LastWriteTimeUtc);
            })
            .ToList();

    private static IReadOnlyList<string> GetSourceSidecarPaths(string mediaPath) =>
        new[]
        {
            Path.ChangeExtension(mediaPath, ".xmp"),
            FaceMetadataStore.GetSidecarPathForImage(mediaPath)
        }
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static IReadOnlyList<string> GetDestinationSidecarPaths(string mediaPath) =>
        new[]
        {
            Path.ChangeExtension(mediaPath, ".xmp"),
            FaceMetadataStore.GetSidecarPathForDestinationImage(mediaPath)
        }
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string GetMappedSidecarPath(
        string sourceMediaPath,
        string destinationMediaPath,
        string sourceSidecarPath)
    {
        var appendedSidecarPath = $"{sourceMediaPath}.xmp";
        return string.Equals(
                sourceSidecarPath,
                appendedSidecarPath,
                StringComparison.OrdinalIgnoreCase)
            ? $"{destinationMediaPath}.xmp"
            : Path.ChangeExtension(destinationMediaPath, ".xmp");
    }

    private static void ValidateTemplate(string template)
    {
        var remaining = template
            .Replace("{name}", "", StringComparison.Ordinal)
            .Replace("{date}", "", StringComparison.Ordinal)
            .Replace("{year}", "", StringComparison.Ordinal)
            .Replace("{month}", "", StringComparison.Ordinal)
            .Replace("{day}", "", StringComparison.Ordinal)
            .Replace("{counter}", "", StringComparison.Ordinal);
        if (remaining.Contains('{') || remaining.Contains('}'))
            throw new ArgumentException(
                "Use only {name}, {date}, {year}, {month}, {day}, and {counter} in the naming template.",
                nameof(template));
    }

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars()
            .Concat(['/', '\\', ':'])
            .ToHashSet();
        var safe = new string(value
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.', ' ');
        if (safe.Length == 0 || safe is "." or "..")
            safe = "photo";

        var deviceName = safe.Split('.')[0];
        if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Length == 4 &&
            (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            deviceName[3] is >= '1' and <= '9')
            safe = "_" + safe;

        return safe;
    }

    private sealed class StagedSidecar(
        string temporaryPath,
        string destinationPath,
        PhotoImportFileIdentity? sourceIdentity)
    {
        public string TemporaryPath { get; } = temporaryPath;
        public string DestinationPath { get; } = destinationPath;
        public PhotoImportFileIdentity? SourceIdentity { get; } = sourceIdentity;
        public string? FinalHash { get; set; }
    }

    private static bool IsImportFailure(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidOperationException or
            NotSupportedException or
        System.Security.SecurityException or
        CryptographicException or
            Microsoft.Data.Sqlite.SqliteException or
            XmpCore.XmpException;
}

public sealed record PhotoImportCandidate(
    string SourcePath,
    string SourceRoot,
    string FileName,
    long FileSize,
    DateTime SourceLastWriteTimeUtc,
    DateTime DateTaken,
    bool UsesFileDateFallback,
    IReadOnlyList<PhotoImportSidecar> SourceSidecars);

public sealed record PhotoImportSidecar(
    string Path,
    long Size,
    DateTime LastWriteTimeUtc);

public sealed record PhotoImportScanResult(
    IReadOnlyList<PhotoImportCandidate> Candidates,
    IReadOnlyList<string> Warnings);

public sealed record PhotoImportResult(
    IReadOnlyList<PhotoImportItemResult> Items,
    bool Canceled)
{
    public IReadOnlyList<PhotoImportItemResult> SuccessfulItems =>
        Items.Where(item => item.Succeeded).ToList();
}

public sealed record PhotoImportItemResult(
    string SourcePath,
    string? DestinationPath,
    bool Succeeded,
    PhotoImportIdentity? Identity,
    string? Error);

public sealed record PhotoImportIdentity(
    string SourcePath,
    long SourceSize,
    DateTime SourceLastWriteTimeUtc,
    string SourceHash,
    IReadOnlyList<PhotoImportFileIdentity> SourceSidecars,
    string DestinationPath,
    long DestinationSize,
    DateTime DestinationLastWriteTimeUtc,
    string DestinationHash,
    IReadOnlyList<PhotoImportFileIdentity> DestinationSidecars);

public sealed record PhotoImportFileIdentity(
    string Path,
    long Size,
    DateTime LastWriteTimeUtc,
    string Hash);

public sealed record PhotoSourceDeletionResult(
    string SourcePath,
    bool Deleted,
    string? Error);

public sealed record PhotoImportProgress(
    int FilesCompleted,
    int FilesTotal,
    string CurrentFileName,
    PhotoImportStage Stage,
    long BytesCopied,
    long TotalBytes);

public enum PhotoImportStage
{
    Copying,
    Completed,
    VerifyingDeletion
}

public sealed class RemovableDeviceTracker
{
    private readonly HashSet<string> _present = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Refresh(IEnumerable<string> availableSources)
    {
        ArgumentNullException.ThrowIfNull(availableSources);
        var current = availableSources
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inserted = current.Except(_present, StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _present.Clear();
        _present.UnionWith(current);
        return inserted;
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath);
        return string.Equals(
                fullPath,
                pathRoot,
                StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.TrimEndingDirectorySeparator(fullPath);
    }
}

public sealed class RemovableVolumeMonitor : IDisposable
{
    private readonly Func<IEnumerable<string>> _discoverSources;
    private readonly RemovableDeviceTracker _tracker = new();
    private readonly TimeSpan _pollInterval;
    private readonly System.Threading.Timer _timer;
    private readonly object _pollLock = new();
    private bool _started;
    private bool _disposed;

    public event EventHandler<string>? DeviceInserted;
    public event EventHandler<Exception>? ScanFailed;

    public RemovableVolumeMonitor(
        Func<IEnumerable<string>>? discoverSources = null,
        TimeSpan? pollInterval = null)
    {
        _discoverSources = discoverSources ?? DiscoverRemovableDcimRoots;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(3);
        _timer = new System.Threading.Timer(
            OnTimer,
            null,
            System.Threading.Timeout.Infinite,
            System.Threading.Timeout.Infinite);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            return;

        try
        {
            _tracker.Refresh(_discoverSources());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DriveNotFoundException)
        {
            ScanFailed?.Invoke(this, exception);
        }
        _started = true;
        _timer.Change(_pollInterval, _pollInterval);
    }

    private void OnTimer(object? state)
    {
        if (_disposed || !System.Threading.Monitor.TryEnter(_pollLock))
            return;

        try
        {
            foreach (var source in _tracker.Refresh(_discoverSources()))
                DeviceInserted?.Invoke(this, source);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DriveNotFoundException)
        {
            ScanFailed?.Invoke(this, exception);
        }
        finally
        {
            System.Threading.Monitor.Exit(_pollLock);
        }
    }

    private static IEnumerable<string> DiscoverRemovableDcimRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Removable || !drive.IsReady)
                continue;

            var root = drive.RootDirectory.FullName;
            if (Directory.Exists(Path.Combine(root, "DCIM")))
                yield return root;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Dispose();
    }
}
