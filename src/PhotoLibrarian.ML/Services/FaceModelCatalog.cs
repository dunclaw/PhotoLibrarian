using System.Security.Cryptography;

namespace PhotoLibrarian.ML.Services;

public static class FaceModelCatalog
{
    private const string OpenCvZooCommit = "47534e27c9851bb1128ccc0102f1145e27f23f98";
    private const string OpenCvZooMediaRoot =
        $"https://media.githubusercontent.com/media/opencv/opencv_zoo/{OpenCvZooCommit}/models";

    public const string PipelineVersion = "yunet-2026may+sface-2021dec-v1";

    public static FaceModelDefinition Detector { get; } = new(
        "face_detection_yunet_2026may.onnx",
        new Uri($"{OpenCvZooMediaRoot}/face_detection_yunet/face_detection_yunet_2026may.onnx"),
        "ebafce4e3c118d6554634be5c27ab333b4c047a9a8c3faf1d7cf93101c22f0f0");

    public static FaceModelDefinition Recognizer { get; } = new(
        "face_recognition_sface_2021dec.onnx",
        new Uri($"{OpenCvZooMediaRoot}/face_recognition_sface/face_recognition_sface_2021dec.onnx"),
        "0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79");

    public static IReadOnlyList<FaceModelDefinition> All { get; } = [Detector, Recognizer];
}

public sealed record FaceModelDefinition(string FileName, Uri DownloadUri, string Sha256)
{
    public bool HasExpectedHash(Stream stream)
    {
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return hash.Equals(Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
