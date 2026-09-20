namespace PhotoLibrarian.ModelBench;

public enum ModelTask
{
    Classification,
    MultiLabelTagging,
    ObjectDetectionV8,
    ObjectDetectionV10,
    ZeroShotTagging
}

public enum TensorLayout
{
    Nchw,
    Nhwc
}

public enum PixelNormalization
{
    Unit,
    MinusOneToOne,
    ImageNet,
    EfficientNetLite
}

public sealed record ModelDefinition(
    string Id,
    string DisplayName,
    string ModelFileName,
    ModelTask Task,
    TensorLayout Layout,
    PixelNormalization Normalization,
    int FallbackInputSize,
    bool PreserveAspectRatio,
    float DefaultThreshold,
    string? LabelsFileName = null,
    string? MappingFileName = null,
    bool BinaryTagMask = false,
    string? OutputName = null);

public static class ModelCatalog
{
    public static IReadOnlyList<ModelDefinition> All { get; } =
    [
        new(
            "efficientnet",
            "EfficientNet-Lite4",
            "efficientnet-lite4-11.onnx",
            ModelTask.Classification,
            TensorLayout.Nhwc,
            PixelNormalization.EfficientNetLite,
            224,
            false,
            0,
            "efficientnet-lite4-11_labels.txt",
            "efficientnet-lite4-11_labels_Mapping.txt"),
        new(
            "yolov8-cls",
            "YOLOv8x classification",
            "yolov8x-cls.onnx",
            ModelTask.Classification,
            TensorLayout.Nchw,
            PixelNormalization.Unit,
            224,
            true,
            0,
            null,
            "yolov8x-cls_labels_Mapping.txt"),
        new(
            "yolov8-detect",
            "YOLOv8x object detection",
            "yolov8x.onnx",
            ModelTask.ObjectDetectionV8,
            TensorLayout.Nchw,
            PixelNormalization.Unit,
            640,
            true,
            0.23f),
        new(
            "yolov10-detect",
            "YOLOv10x object detection",
            "yolov10x.onnx",
            ModelTask.ObjectDetectionV10,
            TensorLayout.Nchw,
            PixelNormalization.Unit,
            640,
            true,
            0.23f),
        new(
            "joytag",
            "JoyTag",
            "joytagmodel.onnx",
            ModelTask.MultiLabelTagging,
            TensorLayout.Nchw,
            PixelNormalization.Unit,
            448,
            true,
            0.40f,
            "joytagmodel_labels.txt",
            "joytagmodel_labels_Mapping.txt"),
        new(
            "ram-plus",
            "RAM++ Swin-Large",
            "ram_plus_swin_large_14m.onnx",
            ModelTask.MultiLabelTagging,
            TensorLayout.Nchw,
            PixelNormalization.ImageNet,
            384,
            false,
            0.40f,
            "ram_plus_swin_large_14m_labels.txt",
            "ram_plus_swin_large_14m_labels_Mapping.txt",
            BinaryTagMask: true,
            OutputName: "targets"),
        new(
            "siglip2",
            "SigLIP 2 Base (224)",
            "siglip2.json",
            ModelTask.ZeroShotTagging,
            TensorLayout.Nchw,
            PixelNormalization.MinusOneToOne,
            224,
            false,
            0.10f),
        new(
            "tinyclip",
            "TinyCLIP ViT-40M/32",
            "tinyclip.json",
            ModelTask.ZeroShotTagging,
            TensorLayout.Nchw,
            PixelNormalization.ImageNet,
            224,
            false,
            0.25f)
    ];
}
