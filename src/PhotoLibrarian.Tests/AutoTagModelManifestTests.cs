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
    [Theory]
    [InlineData(AutoTagModelCatalog.RamPlusProfileId)]
    [InlineData(AutoTagModelCatalog.TinyClipProfileId)]
    public async Task LoadAsyncAcceptsOnlyThePinnedVersionedAssets(string profileId)
    {
        var profile = AutoTagModelCatalog.ForId(profileId);
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

    [Theory]
    [InlineData(AutoTagModelCatalog.RamPlusProfileId)]
    [InlineData(AutoTagModelCatalog.TinyClipProfileId)]
    public async Task LoadAsyncRejectsAssetHashChanges(string profileId)
    {
        var profile = AutoTagModelCatalog.ForId(profileId);
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

    [Theory]
    [InlineData(AutoTagModelCatalog.RamPlusProfileId)]
    [InlineData(AutoTagModelCatalog.TinyClipProfileId)]
    public async Task LoadAsyncRejectsAssetUrlsOutsideTheVersionedRelease(string profileId)
    {
        var profile = AutoTagModelCatalog.ForId(profileId);
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
    public async Task DownloadVerifiedAssetAsyncCancellationRetainsOnlyUnverifiedPartial()
    {
        var bytes = new byte[3 * 1_048_576];
        var asset = new AutoTagAssetDefinition(
            "asset.bin", null, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
        var root = Path.Combine(Path.GetTempPath(), $"cancel-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, asset.FileName);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        using var client = new HttpClient(new DelegateHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AutoTagModelManager.DownloadVerifiedAssetAsync(
                    client, destination, asset,
                    new Uri("https://assets.example/releases/v1/asset.bin"),
                    0, bytes.Length, new CancellingProgress(cancellation), cancellation.Token));
            Assert.False(File.Exists(destination));
            Assert.InRange(new FileInfo(destination + ".partial").Length, 1, bytes.Length - 1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CancellingProgress(CancellationTokenSource cancellation)
        : IProgress<AutoTagModelDownloadProgress>
    {
        public void Report(AutoTagModelDownloadProgress value)
        {
            if (value.BytesDownloaded > 0)
            {
                cancellation.Cancel();
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadAsyncRejectsDuplicateOrOversizedNotices(bool duplicate)
    {
        var profile = AutoTagModelCatalog.TinyClip;
        var manifest = CreateManifest(profile);
        var notice = manifest.LegalNotices[0];
        if (duplicate)
        {
            manifest.LegalNotices.Add(notice);
        }
        else
        {
            manifest.LegalNotices[0] = new AutoTagManifestAsset
            {
                FileName = notice.FileName, Url = notice.Url,
                Sha256 = notice.Sha256, SizeBytes = 1_048_577
            };
        }
        using var client = CreateClient(manifest);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AutoTagModelManifest.LoadAsync(client, profile.ManifestUri!, profile,
                TestContext.Current.CancellationToken));
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

    [Theory]
    [InlineData(AutoTagModelCatalog.TinyClipProfileId)]
    [InlineData(AutoTagModelCatalog.RamPlusProfileId)]
    public async Task PreparedProfileMissingNoticeNeverInstallsAssets(string profileId)
    {
        var profile = AutoTagModelCatalog.ForId(profileId);
        var manifest = CreateManifest(profile);
        var root = Path.Combine(Path.GetTempPath(), $"missing-notice-{Guid.NewGuid():N}");
        var requests = new List<string>();
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri == profile.ManifestUri
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
                        manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        using var sessions = new OnnxSessionManager(root);
        var manager = new AutoTagModelManager(sessions, httpClient: client);
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                manager.DownloadProfileAssetsAsync(profileId, root,
                    cancellationToken: TestContext.Current.CancellationToken));
            Assert.DoesNotContain(requests, path => path.EndsWith(profile.ModelAsset.FileName));
            Assert.False(File.Exists(manager.GetAssetPath(profileId, profile.ModelAsset, root)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory(Explicit = true)]
    [InlineData(AutoTagModelCatalog.TinyClipProfileId, "MODELBENCH_ASSETS")]
    [InlineData(AutoTagModelCatalog.RamPlusProfileId, "RAM_PLUS_ASSETS")]
    public async Task PreparedProfileDownloadsResumesAndLoads(
        string profileId,
        string assetsVariable)
    {
        var assets = Environment.GetEnvironmentVariable(assetsVariable)
            ?? throw new InvalidOperationException($"Set {assetsVariable} to the verified bundle.");
        var profile = AutoTagModelCatalog.ForId(profileId);
        var manifest = CreateManifest(profile);
        for (var index = 0; index < manifest.LegalNotices.Count; index++)
        {
            var notice = manifest.LegalNotices[index];
            var path = Path.Combine(assets, notice.FileName);
            manifest.LegalNotices[index] = new AutoTagManifestAsset
            {
                FileName = notice.FileName,
                Url = notice.Url,
                Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                SizeBytes = new FileInfo(path).Length
            };
        }
        var root = Path.Combine(Path.GetTempPath(), $"model-install-{Guid.NewGuid():N}");
        var requests = 0;
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            requests++;
            if (request.RequestUri == profile.ManifestUri)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
                        manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
                };
            }
            var name = Uri.UnescapeDataString(Path.GetFileName(request.RequestUri!.AbsolutePath));
            var asset = manifest.Assets.Concat(manifest.LegalNotices)
                .Single(item => item.FileName == name);
            var stream = File.OpenRead(Path.Combine(assets, name));
            var offset = request.Headers.Range?.Ranges.Single().From ?? 0;
            stream.Position = offset;
            var response = new HttpResponseMessage(
                offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            response.Content.Headers.ContentLength = asset.SizeBytes - offset;
            if (offset > 0)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    offset, asset.SizeBytes - 1, asset.SizeBytes);
            }
            return response;
        }));
        try
        {
            using var sessions = new OnnxSessionManager(root);
            var manager = new AutoTagModelManager(sessions, httpClient: client);
            var destination = manager.GetAssetPath(profileId, profile.ModelAsset, null);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var prefix = new byte[4096];
            await using (var source = File.OpenRead(Path.Combine(assets, profile.ModelAsset.FileName)))
            {
                await source.ReadExactlyAsync(prefix, TestContext.Current.CancellationToken);
            }
            await File.WriteAllBytesAsync(
                destination + ".partial", prefix, TestContext.Current.CancellationToken);
            await manager.DownloadProfileAssetsAsync(
                profileId, null, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(3 + manifest.LegalNotices.Count, requests);
            Assert.All(manifest.LegalNotices, notice =>
                Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(destination)!, notice.FileName))));
            Assert.False(File.Exists(destination + ".partial"));
            Assert.Equal(AutoTagDownloadState.Downloaded, manager.GetDownloadStatus(profileId, null).State);
            await manager.EnsureModelAsync(profileId, null, TestContext.Current.CancellationToken);
            var tagger = new AutoTaggingService(sessions, manager);
            tagger.LoadModel(profileId, null);
            await manager.DownloadProfileAssetsAsync(
                profileId, null, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(3 + manifest.LegalNotices.Count, requests);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
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
