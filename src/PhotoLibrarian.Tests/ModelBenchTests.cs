using PhotoLibrarian.ModelBench;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ModelBenchTests
{
    [Fact]
    public void ParseOptionsAcceptsComparisonControls()
    {
        var options = BenchmarkOptions.Parse(
        [
            "--images",
            "photos",
            "--models-dir",
            "models",
            "--output",
            "results",
            "--models",
            "joytag,ram-plus",
            "--provider",
            "directml",
            "--labels",
            "mapped",
            "--top",
            "12",
            "--threshold",
            "0.35",
            "--limit",
            "25",
            "--no-recursive",
            "--no-warmup"
        ]);

        Assert.Equal(ExecutionProvider.DirectML, options.Provider);
        Assert.Equal(LabelMode.Mapped, options.LabelMode);
        Assert.Equal(12, options.MaximumPredictions);
        Assert.Equal(0.35f, options.ConfidenceThreshold);
        Assert.Equal(25, options.ImageLimit);
        Assert.False(options.Recursive);
        Assert.False(options.WarmUp);
        Assert.Equal(
            ["joytag", "ram-plus"],
            options.ModelIds.OrderBy(id => id));
    }

    [Fact]
    public void ParseLabelsPreservesIndexesFromPythonDictionary()
    {
        var labels = LabelResolver.Parse(
            "{'0': 'first_label', '2': 'girl\\'s hat'}");

        Assert.Equal(3, labels.Count);
        Assert.Equal("first label", labels[0]);
        Assert.Equal(string.Empty, labels[1]);
        Assert.Equal("girl's hat", labels[2]);
    }

    [Fact]
    public void FullVocabularyLabelsPreserveUnescapedApostrophes()
    {
        var labels = LabelResolver.Parse(
            "{'0': 'farmer's market', '1': 'artist\\'s palette', '2': 'bird'}");
        Assert.Equal(["farmer's market", "artist's palette", "bird"], labels);
    }

    [Fact]
    public void MappedVocabularyFiltersExcludedLabelsAndDeduplicatesBeforeRanking()
    {
        var predictions = OnnxModelRunner.SelectLabelScores(
            [0.99, 0.9, 0.8, 0.2], ["", "flower", "flower", "bird"],
            0.4f, 2, includeBelowThreshold: true, deduplicate: true);
        Assert.Equal(["flower", "bird"], predictions.Select(prediction => prediction.Label));
        Assert.Equal([1, 2], predictions.Select(prediction => prediction.Rank));
        Assert.Equal(0.9f, predictions[0].Confidence);
        Assert.False(predictions[1].AboveThreshold);

        var raw = OnnxModelRunner.SelectLabelScores(
            [0.9, 0.8, 0.2], ["flower", "flower", "bird"],
            0.4f, 2, includeBelowThreshold: true, deduplicate: false);
        Assert.Equal(["flower", "flower"], raw.Select(prediction => prediction.Label));
    }

    [Fact]
    public void BinaryMasksNeverPadWithInactiveLabelsEvenAtZeroThreshold()
    {
        var predictions = OnnxModelRunner.SelectLabelScores(
            [0, 0, 1, 0, 1], ["3d glasses", "abacus", "bird", "abalone", "tree"],
            0, 10, includeBelowThreshold: true, deduplicate: true, binaryMask: true);
        Assert.Equal(["bird", "tree"], predictions.Select(prediction => prediction.Label));
        Assert.All(predictions, prediction => Assert.Equal(1f, prediction.Confidence));
        Assert.Empty(OnnxModelRunner.SelectLabelScores(
            [0, 0], ["abacus", "monastery"], 0, 10, true, true, binaryMask: true));
        Assert.Throws<InvalidDataException>(() => OnnxModelRunner.SelectLabelScores(
            [0.5], ["bird"], 0, 10, true, true, binaryMask: true));
        Assert.Throws<InvalidDataException>(() => OnnxModelRunner.SelectLabelScores(
            [float.NaN], ["bird"], 0, 10, true, true, binaryMask: true));
        Assert.Throws<InvalidDataException>(() => OnnxModelRunner.SelectLabelScores(
            [1], ["bird", "tree"], 0, 10, true, true, binaryMask: true));
        var ram = ModelCatalog.All.Single(model => model.Id == "ram-plus");
        Assert.True(ram.BinaryTagMask);
        Assert.Equal("targets", ram.OutputName);
    }

    [Fact]
    public void ScorecardContainsEditableRatingColumnsAndEscapesValues()
    {
        var result = new ImageModelResult(
            @"C:\photos\sample.jpg",
            "sample.jpg",
            "joytag",
            "JoyTag",
            ModelTask.MultiLabelTagging,
            "Cpu",
            "labels.txt",
            1.25,
            2.5,
            [new PredictionResult(1, "quoted \"label\"", 0.75f)]);
        var run = new BenchmarkRun(
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            @"C:\photos",
            @"C:\models",
            @"C:\output",
            LabelMode.Raw,
            [result.ImagePath],
            [],
            [result]);

        var csv = BenchmarkReportWriter.CreateScorecardCsv(run);

        Assert.Contains("\"QualityScore0To5\",\"Notes\"", csv);
        Assert.Contains("\"quoted \"\"label\"\" (75.0%)\"", csv);
    }

    [Fact]
    public void CatalogContainsEveryDiscussedModel()
    {
        var ids = ModelCatalog.All.Select(model => model.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Contains("efficientnet", ids);
        Assert.Contains("yolov8-cls", ids);
        Assert.Contains("yolov8-detect", ids);
        Assert.Contains("yolov10-detect", ids);
        Assert.Contains("joytag", ids);
        Assert.Contains("ram-plus", ids);
        Assert.Contains("siglip2", ids);
        Assert.Contains("tinyclip", ids);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-0.1")]
    public void InvalidThresholdIsRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => BenchmarkOptions.Parse(
            ["--images", "photos", "--models-dir", "models", "--threshold", value]));
    }
}
