namespace PhotoLibrarian.ML.Services;

public interface IFaceModelProvider
{
    Task EnsureModelsAsync(CancellationToken cancellationToken = default);
}

public sealed class FaceModelProvider : IFaceModelProvider
{
    private static readonly HttpClient HttpClient = new();
    private readonly OnnxSessionManager _sessionManager;

    public FaceModelProvider(OnnxSessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public async Task EnsureModelsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var model in FaceModelCatalog.All)
        {
            await EnsureModelAsync(model, cancellationToken);
        }
    }

    private async Task EnsureModelAsync(
        FaceModelDefinition model,
        CancellationToken cancellationToken)
    {
        var destinationPath = Path.Combine(_sessionManager.ModelDirectory, model.FileName);
        if (await HasExpectedHashAsync(destinationPath, model, cancellationToken))
        {
            return;
        }

        var temporaryPath = destinationPath + ".download";
        try
        {
            using var response = await HttpClient.GetAsync(
                model.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
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

            if (!await HasExpectedHashAsync(temporaryPath, model, cancellationToken))
            {
                throw new InvalidDataException(
                    $"Downloaded face model '{model.FileName}' failed checksum validation.");
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

    private static async Task<bool> HasExpectedHashAsync(
        string path,
        FaceModelDefinition model,
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
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).Equals(model.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
