using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class AutoTaggingServiceTests
{
    [Fact]
    public void SelectPredictions_NormalizesAndRanksProbabilityOutput()
    {
        var predictions = AutoTaggingService.SelectPredictions(
            [0.1f, 0.65f, 0.25f],
            ["tabby, tabby cat", "Golden_retriever, dog", "park/land"],
            0.2f,
            2);

        Assert.Equal(2, predictions.Count);
        Assert.Equal("golden retriever", predictions[0].Tag);
        Assert.Equal("park land", predictions[1].Tag);
    }

    [Fact]
    public void SelectPredictions_AppliesSoftmaxToLogits()
    {
        var predictions = AutoTaggingService.SelectPredictions(
            [-2f, 3f, 1f],
            ["first", "second", "third"],
            0.5f,
            3);

        var prediction = Assert.Single(predictions);
        Assert.Equal("second", prediction.Tag);
        Assert.InRange(prediction.Confidence, 0.87f, 0.88f);
    }

    [Fact]
    public void SelectPredictions_OnlyEmitsPositiveAllowlistMappings()
    {
        var predictions = AutoTaggingService.SelectPredictions(
            [0.60f, 0.30f, 0.10f],
            ["unreviewed label", "Golden_retriever, dog", "other"],
            0.05f,
            3,
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["golden retriever"] = "dog"
            });

        var prediction = Assert.Single(predictions);
        Assert.Equal("dog", prediction.Tag);
        Assert.Equal(0.30f, prediction.Confidence);
    }

    [Fact]
    public void BinaryTagMasksRejectInactiveClassesAndInvalidOutputs()
    {
        var predictions = AutoTaggingService.SelectPredictions(
            [0, 1, 0], ["abalone", "bird", "monastery"], 0, 10,
            outputKind: AutoTagOutputKind.BinaryTagMask);
        Assert.Equal(new TagPrediction("bird", 1), Assert.Single(predictions));
        Assert.Empty(AutoTaggingService.SelectPredictions(
            [0, 0], ["abacus", "bird"], 0, 10, outputKind: AutoTagOutputKind.BinaryTagMask));
        Assert.Throws<InvalidDataException>(() => AutoTaggingService.SelectPredictions(
            [0.5f], ["bird"], 0, 10, outputKind: AutoTagOutputKind.BinaryTagMask));
        Assert.Throws<InvalidDataException>(() => AutoTaggingService.SelectPredictions(
            [float.NaN], ["bird"], 0, 10, outputKind: AutoTagOutputKind.BinaryTagMask));
        Assert.Throws<InvalidDataException>(() => AutoTaggingService.SelectPredictions(
            [], ["bird"], 0, 10, outputKind: AutoTagOutputKind.BinaryTagMask));
    }

    [Fact]
    public void SelectPredictions_RamPlusUsesDirectMultiLabelScores()
    {
        var predictions = AutoTaggingService.SelectPredictions(
            [0.95f, 0.80f, 0.65f],
            ["flower", "tree", "water"],
            0.70f,
            5,
            AutoTagModelCatalog.RamPlus.SafeLabelMappings,
            AutoTagOutputKind.DirectMultiLabel);

        Assert.Collection(
            predictions,
            prediction =>
            {
                Assert.Equal("flower", prediction.Tag);
                Assert.Equal(0.95f, prediction.Confidence);
            },
            prediction =>
            {
                Assert.Equal("tree", prediction.Tag);
                Assert.Equal(0.80f, prediction.Confidence);
            });
    }

    [Fact]
    public void SelectPredictions_RamPlusUsesGeneralEligibilityRatherThanSampleAllowlist()
    {
        var predictions = AutoTaggingService.SelectPredictions(
            [1.0f, 1.0f, 1.0f, 0.9f],
            ["beak", "claw", "boat", "grass"],
            0.40f,
            10,
            AutoTagModelCatalog.RamPlus.SafeLabelMappings,
            AutoTagOutputKind.DirectMultiLabel);

        Assert.Equal(["boat", "grass"], predictions.Select(prediction => prediction.Tag));
    }

    [Fact]
    public async Task ReadLabels_ParsesRamPlusDictionaryFormat()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"ram-labels-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(
                path,
                "{'0': 'flower', '1': 'tree branch', '2': 'child\\'s toy', '3': 'farmer's market'}",
                TestContext.Current.CancellationToken);

            var labels = AutoTaggingService.ReadLabels(path);

            Assert.Equal(
                ["flower", "tree branch", "child's toy", "farmer's market"],
                labels);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RamPlusProfile_UsesVerifiedLocalMultiLabelAssets()
    {
        var profile = AutoTagModelCatalog.RamPlus;

        Assert.True(profile.RequiresLocalImport);
        Assert.Equal(384, profile.InputSize);
        Assert.Equal(AutoTagTensorLayout.Nchw, profile.TensorLayout);
        Assert.Equal(AutoTagPixelNormalization.Unit, profile.PixelNormalization);
        Assert.True(profile.PreserveAspectRatio);
        Assert.Equal(
            AutoTagOutputKind.BinaryTagMask,
            profile.OutputKind);
        Assert.Equal("targets", profile.OutputName);
        Assert.True(profile.UsesFixedThresholds);
        Assert.Equal(10, profile.MaximumSupportedTags);
        Assert.Null(profile.ModelAsset.DownloadUri);
        Assert.Null(profile.LabelsAsset.DownloadUri);
    }
}
