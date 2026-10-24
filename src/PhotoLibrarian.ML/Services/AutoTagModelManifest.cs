// Copyright (c) Duncan Lawler. All Rights Reserved.

using System.Text.Json;

namespace PhotoLibrarian.ML.Services;

public sealed class AutoTagModelManifest
{
    private const int MaximumManifestBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public int SchemaVersion { get; init; }
    public string ProfileId { get; init; } = "";
    public string ManifestUri { get; init; } = "";
    public string Version { get; init; } = "";
    public string PipelineVersion { get; init; } = "";
    public string BuildRevision { get; init; } = "";
    public string License { get; init; } = "";
    public Dictionary<string, string> SourceRevisions { get; init; } = [];
    public List<AutoTagManifestAsset> Assets { get; init; } = [];
    public List<AutoTagManifestAsset> LegalNotices { get; init; } = [];

    public static async Task<AutoTagModelManifest> LoadAsync(
        HttpClient httpClient,
        Uri manifestUri,
        AutoTagModelDefinition definition,
        CancellationToken cancellationToken)
    {
        ValidateHttpsUri(manifestUri, nameof(manifestUri));
        using var response = await httpClient.GetAsync(
            manifestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long length &&
            length > MaximumManifestBytes)
        {
            throw new InvalidDataException(
                "The model release manifest is larger than the supported limit.");
        }

        await using var responseStream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        var manifestBytes = await ReadBoundedAsync(
            responseStream,
            MaximumManifestBytes,
            cancellationToken);
        AutoTagModelManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<AutoTagModelManifest>(
                manifestBytes,
                JsonOptions)
                ?? throw new InvalidDataException(
                    "The model release manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The model release manifest is not valid JSON.",
                exception);
        }
        manifest.Validate(manifestUri, definition);
        return manifest;
    }

    public Uri GetAssetUri(string fileName) =>
        Assets.Single(asset => asset.FileName == fileName).GetUri();

    private void Validate(
        Uri manifestUri,
        AutoTagModelDefinition definition)
    {
        if (Assets is null ||
            LegalNotices is null ||
            SourceRevisions is null ||
            Assets.Any(asset => asset is null) ||
            LegalNotices.Any(notice => notice is null) ||
            SchemaVersion != 1 ||
            !Uri.TryCreate(
                ManifestUri,
                UriKind.Absolute,
                out var declaredManifestUri) ||
            definition.ManifestUri is null ||
            !string.Equals(
                declaredManifestUri.AbsoluteUri,
                manifestUri.AbsoluteUri,
                StringComparison.Ordinal) ||
            !string.Equals(
                definition.ManifestUri.AbsoluteUri,
                manifestUri.AbsoluteUri,
                StringComparison.Ordinal) ||
            !string.Equals(ProfileId, definition.Id, StringComparison.Ordinal) ||
            !string.Equals(
                PipelineVersion,
                definition.PipelineVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                Version,
                definition.PipelineVersion,
                StringComparison.Ordinal) ||
            !string.Equals(License, definition.License, StringComparison.Ordinal) ||
            BuildRevision is not { Length: 40 } ||
            !BuildRevision.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(
                "The model manifest does not describe this tested profile version.");
        }

        var expectedRevisions = definition.SourceRevisions;
        if (expectedRevisions is null ||
            SourceRevisions.Count != expectedRevisions.Count ||
            expectedRevisions.Any(revision =>
                !SourceRevisions.TryGetValue(
                    revision.Key,
                    out var value) ||
                value != revision.Value))
        {
            throw new InvalidDataException(
                "The model manifest source revisions do not match the approved profile.");
        }

        var expectedAssets = new[]
        {
            definition.ModelAsset,
            definition.LabelsAsset
        };
        if (Assets.Count != expectedAssets.Length ||
            Assets.Select(asset => asset.FileName)
                .Distinct(StringComparer.Ordinal)
                .Count() != expectedAssets.Length)
        {
            throw new InvalidDataException(
                "The model manifest has an unexpected asset list.");
        }

        foreach (var expected in expectedAssets)
        {
            var asset = Assets.SingleOrDefault(
                item => item.FileName == expected.FileName);
            if (asset is null ||
                asset.SizeBytes != expected.DownloadSizeBytes ||
                !string.Equals(
                    expected.Sha256,
                    asset.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The model manifest does not match the approved '{expected.FileName}' asset.");
            }
            ValidateReleaseAssetUri(manifestUri, asset, Version, ProfileId);
        }

        var requiredNotices = definition.RequiredNoticeFiles ?? [];
        if (LegalNotices.Select(notice => notice.FileName)
                .Distinct(StringComparer.Ordinal)
                .Count() != LegalNotices.Count ||
            requiredNotices.Any(required =>
                !LegalNotices.Any(notice =>
                    notice.FileName == required)))
        {
            throw new InvalidDataException(
                "The model release is missing required license or modification notices.");
        }
        foreach (var notice in LegalNotices)
        {
            if (notice.SizeBytes <= 0 ||
                !IsSha256(notice.Sha256))
            {
                throw new InvalidDataException(
                    $"The legal notice metadata for '{notice.FileName}' is invalid.");
            }
            ValidateReleaseAssetUri(manifestUri, notice, Version, ProfileId);
        }
    }

    private static void ValidateReleaseAssetUri(
        Uri manifestUri,
        AutoTagManifestAsset asset,
        string version,
        string profileId)
    {
        if (asset.SizeBytes <= 0 || !IsSha256(asset.Sha256))
        {
            throw new InvalidDataException(
                $"The asset metadata for '{asset.FileName}' is invalid.");
        }

        var assetUri = asset.GetUri();
        var markerIndex = manifestUri.AbsolutePath.IndexOf(
            "/manifests/",
            StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new InvalidDataException(
                "The configured model manifest URL has an invalid path.");
        }

        var containerPath = manifestUri.AbsolutePath[..markerIndex];
        var expectedPath =
            $"{containerPath}/releases/" +
            $"{Uri.EscapeDataString(profileId)}/" +
            $"{Uri.EscapeDataString(version)}/" +
            Uri.EscapeDataString(asset.FileName);
        if (!assetUri.Scheme.Equals(
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            assetUri.Host != manifestUri.Host ||
            assetUri.Port != manifestUri.Port ||
            !string.IsNullOrEmpty(assetUri.UserInfo) ||
            !string.IsNullOrEmpty(assetUri.Query) ||
            !string.IsNullOrEmpty(assetUri.Fragment) ||
            assetUri.AbsolutePath != expectedPath)
        {
            throw new InvalidDataException(
                $"The release URL for '{asset.FileName}' is not a versioned HTTPS asset URL.");
        }
    }

    private static void ValidateHttpsUri(Uri uri, string parameterName)
    {
        if (!uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException(
                $"The configured model manifest URL '{parameterName}' must be an absolute HTTPS URL without credentials or query parameters.");
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(Uri.IsHexDigit);

    private static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(
                buffer,
                cancellationToken);
            if (read == 0)
            {
                return destination.ToArray();
            }
            if (destination.Length + read > maximumBytes)
            {
                throw new InvalidDataException(
                    "The model release manifest is larger than the supported limit.");
            }
            await destination.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }
    }
}

public sealed class AutoTagManifestAsset
{
    public string FileName { get; init; } = "";
    public string Url { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = "";

    public Uri GetUri()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri))
        {
            throw new InvalidDataException(
                $"The model release contains an invalid URL for '{FileName}'.");
        }
        return uri;
    }
}
