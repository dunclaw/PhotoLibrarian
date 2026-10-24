// Copyright (c) Duncan Lawler. All Rights Reserved.

using PhotoLibrarian.ML.Services;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class AutoTagModelManifestTests
{
    [Fact]
    public async Task LoadAsyncAcceptsOnlyThePinnedVersionedAssets()
    {
        var profile = AutoTagModelCatalog.RamPlus;
        var manifest = CreateManifest(profile);
        using var client = CreateClient(manifest);

        var loaded = await AutoTagModelManifest.LoadAsync(
            client,
            profile.ManifestUri!,
            profile,
            TestContext.Current.CancellationToken);

        Assert.Equal(profile.Id, loaded.ProfileId);
        Assert.Equal(profile.PipelineVersion, loaded.Version);
        Assert.Equal(
            profile.ModelAsset.Sha256,
            loaded.Assets.Single(asset =>
                asset.FileName == profile.ModelAsset.FileName).Sha256);
        Assert.Equal(
            profile.RequiredNoticeFiles,
            loaded.LegalNotices.Select(notice => notice.FileName));
    }

    [Fact]
    public async Task LoadAsyncRejectsAssetHashChanges()
    {
        var profile = AutoTagModelCatalog.RamPlus;
        var manifest = CreateManifest(profile);
        var original = manifest.Assets[0];
        manifest.Assets[0] = new AutoTagManifestAsset
        {
            FileName = original.FileName,
            Url = original.Url,
            SizeBytes = original.SizeBytes,
            Sha256 = new string('0', 64)
        };
        using var client = CreateClient(manifest);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AutoTagModelManifest.LoadAsync(
                client,
                profile.ManifestUri!,
                profile,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsyncRejectsAssetUrlsOutsideTheVersionedRelease()
    {
        var profile = AutoTagModelCatalog.RamPlus;
        var manifest = CreateManifest(profile);
        var original = manifest.Assets[0];
        manifest.Assets[0] = new AutoTagManifestAsset
        {
            FileName = original.FileName,
            Url = "https://attacker.example/model.onnx",
            SizeBytes = original.SizeBytes,
            Sha256 = original.Sha256
        };
        using var client = CreateClient(manifest);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AutoTagModelManifest.LoadAsync(
                client,
                profile.ManifestUri!,
                profile,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsyncRejectsManifestUriOutsideTheConfiguredRelease()
    {
        var profile = AutoTagModelCatalog.RamPlus;
        var manifest = CreateManifest(
            profile,
            "https://other.example/manifest.json");
        using var client = CreateClient(manifest);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AutoTagModelManifest.LoadAsync(
                client,
                profile.ManifestUri!,
                profile,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadVerifiedAssetAsyncResumesPartialRange()
    {
        var bytes = Encoding.UTF8.GetBytes("resumable model");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var asset = new AutoTagAssetDefinition(
            "asset.bin",
            null,
            hash,
            bytes.Length);
        var root = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, asset.FileName);
        var offset = 4;
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            Assert.Equal(offset, request.Headers.Range!.Ranges.Single().From);
            var response = new HttpResponseMessage(
                HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(bytes[offset..])
            };
            response.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(
                    offset,
                    bytes.Length - 1,
                    bytes.Length);
            return response;
        }));

        try
        {
            await File.WriteAllBytesAsync(
                destination + ".partial",
                bytes[..offset],
                TestContext.Current.CancellationToken);
            var progress = new List<AutoTagModelDownloadProgress>();

            await AutoTagModelManager.DownloadVerifiedAssetAsync(
                client,
                destination,
                asset,
                new Uri("https://assets.example/releases/v1/asset.bin"),
                0,
                asset.DownloadSizeBytes,
                new RecordingProgress(progress),
                TestContext.Current.CancellationToken);

            Assert.Equal(bytes, await File.ReadAllBytesAsync(
                destination,
                TestContext.Current.CancellationToken));
            Assert.False(File.Exists(destination + ".partial"));
            Assert.Contains(progress, item =>
                item.BytesDownloaded == asset.DownloadSizeBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadVerifiedAssetAsyncRetainsInterruptedPartialForRetry()
    {
        var bytes = Encoding.UTF8.GetBytes("resumable model");
        var asset = new AutoTagAssetDefinition(
            "asset.bin",
            null,
            Convert.ToHexString(SHA256.HashData(bytes)),
            bytes.Length);
        var root = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, asset.FileName);
        using var client = new HttpClient(new DelegateHandler(_ =>
        {
            var content = new StreamContent(
                new FailingAfterBytesStream(bytes, 4));
            content.Headers.ContentLength = bytes.Length;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            };
        }));

        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                AutoTagModelManager.DownloadVerifiedAssetAsync(
                    client,
                    destination,
                    asset,
                    new Uri("https://assets.example/releases/v1/asset.bin"),
                    0,
                    asset.DownloadSizeBytes,
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.Equal(
                4,
                new FileInfo(destination + ".partial").Length);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static HttpClient CreateClient(AutoTagModelManifest manifest) =>
        new(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
                manifest,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }))
        }));

    private static AutoTagModelManifest CreateManifest(
        AutoTagModelDefinition profile,
        string? manifestUriOverride = null)
    {
        var manifestUri = profile.ManifestUri!;
        var markerIndex = manifestUri.AbsolutePath.IndexOf(
            "/manifests/",
            StringComparison.Ordinal);
        var containerPath = manifestUri.AbsolutePath[..markerIndex];
        var releaseRoot =
            $"{manifestUri.GetLeftPart(UriPartial.Authority)}" +
            $"{containerPath}/releases/" +
            $"{Uri.EscapeDataString(profile.Id)}/" +
            $"{Uri.EscapeDataString(profile.PipelineVersion)}";

        return new AutoTagModelManifest
        {
            SchemaVersion = 1,
            ProfileId = profile.Id,
            ManifestUri = manifestUriOverride ?? manifestUri.AbsoluteUri,
            Version = profile.PipelineVersion,
            PipelineVersion = profile.PipelineVersion,
            BuildRevision = new string('a', 40),
            License = profile.License!,
            SourceRevisions = new Dictionary<string, string>(
                profile.SourceRevisions!,
                StringComparer.Ordinal),
            Assets =
            [
                CreateAsset(profile.ModelAsset),
                CreateAsset(profile.LabelsAsset)
            ],
            LegalNotices = profile.RequiredNoticeFiles!
                .Select(name => new AutoTagManifestAsset
                {
                    FileName = name,
                    Url = $"{releaseRoot}/{Uri.EscapeDataString(name)}",
                    SizeBytes = 32,
                    Sha256 = new string('b', 64)
                })
                .ToList()
        };

        AutoTagManifestAsset CreateAsset(AutoTagAssetDefinition asset) =>
            new()
            {
                FileName = asset.FileName,
                Url = $"{releaseRoot}/{Uri.EscapeDataString(asset.FileName)}",
                SizeBytes = asset.DownloadSizeBytes,
                Sha256 = asset.Sha256
            };
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class RecordingProgress(
        ICollection<AutoTagModelDownloadProgress> reports)
        : IProgress<AutoTagModelDownloadProgress>
    {
        public void Report(AutoTagModelDownloadProgress value) =>
            reports.Add(value);
    }

    private sealed class FailingAfterBytesStream(
        byte[] data,
        int bytesBeforeFailure) : Stream
    {
        private int _position;
        private bool _failed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
        {
            if (_position >= bytesBeforeFailure && !_failed)
            {
                _failed = true;
                throw new IOException("Simulated interrupted connection.");
            }
            var read = Math.Min(
                Math.Min(count, bytesBeforeFailure - _position),
                data.Length - _position);
            Array.Copy(data, _position, buffer, offset, read);
            _position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temp = new byte[buffer.Length];
            var read = Read(temp, 0, temp.Length);
            temp.AsMemory(0, read).CopyTo(buffer);
            return ValueTask.FromResult(read);
        }

        public override void Flush() =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();
    }
}
