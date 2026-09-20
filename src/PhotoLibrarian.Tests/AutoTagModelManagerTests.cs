using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class AutoTagModelManagerTests
{
    [Fact]
    public async Task CopyVerifiedAssetAsync_CopiesWithoutChangingSource()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.onnx");
            var destination = Path.Combine(root, "managed", "model.onnx");
            var content = "verified model content";
            await File.WriteAllTextAsync(
                source,
                content,
                TestContext.Current.CancellationToken);
            var asset = new AutoTagAssetDefinition(
                "model.onnx",
                null,
                Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(content))),
                content.Length);

            await AutoTagModelManager.CopyVerifiedAssetAsync(
                asset,
                source,
                destination,
                TestContext.Current.CancellationToken);

            Assert.Equal(content, await File.ReadAllTextAsync(
                source,
                TestContext.Current.CancellationToken));
            Assert.Equal(content, await File.ReadAllTextAsync(
                destination,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CopyVerifiedAssetAsync_HashMismatchRemovesOnlyCopy()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.onnx");
            var destination = Path.Combine(root, "managed", "model.onnx");
            await File.WriteAllTextAsync(
                source,
                "unexpected",
                TestContext.Current.CancellationToken);
            var asset = new AutoTagAssetDefinition(
                "model.onnx",
                null,
                new string('0', 64),
                10);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                AutoTagModelManager.CopyVerifiedAssetAsync(
                    asset,
                    source,
                    destination,
                    TestContext.Current.CancellationToken));

            Assert.True(File.Exists(source));
            Assert.False(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CustomLocationAndDeletionOnlyTouchCatalogAssets()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"PhotoLibrarian-{Guid.NewGuid():N}");
        var defaultDirectory = Path.Combine(root, "default");
        var customDirectory = Path.Combine(root, "custom");
        try
        {
            using var sessionManager =
                new OnnxSessionManager(defaultDirectory);
            var manager = new AutoTagModelManager(
                sessionManager,
                defaultDirectory);
            var previousFile = Path.Combine(
                defaultDirectory,
                "previous-download.onnx");
            await File.WriteAllTextAsync(
                previousFile,
                "keep",
                TestContext.Current.CancellationToken);
            var prepared =
                manager.PrepareCustomDirectory(customDirectory);
            Assert.Equal(
                Path.GetFullPath(customDirectory),
                prepared);
            Assert.Equal(
                Path.GetFullPath(defaultDirectory),
                manager.ResolveRootDirectory(null));
            Assert.True(File.Exists(previousFile));

            var selected = AutoTagModelCatalog.MobileNetV2;
            var other = AutoTagModelCatalog.EfficientNetLite4;
            var selectedAsset = manager.GetAssetPath(
                selected.Id,
                selected.ModelAsset,
                customDirectory);
            var otherAsset = manager.GetAssetPath(
                other.Id,
                other.ModelAsset,
                customDirectory);
            Directory.CreateDirectory(
                Path.GetDirectoryName(selectedAsset)!);
            Directory.CreateDirectory(
                Path.GetDirectoryName(otherAsset)!);
            await File.WriteAllTextAsync(
                selectedAsset,
                "selected",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                otherAsset,
                "other",
                TestContext.Current.CancellationToken);
            var unrelated = Path.Combine(
                customDirectory,
                "keep.txt");
            await File.WriteAllTextAsync(
                unrelated,
                "keep",
                TestContext.Current.CancellationToken);

            await manager.DeleteProfileAssetsAsync(
                selected.Id,
                customDirectory);

            Assert.False(File.Exists(selectedAsset));
            Assert.True(File.Exists(otherAsset));
            Assert.True(File.Exists(unrelated));

            await manager.DeleteAllProfileAssetsAsync(
                customDirectory);

            Assert.False(File.Exists(otherAsset));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact(Explicit = true)]
    public async Task OfficialRamPlusExportImportsLoadsAndTagsRepresentativeImage()
    {
        var assets = Environment.GetEnvironmentVariable("RAM_PLUS_ASSETS")
            ?? throw new InvalidOperationException(
                "Set RAM_PLUS_ASSETS to the generated official RAM++ bundle.");
        var image = Environment.GetEnvironmentVariable("RAM_PLUS_VALIDATION_IMAGE")
            ?? throw new InvalidOperationException(
                "Set RAM_PLUS_VALIDATION_IMAGE to a representative image.");
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ram-plus-import-{Guid.NewGuid():N}");
        try
        {
            using var sessions = new OnnxSessionManager(root);
            var manager = new AutoTagModelManager(sessions);
            var tagger = new AutoTaggingService(sessions, manager);
            var profile = AutoTagModelCatalog.RamPlus;

            await manager.ImportProfileAssetsAsync(
                profile.Id,
                assets,
                cancellationToken: TestContext.Current.CancellationToken);
            await manager.EnsureModelAsync(
                profile.Id,
                null,
                TestContext.Current.CancellationToken);
            tagger.LoadModel(profile.Id, null);

            var predictions = await tagger.PredictTagsAsync(
                image,
                profile.Id,
                null,
                profile.DefaultMaximumTags,
                profile.DefaultConfidenceThreshold,
                TestContext.Current.CancellationToken);

            Assert.NotEmpty(predictions);
            Assert.All(predictions, prediction =>
            {
                Assert.NotEqual(string.Empty, prediction.Tag);
                Assert.Equal(1, prediction.Confidence);
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
