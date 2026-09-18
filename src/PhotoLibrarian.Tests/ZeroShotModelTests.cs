using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PhotoLibrarian.ModelBench;
using PhotoLibrarian.Inference;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class ZeroShotModelTests
{
    [Fact(Explicit = true)]
    public void PreparedAssetsMatchPythonReference()
    {
        var directory = Environment.GetEnvironmentVariable("MODELBENCH_ASSETS")
            ?? throw new InvalidOperationException("Set MODELBENCH_ASSETS to the prepared bundle directory.");
        using var reference = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory, "reference.json")));
        foreach (var fixture in reference.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            foreach (var model in fixture.GetProperty("models").EnumerateArray())
            {
                var id = model.GetProperty("modelId").GetString()!;
                var definition = ModelCatalog.All.Single(item => item.Id == id);
                var bundle = ZeroShotModelBundle.Load(Path.Combine(directory, id + ".json"), id);
                var expectedPixels = new float[3 * 224 * 224];
                var bytes = File.ReadAllBytes(Path.Combine(directory,
                    model.GetProperty("pixelValuesFile").GetString()!));
                Buffer.BlockCopy(bytes, 0, expectedPixels, 0, bytes.Length);
                var prepared = ImageTensorBuilder.Create(Path.Combine(directory,
                    fixture.GetProperty("imageFile").GetString()!), 224, 224, definition, bundle);
                var actualPixels = prepared.Tensor.ToArray();
                Assert.True(actualPixels.Zip(expectedPixels)
                    .Max(pair => Math.Abs(pair.First - pair.Second)) < 0.000002f,
                    $"{id}: preprocessing differs from the reference.");

                using var options = new SessionOptions();
                using var session = new InferenceSession(Path.Combine(directory, bundle.ModelFile), options);
                using var output = session.Run(
                    [NamedOnnxValue.CreateFromTensor(bundle.InputName,
                        new DenseTensor<float>(expectedPixels, [1, 3, 224, 224]))]);
                var embedding = output.Single(item => item.Name == bundle.OutputName)
                    .AsTensor<float>().ToArray();
                var scores = bundle.Predict(embedding, bundle.Labels.Length, 0)
                    .ToDictionary(item => item.Label, item => item.Confidence);
                var expectedScores = model.GetProperty("scores").EnumerateArray()
                    .Select(item => item.GetSingle()).ToArray();
                for (var index = 0; index < expectedScores.Length; index++)
                {
                    Assert.InRange(Math.Abs(scores[bundle.Labels[index].Label] - expectedScores[index]), 0, 0.0001f);
                }
            }
        }
    }

    [Fact]
    public void SigmoidScoresAreIndependentAndUseLearnedScaleAndBias()
    {
        var bundle = CreateBundle("sigmoid");
        bundle.Validate("siglip2");

        var predictions = bundle.Predict([3, 0], 10, 0.5f);

        Assert.Equal(3, predictions.Count);
        Assert.Equal("first", predictions[0].Label);
        Assert.Equal("similar", predictions[1].Label);
        Assert.InRange(predictions[0].Confidence, 0.73f, 0.732f);
        Assert.Equal(predictions[0].Confidence, predictions[1].Confidence);
        Assert.True(predictions[1].AboveThreshold);
        Assert.False(predictions[2].AboveThreshold);
        Assert.InRange(predictions[2].Confidence, 0.268f, 0.27f);
    }

    [Fact]
    public void CosineDoesNotApplySoftmaxAndPreservesBelowThresholdCandidates()
    {
        var bundle = CreateBundle("cosine");
        var predictions = bundle.Predict([0, 5], 2, 0.25f);

        Assert.Equal(2, predictions.Count);
        Assert.Equal("other", predictions[0].Label);
        Assert.Equal(1f, predictions[0].Confidence);
        Assert.Equal(0f, predictions[1].Confidence);
        Assert.False(predictions[1].AboveThreshold);
    }

    [Fact]
    public void InvalidEmbeddingsFailExplicitly()
    {
        var bundle = CreateBundle();
        Assert.Throws<InvalidDataException>(() => bundle.Predict([1], 10, 0.1f));
        Assert.Throws<InvalidDataException>(() => bundle.Predict([0, 0], 10, 0.1f));
        Assert.Throws<InvalidDataException>(() => bundle.Predict([float.NaN, 0], 10, 0.1f));
        bundle.Labels[0].Embedding[0] = float.NaN;
        Assert.Throws<InvalidDataException>(() => bundle.Validate("siglip2"));
    }

    [Fact]
    public async Task BundleLoadingVerifiesModelHash()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ZeroShot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var modelPath = Path.Combine(directory, "vision.onnx");
            byte[] contents = [1, 2, 3];
            await File.WriteAllBytesAsync(modelPath, contents, TestContext.Current.CancellationToken);
            var bundle = CreateBundle(hash: Convert.ToHexString(SHA256.HashData(contents)));
            var path = Path.Combine(directory, "siglip2.json");
            await File.WriteAllTextAsync(path,
                JsonSerializer.Serialize(bundle, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                TestContext.Current.CancellationToken);

            Assert.Equal(3, ZeroShotModelBundle.Load(path, "siglip2").Labels.Length);
            Assert.Throws<InvalidDataException>(() => ZeroShotModelBundle.Load(path, "tinyclip"));
            await File.WriteAllBytesAsync(modelPath, [4, 5, 6], TestContext.Current.CancellationToken);
            Assert.Throws<InvalidDataException>(() => ZeroShotModelBundle.Load(path, "siglip2"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("..\\outside.onnx")]
    [InlineData("C:\\outside.onnx")]
    [InlineData("file.onnx:stream")]
    public void BundleRejectsModelPathsOutsideAssetDirectory(string modelFile)
    {
        var bundle = CreateBundle(modelFile: modelFile);
        Assert.Throws<InvalidDataException>(() => bundle.Validate("siglip2"));
    }

    [Theory]
    [InlineData("squash", -1f, 1f)]
    [InlineData("shortest-center-crop", 1f, -1f)]
    public void PreprocessingUsesBundleResizeAndNormalization(
        string resizeMode, float expectedRed, float expectedGreen)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ZeroShot-{Guid.NewGuid():N}.png");
        try
        {
            using (var image = new Image<Rgb24>(32, 16, new Rgb24(255, 0, 0)))
            {
                for (var y = 0; y < 16; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        image[x, y] = new Rgb24(0, 255, 0);
                    }
                }
                image.SaveAsPng(path);
            }
            var bundle = CreateBundle(resizeMode: resizeMode);
            var model = ModelCatalog.All.Single(model => model.Id == "siglip2");
            var input = ImageTensorBuilder.Create(path, 16, 16, model, bundle);

            Assert.Equal([1, 3, 16, 16], input.Tensor.Dimensions.ToArray());
            Assert.Equal(expectedRed, input.Tensor[0, 0, 8, 0], 3);
            Assert.Equal(expectedGreen, input.Tensor[0, 1, 8, 0], 3);
            Assert.Equal(-1f, input.Tensor[0, 2, 8, 0], 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReportDescribesCosineWithoutConfidencePercent()
    {
        var result = new ImageModelResult(
            @"C:\test.png", "test.png", "tinyclip", "TinyCLIP",
            ModelTask.ZeroShotTagging, "Cpu", "vocabulary", 1, 2,
            [new PredictionResult(1, "flower", 0.25f)],
            ScoreKind: "cosine", Threshold: 0.25f);
        var run = new BenchmarkRun(
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            @"C:\", @"C:\models", @"C:\output", LabelMode.Raw,
            [result.ImagePath], [], [result]);

        var csv = BenchmarkReportWriter.CreateScorecardCsv(run);
        Assert.Contains("flower (0.250 cosine)", csv);
        Assert.DoesNotContain("25.0%", csv);
        Assert.Contains("\"ScoreKind\",\"Threshold\"", csv);
    }

    private static ZeroShotModelBundle CreateBundle(
        string scoreKind = "sigmoid",
        string hash = "",
        string modelFile = "vision.onnx",
        string resizeMode = "squash") => new()
    {
        ModelId = "siglip2",
        ModelFile = modelFile,
        ModelSha256 = hash,
        InputName = "pixel_values",
        OutputName = "image_embeds",
        InputSize = 16,
        ResizeMode = resizeMode,
        Interpolation = "linear",
        NormalizationMean = [0.5f, 0.5f, 0.5f],
        NormalizationStd = [0.5f, 0.5f, 0.5f],
        ScoreKind = scoreKind,
        LogitScale = 2,
        LogitBias = -1,
        Labels =
        [
            new("first", [1, 0]),
            new("similar", [2, 0]),
            new("other", [0, 1])
        ]
    };
}
