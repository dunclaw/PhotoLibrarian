// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Text;
using Microsoft.Data.Sqlite;
using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;
using PhotoLibrarian.Core.Services;
using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

/// <summary>
/// Regression coverage for issue #183: a camera RAW+JPEG pair whose JPEG cannot take embedded
/// tags must not re-queue background tagging, or repeat the fallback warning, on every launch.
/// All fixtures are generated in a temporary directory.
/// </summary>
public sealed class TagSidecarCompanionTests : IDisposable
{
    private const string MalformedXmp =
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF " +
        "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Desc";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"PhotoLibrarian-tag-sidecar-companion-{Guid.NewGuid():N}");
    private readonly List<MetadataWriteFallbackEventArgs> _fallbacks = [];

    public TagSidecarCompanionTests()
    {
        Directory.CreateDirectory(_directory);
        MetadataWriterService.SidecarFallback += OnSidecarFallback;
    }

    [Fact]
    public async Task RawJpegPair_MalformedJpegXmp_WarnsOnceAndStaysTaggedAcrossRestarts()
    {
        var jpegPath = Path.Combine(_directory, "R0001.jpg");
        var rawPath = Path.Combine(_directory, "R0001.cr3");
        await CreateMalformedXmpJpegAsync(jpegPath);
        await File.WriteAllBytesAsync(rawPath, [1, 2, 3], TestContext.Current.CancellationToken);
        var originalJpeg = await File.ReadAllBytesAsync(jpegPath, TestContext.Current.CancellationToken);
        using var library = await TestLibrary.CreateAsync(Path.Combine(_directory, "library.db"));

        for (var launch = 0; launch < 3; launch++)
        {
            await library.Indexer.IndexFileAsync(rawPath, TestContext.Current.CancellationToken, verifyContent: false);
            await library.Indexer.IndexFileAsync(jpegPath, TestContext.Current.CancellationToken, verifyContent: false);
            var result = await CreateProcessor(library.Tags).ProcessLibraryAsync(
                CreateApprovedSettings(),
                TestContext.Current.CancellationToken);

            Assert.Equal(launch == 0 ? 2 : 0, result.Processed);
            Assert.Equal(0, result.Failed);
            Assert.Empty(await library.Tags.GetImagesNeedingAutoTagsAsync(
                AutoTagModelCatalog.MobileNetV2.PipelineVersion,
                TestContext.Current.CancellationToken));
        }

        var fallback = Assert.Single(_fallbacks);
        Assert.Equal(jpegPath, fallback.ImagePath);
        Assert.Equal("tags", fallback.PropertyName);
        Assert.Equal($"{jpegPath}.xmp", fallback.SidecarPath);
        Assert.Contains("0x80070057", fallback.Reason);
        Assert.Equal(originalJpeg, await File.ReadAllBytesAsync(jpegPath, TestContext.Current.CancellationToken));

        var reader = new MetadataReaderService();
        var jpegImage = (await library.Images.GetByPathAsync(jpegPath))!;
        var rawImage = (await library.Images.GetByPathAsync(rawPath))!;
        Assert.Equal(jpegImage.SourceIdentity, reader.ReadPortableTagMetadata(jpegPath)!.SourceIdentity);
        Assert.Equal(rawImage.SourceIdentity, reader.ReadPortableTagMetadata(rawPath)!.SourceIdentity);
        Assert.Equal(Path.ChangeExtension(rawPath, ".xmp"), TagSidecarLocator.ResolveReadPath(rawPath));
        Assert.Equal($"{jpegPath}.xmp", TagSidecarLocator.ResolveReadPath(jpegPath));
    }

    [Fact]
    public async Task SharedLegacySidecar_IsNotAttributedToTheJpegCompanion()
    {
        var jpegPath = Path.Combine(_directory, "R0002.jpg");
        var rawPath = Path.Combine(_directory, "R0002.cr3");
        await WicTestImage.CreateAsync(jpegPath, 32, 32, jpeg: true);
        await File.WriteAllBytesAsync(rawPath, [4, 5, 6], TestContext.Current.CancellationToken);
        await TagWriterService.WriteTagsToSidecarAsync(
            rawPath,
            ["raw-only"],
            [new ImageTag { Tag = "raw-only", Source = TagSource.Manual, Confidence = 1.0f }]);
        using var library = await TestLibrary.CreateAsync(Path.Combine(_directory, "library.db"));

        Assert.True(await library.Indexer.IndexFileAsync(jpegPath, TestContext.Current.CancellationToken));
        Assert.True(await library.Indexer.IndexFileAsync(rawPath, TestContext.Current.CancellationToken));

        var jpegImage = (await library.Images.GetByPathAsync(jpegPath))!;
        var rawImage = (await library.Images.GetByPathAsync(rawPath))!;
        Assert.DoesNotContain(
            await library.Tags.GetTagsAsync(jpegImage.Id, TestContext.Current.CancellationToken),
            tag => tag.Tag == "raw-only");
        Assert.Contains(
            await library.Tags.GetTagsAsync(rawImage.Id, TestContext.Current.CancellationToken),
            tag => tag.Tag == "raw-only");
        Assert.Null(TagSidecarLocator.ResolveReadPath(jpegPath));
    }

    [Fact]
    public async Task LoneJpegWithLegacySidecar_StillReadsAndUpdatesIt()
    {
        var jpegPath = Path.Combine(_directory, "legacy.jpg");
        var legacySidecar = Path.ChangeExtension(jpegPath, ".xmp");
        await CreateMalformedXmpJpegAsync(jpegPath);
        await TagWriterService.WriteTagsToSidecarAsync(jpegPath, ["kept"]);
        File.Move($"{jpegPath}.xmp", legacySidecar);
        _fallbacks.Clear();

        Assert.Equal(["kept"], TagWriterService.ReadTagsFromSidecar(jpegPath));
        await TagWriterService.WriteTagsToSidecarAsync(jpegPath, []);

        Assert.False(File.Exists($"{jpegPath}.xmp"));
        Assert.Empty(TagWriterService.ReadTagsFromSidecar(jpegPath));
        Assert.Equal(legacySidecar, TagSidecarLocator.ResolveReadPath(jpegPath));
        Assert.Equal(legacySidecar, Assert.Single(_fallbacks).SidecarPath);
    }

    [Fact]
    public async Task SidecarWriteFailure_KeepsEditsPendingThenRetrySucceedsWithoutRepeatingWarning()
    {
        var jpegPath = Path.Combine(_directory, "R0003.jpg");
        var rawPath = Path.Combine(_directory, "R0003.cr3");
        var blockedSidecar = $"{jpegPath}.xmp";
        await CreateMalformedXmpJpegAsync(jpegPath);
        await File.WriteAllBytesAsync(rawPath, [7, 8, 9], TestContext.Current.CancellationToken);
        var originalJpeg = await File.ReadAllBytesAsync(jpegPath, TestContext.Current.CancellationToken);
        using var library = await TestLibrary.CreateAsync(Path.Combine(_directory, "library.db"));
        await library.Indexer.IndexFileAsync(jpegPath, TestContext.Current.CancellationToken);
        var jpegImage = (await library.Images.GetByPathAsync(jpegPath))!;
        await library.Tags.AddTagAsync(new ImageTag
        {
            ImageId = jpegImage.Id,
            Tag = "family",
            Source = TagSource.Manual,
            Confidence = 1.0f
        });
        Directory.CreateDirectory(blockedSidecar);

        var failed = await CreateProcessor(library.Tags).ProcessLibraryAsync(
            CreateApprovedSettings(),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, failed.Failed);
        Assert.Empty(_fallbacks);
        Assert.Equal(originalJpeg, await File.ReadAllBytesAsync(jpegPath, TestContext.Current.CancellationToken));
        Assert.Single(await library.Tags.GetImagesNeedingAutoTagsAsync(
            AutoTagModelCatalog.MobileNetV2.PipelineVersion,
            TestContext.Current.CancellationToken));
        Assert.Contains(
            await library.Tags.GetTagsAsync(jpegImage.Id, TestContext.Current.CancellationToken),
            tag => tag.Tag == "family" && tag.Source == TagSource.Manual);

        Directory.Delete(blockedSidecar);
        var retried = await CreateProcessor(library.Tags).ProcessLibraryAsync(
            CreateApprovedSettings(),
            TestContext.Current.CancellationToken);
        Assert.Equal(new BatchTagResult(1, 0, 1), retried);
        Assert.Single(_fallbacks);

        await library.Indexer.IndexFileAsync(jpegPath, TestContext.Current.CancellationToken, verifyContent: false);
        var restarted = await CreateProcessor(library.Tags).ProcessLibraryAsync(
            CreateApprovedSettings(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, restarted.Processed);
        Assert.Single(_fallbacks);
        var portable = new MetadataReaderService().ReadPortableTagMetadata(jpegPath)!;
        Assert.Contains(portable.Tags, tag => tag.Tag == "family");
        Assert.Contains(portable.Tags, tag => tag.Tag == "Auto/dog");
        Assert.Equal(originalJpeg, await File.ReadAllBytesAsync(jpegPath, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        MetadataWriterService.SidecarFallback -= OnSidecarFallback;
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private void OnSidecarFallback(object? sender, MetadataWriteFallbackEventArgs e)
    {
        if (e.ImagePath.StartsWith(_directory, StringComparison.OrdinalIgnoreCase))
            _fallbacks.Add(e);
    }

    /// <summary>
    /// Creates a JPEG whose embedded XMP packet is truncated. Windows Imaging Component rejects
    /// in-place metadata updates to such files with E_INVALIDARG ("Value does not fall within
    /// the expected range").
    /// </summary>
    private static async Task CreateMalformedXmpJpegAsync(string path)
    {
        await WicTestImage.CreateAsync(path, 32, 32, jpeg: true);
        var image = await File.ReadAllBytesAsync(path);
        var payload = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0")
            .Concat(Encoding.UTF8.GetBytes(MalformedXmp))
            .ToArray();
        var length = payload.Length + 2;
        var segment = new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length };
        await File.WriteAllBytesAsync(
            path,
            image.Take(2).Concat(segment).Concat(payload).Concat(image.Skip(2)).ToArray());
    }

    private static BatchTagProcessor CreateProcessor(IAutoTagStore store) =>
        new(store, new FakeModelProvider(), new FakeTagger(), new IdleActivityGate());

    private static AutoTaggingSettings CreateApprovedSettings()
    {
        var profile = AutoTagModelCatalog.MobileNetV2;
        return new AutoTaggingSettings(
            true,
            profile.Id,
            profile.DefaultMaximumTags,
            profile.DefaultConfidenceThreshold,
            ProfileQualityStatuses: new Dictionary<string, AutoTagQualityStatus>
            {
                [profile.ApprovalKey] = AutoTagQualityStatus.Approved
            });
    }

    private sealed class TestLibrary : IDisposable
    {
        private readonly FolderScannerService _scanner = new();

        private TestLibrary(CacheDatabase database)
        {
            Database = database;
            Images = new ImageRepository(database);
            Tags = new TagRepository(database);
            Indexer = new LibraryIndexingService(
                database,
                Images,
                Tags,
                new FaceRepository(database),
                _scanner,
                new MetadataReaderService(),
                new FaceMetadataStore());
        }

        public CacheDatabase Database { get; }
        public ImageRepository Images { get; }
        public TagRepository Tags { get; }
        public LibraryIndexingService Indexer { get; }

        public static async Task<TestLibrary> CreateAsync(string databasePath)
        {
            var database = new CacheDatabase(databasePath);
            await database.InitializeAsync();
            return new TestLibrary(database);
        }

        public void Dispose()
        {
            _scanner.Dispose();
            Database.Dispose();
        }
    }

    private sealed class FakeModelProvider : IAutoTagModelProvider
    {
        public Task EnsureModelAsync(
            string profileId,
            string? modelDirectory,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public string GetAssetPath(
            string profileId,
            AutoTagAssetDefinition asset,
            string? modelDirectory) =>
            Path.Combine(modelDirectory ?? Path.GetTempPath(), profileId, asset.FileName);
    }

    private sealed class FakeTagger : IAutoTagger
    {
        public void LoadModel(string profileId, string? modelDirectory)
        {
        }

        public Task<IReadOnlyList<TagPrediction>> PredictTagsAsync(
            string imagePath,
            string profileId,
            string? modelDirectory,
            int maximumTags,
            float confidenceThreshold,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TagPrediction>>([new TagPrediction("dog", 0.87f)]);
    }

    private sealed class IdleActivityGate : IBackgroundActivityGate
    {
        public Task WaitForIdleAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
