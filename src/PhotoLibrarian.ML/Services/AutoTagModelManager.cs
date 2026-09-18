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

public sealed class AutoTagModelManager : IAutoTagModelProvider
{
    private static readonly HttpClient HttpClient = new();
    private readonly OnnxSessionManager _sessionManager;
    private readonly string _defaultDirectory;

    public AutoTagModelManager(
        OnnxSessionManager sessionManager,
        string? defaultDirectory = null)
    {
        _sessionManager = sessionManager;
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

    public async Task EnsureModelAsync(
        string profileId,
        string? modelDirectory,
        CancellationToken cancellationToken = default)
    {
        var definition = AutoTagModelCatalog.ForId(profileId);
        Directory.CreateDirectory(
            GetProfileDirectory(profileId, modelDirectory));
        await EnsureAssetAsync(
            GetAssetPath(profileId, definition.ModelAsset, modelDirectory),
            definition.ModelAsset,
            cancellationToken);
        await EnsureAssetAsync(
            GetAssetPath(profileId, definition.LabelsAsset, modelDirectory),
            definition.LabelsAsset,
            cancellationToken);
    }

    public async Task ImportProfileAssetsAsync(
        string profileId,
        string sourceDirectory,
        string? modelDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var definition = AutoTagModelCatalog.ForId(profileId);
        if (!definition.RequiresLocalImport)
        {
            throw new InvalidOperationException(
                $"{definition.DisplayName} is downloaded automatically " +
                "and does not accept imported assets.");
        }

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
        var existing = paths.Where(File.Exists).ToArray();
        var state = existing.Length switch
        {
            0 => AutoTagDownloadState.NotDownloaded,
            2 => AutoTagDownloadState.Downloaded,
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

    private static async Task EnsureAssetAsync(
        string destinationPath,
        AutoTagAssetDefinition asset,
        CancellationToken cancellationToken)
    {
        if (await HasExpectedHashAsync(
            destinationPath,
            asset,
            cancellationToken))
        {
            return;
        }

        if (asset.RequiresImport)
        {
            throw new InvalidOperationException(
                $"Local model asset '{asset.FileName}' is missing or does " +
                "not match the selected tested model version. Open Settings and " +
                "choose Import model assets.");
        }

        var temporaryPath = destinationPath + ".download";
        try
        {
            using var response = await HttpClient.GetAsync(
                asset.DownloadUri!,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var source =
                await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            if (!await HasExpectedHashAsync(
                temporaryPath,
                asset,
                cancellationToken))
            {
                throw new InvalidDataException(
                    $"Downloaded automatic-tagging asset '{asset.FileName}' failed checksum validation.");
            }

            File.Move(temporaryPath, destinationPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
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
                    $"'{asset.FileName}' does not match the tested RAM++ " +
                    "asset. Select the exact model conversion used by the " +
                    "comparison benchmark.");
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

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(
            stream,
            cancellationToken);
        return Convert.ToHexString(hash).Equals(
            asset.Sha256,
            StringComparison.OrdinalIgnoreCase);
    }
}
