// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace PhotoLibrarian.ML.Services;

public enum AutoTagDownloadState
{
    NotDownloaded,
    PartiallyDownloaded,
    Downloaded
}

public sealed record AutoTagDownloadStatus(
    AutoTagDownloadState State,
    long BytesOnDisk);

public sealed record AutoTagModelDownloadProgress(
    string FileName,
    long BytesDownloaded,
    long TotalBytes);

public sealed class AutoTagModelManager : IAutoTagModelProvider
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly HttpClient _httpClient;
    private readonly OnnxSessionManager _sessionManager;
    private readonly string _defaultDirectory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim>
        _profileLocks = new(StringComparer.Ordinal);

    public AutoTagModelManager(
        OnnxSessionManager sessionManager,
        string? defaultDirectory = null,
        HttpClient? httpClient = null)
    {
        _sessionManager = sessionManager;
        _httpClient = httpClient ?? SharedHttpClient;
        _defaultDirectory = Path.GetFullPath(defaultDirectory ?? Path.Combine(
            sessionManager.ModelDirectory,
            "AutoTagging"));
        Directory.CreateDirectory(_defaultDirectory);
    }

    public string DefaultDirectory => _defaultDirectory;

    public string ResolveRootDirectory(string? modelDirectory) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(modelDirectory)
            ? _defaultDirectory
            : modelDirectory);

    public string PrepareCustomDirectory(string modelDirectory)
    {
        if (string.IsNullOrWhiteSpace(modelDirectory))
        {
            throw new ArgumentException(
                "Choose a model download folder.",
                nameof(modelDirectory));
        }

        var fullPath = Path.GetFullPath(modelDirectory);
        Directory.CreateDirectory(fullPath);
        var probePath = Path.Combine(
            fullPath,
            $".photolibrarian-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
            stream.WriteByte(0);
        }
        finally
        {
            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }

        return fullPath;
    }

    public string GetAssetPath(
        string profileId,
        AutoTagAssetDefinition asset,
        string? modelDirectory)
    {
        var definition = AutoTagModelCatalog.ForId(profileId);
        if (asset != definition.ModelAsset &&
            asset != definition.LabelsAsset)
        {
            throw new ArgumentException(
                "The asset does not belong to the selected profile.",
                nameof(asset));
        }

        return Path.Combine(
            GetProfileDirectory(profileId, modelDirectory),
            asset.FileName);
    }

    public Task EnsureModelAsync(
        string profileId,
        string? modelDirectory,
        CancellationToken cancellationToken = default) =>
        DownloadProfileAssetsAsync(
            profileId,
            modelDirectory,
            progress: null,
            cancellationToken);

    public async Task DownloadProfileAssetsAsync(
        string profileId,
        string? modelDirectory,
        IProgress<AutoTagModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var definition = AutoTagModelCatalog.ForId(profileId);
        var profileLock = _profileLocks.GetOrAdd(
            profileId,
            _ => new SemaphoreSlim(1, 1));
        await profileLock.WaitAsync(cancellationToken);
        try
        {
            await DownloadProfileAssetsCoreAsync(
                definition,
                modelDirectory,
                progress,
                cancellationToken);
        }
        finally
        {
            profileLock.Release();
        }
    }

    private async Task DownloadProfileAssetsCoreAsync(
        AutoTagModelDefinition definition,
        string? modelDirectory,
        IProgress<AutoTagModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var profileId = definition.Id;
        var profileDirectory =
            GetProfileDirectory(profileId, modelDirectory);
        Directory.CreateDirectory(profileDirectory);

        var assets = new[]
        {
            definition.ModelAsset,
            definition.LabelsAsset
        };
        var existing = new bool[assets.Length];
        for (var index = 0; index < assets.Length; index++)
        {
            existing[index] = await HasExpectedHashAsync(
                GetAssetPath(profileId, assets[index], modelDirectory),
                assets[index],
                cancellationToken);
        }

        AutoTagModelManifest? manifest = null;
        if (existing.Any(isPresent => !isPresent) &&
            definition.ManifestUri is not null)
        {
            manifest = await AutoTagModelManifest.LoadAsync(
                _httpClient,
                definition.ManifestUri,
                definition,
                cancellationToken);
        }

        long bytesCompleted = 0;
        var totalBytes = assets.Sum(asset => asset.DownloadSizeBytes) +
            (manifest?.LegalNotices.Sum(notice => notice.SizeBytes) ?? 0);
        if (manifest is not null)
        {
            foreach (var notice in manifest.LegalNotices)
            {
                var noticeAsset = new AutoTagAssetDefinition(
                    notice.FileName, notice.GetUri(), notice.Sha256, notice.SizeBytes);
                await DownloadVerifiedAssetAsync(
                    _httpClient,
                    Path.Combine(GetProfileDirectory(profileId, modelDirectory), notice.FileName),
                    noticeAsset,
                    noticeAsset.DownloadUri,
                    bytesCompleted,
                    totalBytes,
                    progress,
                    cancellationToken);
                bytesCompleted += notice.SizeBytes;
            }
        }
        for (var index = 0; index < assets.Length; index++)
        {
            var asset = assets[index];
            var assetPath = GetAssetPath(
                profileId,
                asset,
                modelDirectory);
            if (existing[index])
            {
                bytesCompleted += asset.DownloadSizeBytes;
                progress?.Report(new AutoTagModelDownloadProgress(
                    asset.FileName,
                    bytesCompleted,
                    totalBytes));
                continue;
            }

            var downloadUri = manifest?.GetAssetUri(asset.FileName)
                ?? asset.DownloadUri;
            await DownloadVerifiedAssetAsync(
                _httpClient,
                assetPath,
                asset,
                downloadUri,
                bytesCompleted,
                totalBytes,
                progress,
                cancellationToken);
            bytesCompleted += asset.DownloadSizeBytes;
        }
    }

    public async Task ImportProfileAssetsAsync(
        string profileId,
        string sourceDirectory,
        string? modelDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var definition = AutoTagModelCatalog.ForId(profileId);
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Model asset folder not found: {sourceDirectory}");
        }

        var pending = new List<(string Temporary, string Destination)>();
        try
        {
            foreach (var asset in new[]
            {
                definition.ModelAsset,
                definition.LabelsAsset
            })
            {
                var sourcePath = Path.Combine(
                    sourceDirectory,
                    asset.FileName);
                if (!File.Exists(sourcePath))
                {
                    throw new FileNotFoundException(
                        $"The selected folder does not contain " +
                        $"'{asset.FileName}'.",
                        sourcePath);
                }

                var destinationPath = GetAssetPath(
                    profileId,
                    asset,
                    modelDirectory);
                var temporaryPath =
                    destinationPath + $".importing.{Guid.NewGuid():N}";
                await CopyVerifiedAssetAsync(
                    asset,
                    sourcePath,
                    temporaryPath,
                    cancellationToken);
                pending.Add((temporaryPath, destinationPath));
            }

            _sessionManager.UnloadModelPath(
                GetAssetPath(
                    profileId,
                    definition.ModelAsset,
                    modelDirectory));
            foreach (var (temporary, destination) in pending)
            {
                File.Move(temporary, destination, true);
            }
        }
        finally
        {
            foreach (var (temporary, _) in pending)
            {
                File.Delete(temporary);
            }
        }
    }

    public AutoTagDownloadStatus GetDownloadStatus(
        string profileId,
        string? modelDirectory)
    {
        var definition = AutoTagModelCatalog.ForId(profileId);
        var paths = new[]
        {
            GetAssetPath(profileId, definition.ModelAsset, modelDirectory),
            GetAssetPath(profileId, definition.LabelsAsset, modelDirectory)
        };
        var existing = new List<string>();
        var completeAssets = 0;
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                existing.Add(path);
                completeAssets++;
            }
            else if (File.Exists(path + ".partial"))
            {
                existing.Add(path + ".partial");
            }
        }
        var state = completeAssets == paths.Length
            ? AutoTagDownloadState.Downloaded
            : existing.Count switch
        {
            0 => AutoTagDownloadState.NotDownloaded,
            _ => AutoTagDownloadState.PartiallyDownloaded
        };
        return new AutoTagDownloadStatus(
            state,
            existing.Sum(path => new FileInfo(path).Length));
    }

    public Task DeleteProfileAssetsAsync(
        string profileId,
        string? modelDirectory)
    {
        var definition = AutoTagModelCatalog.ForId(profileId);
        _sessionManager.UnloadModelPath(
            GetAssetPath(profileId, definition.ModelAsset, modelDirectory));
        var profileDirectory =
            GetProfileDirectory(profileId, modelDirectory);
        if (Directory.Exists(profileDirectory))
        {
            Directory.Delete(profileDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    public async Task DeleteAllProfileAssetsAsync(string? modelDirectory)
    {
        foreach (var profile in AutoTagModelCatalog.Profiles)
        {
            await DeleteProfileAssetsAsync(profile.Id, modelDirectory);
        }
    }

    private string GetProfileDirectory(
        string profileId,
        string? modelDirectory)
    {
        AutoTagModelCatalog.ForId(profileId);
        return Path.Combine(
            ResolveRootDirectory(modelDirectory),
            profileId);
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    internal static async Task DownloadVerifiedAssetAsync(
        HttpClient httpClient,
        string destinationPath,
        AutoTagAssetDefinition asset,
        Uri? downloadUri,
        long bytesCompleted,
        long totalBytes,
        IProgress<AutoTagModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        var downloadToken = timeout.Token;

        if (await HasExpectedHashAsync(
            destinationPath,
            asset,
            downloadToken))
        {
            return;
        }

        if (downloadUri is null)
        {
            throw new InvalidOperationException(
                $"Local model asset '{asset.FileName}' is missing or does " +
                "not match the selected tested model version. Downloading is " +
                "not available for this profile yet; choose Import model assets.");
        }

        var temporaryPath = destinationPath + ".partial";
        var offset = 0L;
        if (File.Exists(temporaryPath))
        {
            offset = new FileInfo(temporaryPath).Length;
            if (offset > asset.DownloadSizeBytes)
            {
                File.Delete(temporaryPath);
                offset = 0;
            }
            else if (offset == asset.DownloadSizeBytes &&
                     await HasExpectedHashAsync(
                         temporaryPath,
                         asset,
                         downloadToken))
            {
                File.Move(temporaryPath, destinationPath, true);
                progress?.Report(new AutoTagModelDownloadProgress(
                    asset.FileName,
                    bytesCompleted + asset.DownloadSizeBytes,
                    totalBytes));
                return;
            }
            else if (offset == asset.DownloadSizeBytes)
            {
                File.Delete(temporaryPath);
                offset = 0;
            }
        }

        progress?.Report(new AutoTagModelDownloadProgress(
            asset.FileName,
            bytesCompleted + offset,
            totalBytes));

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            downloadUri);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            downloadToken);
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.Unit != "bytes" ||
                range.From != (offset > 0 ? offset : 0) ||
                range.Length != asset.DownloadSizeBytes)
            {
                throw new InvalidDataException(
                    $"The model server returned an invalid byte range for '{asset.FileName}'.");
            }
        }
        else
        {
            response.EnsureSuccessStatusCode();
            if (offset > 0)
            {
                offset = 0;
            }
        }

        var expectedResponseBytes =
            asset.DownloadSizeBytes - offset;
        if (response.Content.Headers.ContentLength is long responseLength &&
            responseLength != expectedResponseBytes)
        {
            throw new InvalidDataException(
                $"The model server returned an unexpected size for '{asset.FileName}'.");
        }

        var downloaded = offset;
        try
        {
            var fileMode = offset > 0
                ? FileMode.Append
                : FileMode.Create;
            await using (var source =
                await response.Content.ReadAsStreamAsync(downloadToken))
            await using (var destination = new FileStream(
                temporaryPath,
                fileMode,
                FileAccess.Write,
                FileShare.None,
                1_048_576,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[1_048_576];
                while (true)
                {
                    var read = await source.ReadAsync(
                        buffer,
                        downloadToken);
                    if (read == 0)
                    {
                        break;
                    }

                    downloaded += read;
                    if (downloaded > asset.DownloadSizeBytes)
                    {
                        throw new InvalidDataException(
                            $"The model server sent too many bytes for '{asset.FileName}'.");
                    }
                    await destination.WriteAsync(
                        buffer.AsMemory(0, read),
                        downloadToken);
                    progress?.Report(new AutoTagModelDownloadProgress(
                        asset.FileName,
                        bytesCompleted + downloaded,
                        totalBytes));
                }
                await destination.FlushAsync(downloadToken);
            }

            if (downloaded != asset.DownloadSizeBytes)
            {
                throw new InvalidDataException(
                    $"Downloaded model asset '{asset.FileName}' has an unexpected size.");
            }

            if (!await HasExpectedHashAsync(
                temporaryPath,
                asset,
                downloadToken))
            {
                File.Delete(temporaryPath);
                throw new InvalidDataException(
                    $"Downloaded automatic-tagging asset '{asset.FileName}' failed SHA-256 validation. The corrupt partial file was removed.");
            }

            File.Move(temporaryPath, destinationPath, true);
        }
        catch (InvalidDataException)
        {
            if (File.Exists(temporaryPath) &&
                new FileInfo(temporaryPath).Length > asset.DownloadSizeBytes)
            {
                File.Delete(temporaryPath);
            }
            throw;
        }
    }

    internal static async Task CopyVerifiedAssetAsync(
        AutoTagAssetDefinition asset,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(destinationPath)!);
        var destinationCreated = false;
        try
        {
            await using (var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1_048_576,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1_048_576,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                destinationCreated = true;
                await source.CopyToAsync(
                    destination,
                    1_048_576,
                    cancellationToken);
            }

            if (!await HasExpectedHashAsync(
                destinationPath,
                asset,
                cancellationToken))
            {
                throw new InvalidDataException(
                    $"'{asset.FileName}' does not match the tested " +
                    "profile asset. Select the exact model conversion used " +
                    "by the comparison benchmark.");
            }
        }
        catch
        {
            if (destinationCreated)
            {
                File.Delete(destinationPath);
            }

            throw;
        }
    }

    private static async Task<bool> HasExpectedHashAsync(
        string path,
        AutoTagAssetDefinition asset,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (new FileInfo(path).Length != asset.DownloadSizeBytes)
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(
            stream,
            cancellationToken);
        return Convert.ToHexString(hash).Equals(
            asset.Sha256,
            StringComparison.OrdinalIgnoreCase);
    }
}
