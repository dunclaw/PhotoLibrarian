using System.Text.Json;
using PhotoLibrarian.Inference;
using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class TinyClipIntegrationTests
{
    [Fact]
    public void CatalogPreservesRamDefaultAndRequiresIndependentApproval()
    {
        var profile = AutoTagModelCatalog.TinyClip;
        Assert.Contains(profile, AutoTagModelCatalog.Profiles);
        Assert.True(profile.RequiresLocalImport);
        Assert.True(profile.UsesCalibratedThresholds);
        Assert.False(profile.UsesFixedThresholds);
        Assert.True(profile.UseCpuOnly);
        Assert.Equal(AutoTagOutputKind.CosineEmbedding, profile.OutputKind);
        Assert.Equal(3632, profile.SafeLabelMappings.Count);
        Assert.DoesNotContain("beak", profile.SafeLabelMappings.Keys);
        Assert.Contains("bird", profile.SafeLabelMappings.Keys);
        Assert.Equal(98_920_582, profile.DownloadSizeBytes);
        Assert.Equal(AutoTagModelCatalog.RamPlusProfileId, new AutoTaggingSettings().ProfileId);
        var settings = new AutoTaggingSettings(true, profile.Id,
            ProfileQualityStatuses: new()
            {
                [AutoTagModelCatalog.RamPlus.ApprovalKey] = AutoTagQualityStatus.Approved,
                [$"{profile.Id}|old"] = AutoTagQualityStatus.Approved
            });
        Assert.False(settings.CanRun);
        settings.ProfileQualityStatuses![profile.ApprovalKey] = AutoTagQualityStatus.Approved;
        Assert.True(settings.CanRun);
        Assert.False((settings with { IsEnabled = false }).CanRun);
    }

    [Fact]
    public void CalibratedCutoffsAreInclusiveAndAbstentionsDoNotFallBack()
    {
        Assert.Equal(0.3033498f, TinyClipCalibration.Current.FallbackThreshold);
        Assert.Equal(0.22960028f, TinyClipCalibration.Current.Thresholds["wildlife"]);
        var calibration = new TinyClipCalibration
        {
            FallbackThreshold = 0.21654627f,
            Labels = ["bird", "cat", "branch", "lake"],
            Thresholds = new() { ["bird"] = 0.21320139f, ["branch"] = null, ["lake"] = null }
        };
        Assert.True(calibration.IsAccepted("bird", 0.21320139f));
        Assert.False(calibration.IsAccepted("bird", MathF.BitDecrement(0.21320139f)));
        Assert.True(calibration.IsAccepted("cat", calibration.FallbackThreshold));
        Assert.False(calibration.IsAccepted("cat", MathF.BitDecrement(calibration.FallbackThreshold)));
        Assert.False(calibration.IsAccepted("branch", 1f));
        Assert.False(calibration.IsAccepted("lake", 1f));
        Assert.False(calibration.IsAccepted("beak", 1f));
        Assert.False(calibration.IsAccepted("unreviewed label", 1f));
        Assert.False(calibration.IsAccepted("bird", float.NaN));
    }

    [Fact]
    public void BroaderCoverageAcceptsBorderlineTagsButKeepsCutoffBoundaries()
    {
        var calibration = TinyClipCalibration.Current;
        Assert.True(calibration.IsAccepted("bird", 0.31f));
        Assert.True(calibration.IsAccepted("bird", 0.3033498f));
        Assert.False(calibration.IsAccepted("bird", MathF.BitDecrement(0.3033498f)));
        Assert.False(calibration.IsAccepted("bird", 0.30f));
        Assert.True(calibration.IsAccepted("wildlife", 0.22960028f));
        Assert.False(calibration.IsAccepted("sand castle", 0.24188669f));
        Assert.True(calibration.IsAccepted("bird", 0.30f, 0.29f));
        Assert.False(calibration.IsAccepted("bird", 0.30f, 0.31f));
        Assert.True(calibration.IsAccepted("wildlife", 0.22f, 0.2933498f));
        Assert.False(calibration.IsAccepted("wildlife", 0.22f, 0.3033498f));
        Assert.False(calibration.IsAccepted("bird", 1f, float.NaN));
    }

    [Fact]
    public void BroaderCoverageRequiresNewTinyClipApprovalWithoutChangingRamApproval()
    {
        const string previousTinyClipKey =
            "tinyclip.vit-40m-32.int8|tinyclip-40m-int8-cpu-general-v2-top10-20260916-hierarchy-v3";
        const string ramKey =
            "ram-plus.swin-large-14m|ram-plus-swin-large-14m-general-v2-binary-mask-hierarchy-v3";
        Assert.Equal(ramKey, AutoTagModelCatalog.RamPlus.ApprovalKey);
        var settings = new AutoTaggingSettings(true, AutoTagModelCatalog.TinyClipProfileId,
            ProfileQualityStatuses: new()
            {
                [previousTinyClipKey] = AutoTagQualityStatus.Approved,
                [ramKey] = AutoTagQualityStatus.Approved
            });
        Assert.False(settings.CanRun);
        Assert.True((settings with { ProfileId = AutoTagModelCatalog.RamPlusProfileId }).CanRun);
    }

    [Fact]
    public void SelectionUsesIndependentCosineAndOnlyTheCalibratedTopTen()
    {
        var calibration = TinyClipCalibration.Current;
        var labels = new[] { "beak", "unreviewed 1", "unreviewed 2", "unreviewed 3", "unreviewed 4",
            "unreviewed 5", "unreviewed 6", "unreviewed 7", "unreviewed 8", "unreviewed 9", "bird" };
        var bundle = new ZeroShotModelBundle
        {
            ScoreKind = "cosine",
            Labels = labels.Select((label, index) =>
                new ZeroShotLabel(label, index == 10 ? [0.8f, 0.6f] : [1, 0])).ToArray()
        };
        Assert.Empty(calibration.SelectPredictions(bundle, [1, 0], 10));
        var useful = new ZeroShotModelBundle
        {
            ScoreKind = "cosine",
            Labels = [new("bird", [1, 0]), new("flower", [0.9f, 0.1f]), new("beak", [1, 0])]
        };
        Assert.Equal(["bird", "flower"],
            calibration.SelectPredictions(useful, [1, 0], 10).Select(p => p.Tag));
        Assert.Single(calibration.SelectPredictions(useful, [1, 0], 1));
        Assert.Throws<InvalidDataException>(() => calibration.SelectPredictions(useful, [0, 0], 10));
    }

    [Fact]
    public void SettingsPersistFixedPolicyWithoutChangingEnablementOrStorage()
    {
        var file = Path.Combine(Path.GetTempPath(), $"tinyclip-{Guid.NewGuid():N}.json");
        try
        {
            var store = new AutoTaggingSettingsStore(file);
            store.Save(new AutoTaggingSettings(false, AutoTagModelCatalog.TinyClipProfileId,
                MaximumTags: 500, ConfidenceThreshold: 0.99f, ModelDirectory: @"C:\Models\TinyClip"));
            var saved = store.Load();
            Assert.False(saved.CanRun);
            Assert.False(saved.IsEnabled);
            Assert.Equal(10, saved.MaximumTags);
            Assert.Equal(AutoTagModelCatalog.TinyClipConservativeThreshold,
                saved.ConfidenceThreshold);
            Assert.Equal(@"C:\Models\TinyClip", saved.ModelDirectory);
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".tmp");
        }
    }

    [Fact(Explicit = true)]
    public async Task PreparedProfileImportsPredictsAndUnloadsWithoutTouchingSource()
    {
        var assets = Environment.GetEnvironmentVariable("MODELBENCH_ASSETS")
            ?? throw new InvalidOperationException("Set MODELBENCH_ASSETS to the prepared general-vocabulary assets.");
        var root = Path.Combine(Path.GetTempPath(), $"tinyclip-import-{Guid.NewGuid():N}");
        var definition = AutoTagModelCatalog.TinyClip;
        try
        {
            using var sessions = new OnnxSessionManager(root);
            var manager = new AutoTagModelManager(sessions);
            var tagger = new AutoTaggingService(sessions, manager);
            await manager.ImportProfileAssetsAsync(definition.Id, assets,
                cancellationToken: TestContext.Current.CancellationToken);
            await manager.EnsureModelAsync(definition.Id, null, TestContext.Current.CancellationToken);
            Assert.Equal(AutoTagDownloadState.Downloaded, manager.GetDownloadStatus(definition.Id, null).State);
            tagger.LoadModel(definition.Id, null);
            var modelPath = manager.GetAssetPath(definition.Id, definition.ModelAsset, null);
            var cpuSession = sessions.LoadModelPath(modelPath, useCpuOnly: true);
            Assert.Same(cpuSession, sessions.LoadModelPath(modelPath, useCpuOnly: true));
            using var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, "reference.json")));
            foreach (var fixture in reference.RootElement.GetProperty("fixtures").EnumerateArray())
            {
                var expected = fixture.GetProperty("models").EnumerateArray()
                    .Single(item => item.GetProperty("modelId").GetString() == "tinyclip")
                    .GetProperty("scores").EnumerateArray().Select(item => item.GetSingle()).ToArray();
                var predictions = await tagger.PredictTagsAsync(
                    Path.Combine(assets, fixture.GetProperty("imageFile").GetString()!),
                    definition.Id, null, 8, definition.DefaultConfidenceThreshold,
                    TestContext.Current.CancellationToken);
                AssertMatchesReviewedScores(expected, predictions, 8);
            }
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                tagger.PredictTagsAsync("unused.jpg", definition.Id, null, 8, 0f, canceled.Token));
            await manager.DeleteProfileAssetsAsync(definition.Id, null);
            Assert.Equal(AutoTagDownloadState.NotDownloaded, manager.GetDownloadStatus(definition.Id, null).State);
            Assert.True(File.Exists(Path.Combine(assets, definition.ModelAsset.FileName)));
            Assert.True(File.Exists(Path.Combine(assets, definition.LabelsAsset.FileName)));
            await manager.ImportProfileAssetsAsync(definition.Id, assets,
                cancellationToken: TestContext.Current.CancellationToken);
            tagger.LoadModel(definition.Id, null);
            Assert.NotSame(cpuSession, sessions.LoadModelPath(modelPath, useCpuOnly: true));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Explicit = true)]
    public async Task ProductionPredictionsMatchSavedGeneralVocabularyScores()
    {
        var assets = Environment.GetEnvironmentVariable("MODELBENCH_ASSETS")
            ?? throw new InvalidOperationException("Set MODELBENCH_ASSETS to general-vocabulary assets.");
        var resultsPath = Environment.GetEnvironmentVariable("TINYCLIP_REVIEW_RESULTS")
            ?? throw new InvalidOperationException("Set TINYCLIP_REVIEW_RESULTS to the general-vocabulary results.json.");
        var root = Path.Combine(Path.GetTempPath(), $"tinyclip-parity-{Guid.NewGuid():N}");
        try
        {
            using var sessions = new OnnxSessionManager(root);
            var tagger = new AutoTaggingService(sessions, new PreparedProvider(assets));
            tagger.LoadModel(AutoTagModelCatalog.TinyClipProfileId, null);
            using var document = JsonDocument.Parse(File.ReadAllText(resultsPath));
            var results = document.RootElement.GetProperty("Results").EnumerateArray()
                .Where(item => item.GetProperty("ModelId").GetString() == "tinyclip").ToArray();
            Assert.NotEmpty(results);
            foreach (var item in results)
            {
                var predictions = await tagger.PredictTagsAsync(
                    item.GetProperty("ImagePath").GetString()!, AutoTagModelCatalog.TinyClipProfileId,
                    null, 8, AutoTagModelCatalog.TinyClip.DefaultConfidenceThreshold,
                    TestContext.Current.CancellationToken);
                var expectedRaw = item.GetProperty("Predictions").EnumerateArray()
                    .Select(p => new TagPrediction(p.GetProperty("Label").GetString()!,
                        p.GetProperty("Confidence").GetSingle()))
                    .Where(p => TinyClipCalibration.Current.IsAccepted(p.Tag, p.Confidence)).Take(8).ToArray();
                var expected = AutoTagHierarchy.Apply(AutoTagModelCatalog.TinyClip, expectedRaw);
                Assert.Equal(expected.Select(p => p.Tag), predictions.Select(p => p.Tag));
                foreach (var pair in expected.Zip(predictions))
                    Assert.InRange(Math.Abs(pair.First.Confidence - pair.Second.Confidence), 0, 0.00001f);
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertMatchesReviewedScores(float[] scores, IReadOnlyList<TagPrediction> actual, int maximum)
    {
        var calibration = TinyClipCalibration.Current;
        Assert.Equal(calibration.Labels.Length, scores.Length);
        var expectedRaw = scores.Select((score, index) => new TagPrediction(calibration.Labels[index], score))
            .OrderByDescending(p => p.Confidence).Take(10)
            .Where(p => calibration.IsAccepted(p.Tag, p.Confidence)).Take(maximum).ToArray();
        var expected = AutoTagHierarchy.Apply(AutoTagModelCatalog.TinyClip, expectedRaw);
        Assert.Equal(expected.Select(p => p.Tag), actual.Select(p => p.Tag));
        foreach (var pair in expected.Zip(actual))
            Assert.InRange(Math.Abs(pair.First.Confidence - pair.Second.Confidence), 0, 0.0001f);
    }

    private sealed class PreparedProvider(string directory) : IAutoTagModelProvider
    {
        public string GetAssetPath(string profileId, AutoTagAssetDefinition asset, string? modelDirectory) =>
            Path.Combine(directory, asset.FileName);
        public Task EnsureModelAsync(string profileId, string? modelDirectory, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
