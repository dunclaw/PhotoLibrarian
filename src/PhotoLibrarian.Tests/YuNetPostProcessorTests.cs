using PhotoLibrarian.ML.Services;
using Xunit;

namespace PhotoLibrarian.Tests;

public sealed class YuNetPostProcessorTests
{
    [Fact]
    public void Decode_ProducesNormalizedBoxAndLandmarks()
    {
        var outputs = CreateEmptyOutputs(32, 32);
        const int index = 5;
        outputs["cls_8"][index] = 1;
        outputs["obj_8"][index] = 1;
        outputs["bbox_8"][index * 4] = 0.5f;
        outputs["bbox_8"][index * 4 + 1] = 0.5f;
        outputs["bbox_8"][index * 4 + 2] = MathF.Log(2);
        outputs["bbox_8"][index * 4 + 3] = MathF.Log(2);

        var landmarkOffsets = new float[]
        {
            0, 0,
            1, 0,
            0.5f, 0.5f,
            0, 1,
            1, 1
        };
        Array.Copy(landmarkOffsets, 0, outputs["kps_8"], index * 10, landmarkOffsets.Length);

        var face = Assert.Single(YuNetPostProcessor.Decode(
            outputs,
            32,
            32,
            imageScale: 1,
            originalWidth: 32,
            originalHeight: 32,
            confidenceThreshold: 0.6f,
            nmsThreshold: 0.3f,
            maximumFaces: 100));

        Assert.Equal(0.125f, face.X, 3);
        Assert.Equal(0.125f, face.Y, 3);
        Assert.Equal(0.5f, face.Width, 3);
        Assert.Equal(0.5f, face.Height, 3);
        Assert.Equal(5, face.Landmarks.Count);
        Assert.Equal(new FaceLandmark(0.25f, 0.25f), face.Landmarks[0]);
        Assert.Equal(new FaceLandmark(0.375f, 0.375f), face.Landmarks[2]);
    }

    [Fact]
    public void Decode_SuppressesOverlappingLowerConfidenceFace()
    {
        var outputs = CreateEmptyOutputs(32, 32);
        SetCandidate(outputs, index: 5, classification: 1);
        SetCandidate(outputs, index: 6, classification: 0.81f);

        var faces = YuNetPostProcessor.Decode(
            outputs,
            32,
            32,
            imageScale: 1,
            originalWidth: 32,
            originalHeight: 32,
            confidenceThreshold: 0.6f,
            nmsThreshold: 0.1f,
            maximumFaces: 100);

        Assert.Single(faces);
        Assert.Equal(1, faces[0].Confidence, 3);
    }

    private static Dictionary<string, float[]> CreateEmptyOutputs(int width, int height)
    {
        var outputs = new Dictionary<string, float[]>();
        foreach (var stride in new[] { 8, 16, 32 })
        {
            var cells = width / stride * (height / stride);
            outputs[$"cls_{stride}"] = new float[cells];
            outputs[$"obj_{stride}"] = new float[cells];
            outputs[$"bbox_{stride}"] = new float[cells * 4];
            outputs[$"kps_{stride}"] = new float[cells * 10];
        }

        return outputs;
    }

    private static void SetCandidate(
        IDictionary<string, float[]> outputs,
        int index,
        float classification)
    {
        outputs["cls_8"][index] = classification;
        outputs["obj_8"][index] = 1;
        outputs["bbox_8"][index * 4] = index == 5 ? 0.5f : -0.5f;
        outputs["bbox_8"][index * 4 + 1] = 0.5f;
        outputs["bbox_8"][index * 4 + 2] = MathF.Log(2);
        outputs["bbox_8"][index * 4 + 3] = MathF.Log(2);
    }
}
