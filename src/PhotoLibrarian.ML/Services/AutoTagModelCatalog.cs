using System.Security.Cryptography;

namespace PhotoLibrarian.ML.Services;

public static class AutoTagModelCatalog
{
    public const float TinyClipOpenThreshold = 0.27f;
    public const float TinyClipConservativeThreshold = 0.34f;
    public static Uri ModelSetupInstructionsUri { get; } = new(
        "https://github.com/dunclaw/PhotoLibrarian/blob/main/MODEL-ASSETS.md");
    public static Uri RamPlusSourceUri { get; } = new(
        "https://huggingface.co/xinyu1205/recognize-anything-plus-model");
    public static Uri TinyClipSourceUri { get; } = new(
        "https://huggingface.co/wkcn/TinyCLIP-ViT-40M-32-Text-19M-LAION400M");
    public const string TinyClipProfileId = "tinyclip.vit-40m-32.int8";
    public const string RamPlusProfileId =
        "ram-plus.swin-large-14m";
    public const string MobileNetV2ProfileId =
        "imagenet.mobilenetv2-12.legacy";
    public const string EfficientNetLite4ProfileId =
        "imagenet.efficientnet-lite4-11.legacy";

    private const string ModelZooRevision =
        "4c46cd00fbdb7cd30b6c1c17ab54f2e1f4f7b177";
    private const string ModelZooMediaRoot =
        $"https://media.githubusercontent.com/media/onnx/models/{ModelZooRevision}/validated/vision/classification";
    private const string ModelZooRawRoot =
        $"https://raw.githubusercontent.com/onnx/models/{ModelZooRevision}/validated/vision/classification";

    private static readonly AutoTagAssetDefinition ImageNetLabels = new(
        "imagenet-labels.json",
        new Uri($"{ModelZooRawRoot}/efficientnet-lite4/dependencies/labels_map.txt"),
        "3e232637a09beeb733f409908cfc0ba256e83f06815684678abafd4c0990c757",
        31_000);
    private static readonly Lazy<IReadOnlyDictionary<string, string>>
        SafeImageNetMappings = new(CreateSafeImageNetMappings);

    public static AutoTagModelDefinition MobileNetV2 { get; } = new(
        MobileNetV2ProfileId,
        "MobileNetV2 (legacy experiment)",
        "Fast whole-image ImageNet classifier. Use only after reviewing a benchmark; it is not optimized for photo-library tagging.",
        new AutoTagAssetDefinition(
            "mobilenetv2-12.onnx",
            new Uri($"{ModelZooMediaRoot}/mobilenet/model/mobilenetv2-12.onnx"),
            "c0c3f76d93fa3fd6580652a45618618a220fced18babf65774ed169de0432ad5",
            14_000_000),
        ImageNetLabels,
        224,
        AutoTagTensorLayout.Nchw,
        0.20f,
        1,
        5,
        "mobilenetv2-12-imagenet-safe-v2",
        AutoTagVocabularySafety.CuratedAllowlist,
        SafeImageNetMappings.Value);

    public static AutoTagModelDefinition EfficientNetLite4 { get; } = new(
        EfficientNetLite4ProfileId,
        "EfficientNet-Lite4 (legacy experiment)",
        "Slower whole-image ImageNet classifier with more suggestions. Experimental and known to produce weak library tags.",
        new AutoTagAssetDefinition(
            "efficientnet-lite4-11.onnx",
            new Uri($"{ModelZooMediaRoot}/efficientnet-lite4/model/efficientnet-lite4-11.onnx"),
            "d111689907c06eea7c82e4833ddef758da6453b9d4cf60b7e99ca05c7cbd9c12",
            50_000_000),
        ImageNetLabels,
        224,
        AutoTagTensorLayout.Nhwc,
        0.08f,
        3,
        5,
        "efficientnet-lite4-11-imagenet-safe-v2",
        AutoTagVocabularySafety.CuratedAllowlist,
        SafeImageNetMappings.Value,
        PixelNormalization: AutoTagPixelNormalization.EfficientNetLite);

    public static AutoTagModelDefinition RamPlus { get; } = new(
        RamPlusProfileId,
        "RAM++ Swin-Large (recommended)",
        "Multi-label descriptive tagger with a general-purpose vocabulary drawn from all 4,585 model labels. " +
        "Shares a hierarchy with TinyCLIP. Review the model source and profile details before enabling it.",
        new AutoTagAssetDefinition(
            "ram_plus_swin_large_14m.onnx",
            null,
            "FF29E0E18E80B8F2FDC2566B9368BD904664CDC1FCE380F57E73DCF03F4ADDA3",
            1_849_873_814),
        new AutoTagAssetDefinition(
            "ram_plus_swin_large_14m_labels.txt",
            null,
            "1A6C943DD251993770E7CF6FED23A38B7AC068F4C8FBC7A0DB85CBE0FE5221B3",
            41_905),
        384,
        AutoTagTensorLayout.Nchw,
        0.40f,
        8,
        10,
        "ram-plus-swin-large-14m-general-v2-binary-mask-" + AutoTagHierarchy.Version,
        AutoTagVocabularySafety.CuratedAllowlist,
        GeneralPhotoVocabulary.Current.AllowedRamLabels,
        AutoTagPixelNormalization.ImageNet,
        PreserveAspectRatio: false,
        OutputKind: AutoTagOutputKind.BinaryTagMask,
        UsesTagHierarchy: true,
        OutputName: "targets");

    public static AutoTagModelDefinition TinyClip { get; } = new(
        TinyClipProfileId,
        "TinyCLIP (lightweight, experimental)",
        $"CPU-only photo tagger with {GeneralPhotoVocabulary.Current.Labels.Length:N0} general-purpose concepts. " +
        "Broader-coverage provisional cosine cutoffs trade some precision for more tagged photos. " +
        "New concepts remain unvalidated; review the profile details before enabling it.",
        new AutoTagAssetDefinition(
            "tinyclip-vision-int8.onnx", null,
            TinyClipCalibration.ModelHash, 40_622_561),
        new AutoTagAssetDefinition(
            "tinyclip.json", null,
            "F40DF22806EB77961356898CE0B01B540139CA412B877860417BEB4E8B16BC8A",
            58_298_021),
        224,
        AutoTagTensorLayout.Nchw,
        TinyClipCalibration.Current.FallbackThreshold,
        8,
        10,
        "tinyclip-40m-int8-cpu-general-v2-coverage60-top10-20260917-" + AutoTagHierarchy.Version,
        AutoTagVocabularySafety.CuratedAllowlist,
        TinyClipCalibration.Current.Labels.ToDictionary(label => label, label => label, StringComparer.Ordinal),
        AutoTagPixelNormalization.Clip,
        OutputKind: AutoTagOutputKind.CosineEmbedding,
        UseCpuOnly: true,
        UsesCalibratedThresholds: true,
        UsesTagHierarchy: true);

    public static IReadOnlyList<AutoTagModelDefinition> Profiles { get; } =
        [RamPlus, TinyClip, MobileNetV2, EfficientNetLite4];

    public static AutoTagModelDefinition ForId(string profileId) =>
        Profiles.FirstOrDefault(profile =>
            profile.Id.Equals(profileId, StringComparison.Ordinal))
        ?? throw new ArgumentException(
            $"Unknown automatic-tagging model profile '{profileId}'.",
            nameof(profileId));

    public static double TinyClipConfidenceToOpenness(
        double confidencePercent)
    {
        var open = TinyClipOpenThreshold * 100;
        var conservative = TinyClipConservativeThreshold * 100;
        return 100 * (conservative - Math.Clamp(
            confidencePercent, open, conservative)) /
            (conservative - open);
    }

    public static double TinyClipOpennessToConfidence(double openness)
    {
        var open = TinyClipOpenThreshold * 100;
        var conservative = TinyClipConservativeThreshold * 100;
        return conservative -
            Math.Clamp(openness, 0, 100) *
            (conservative - open) / 100;
    }

    public static Uri SourceUriFor(string profileId) =>
        profileId == TinyClipProfileId
            ? TinyClipSourceUri
            : profileId == RamPlusProfileId
                ? RamPlusSourceUri
                : new Uri("https://github.com/onnx/models");

    public static bool TryGet(
        string? profileId,
        out AutoTagModelDefinition definition)
    {
        definition = Profiles.FirstOrDefault(profile =>
            profile.Id.Equals(profileId, StringComparison.Ordinal))!;
        return definition is not null;
    }

    private static IReadOnlyDictionary<string, string>
        CreateSafeImageNetMappings() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tabby"] = "cat",
            ["tiger cat"] = "cat",
            ["persian cat"] = "cat",
            ["siamese cat"] = "cat",
            ["egyptian cat"] = "cat",
            ["golden retriever"] = "dog",
            ["labrador retriever"] = "dog",
            ["german shepherd"] = "dog",
            ["border collie"] = "dog",
            ["beagle"] = "dog",
            ["pug"] = "dog",
            ["chihuahua"] = "dog",
            ["dalmatian"] = "dog",
            ["husky"] = "dog",
            ["red fox"] = "fox",
            ["arctic fox"] = "fox",
            ["brown bear"] = "bear",
            ["polar bear"] = "bear",
            ["giant panda"] = "panda",
            ["koala"] = "koala",
            ["zebra"] = "zebra",
            ["lion"] = "lion",
            ["tiger"] = "tiger",
            ["cheetah"] = "cheetah",
            ["elephant"] = "elephant",
            ["gorilla"] = "gorilla",
            ["orangutan"] = "orangutan",
            ["horse"] = "horse",
            ["ox"] = "cattle",
            ["sheep"] = "sheep",
            ["goose"] = "goose",
            ["duck"] = "duck",
            ["flamingo"] = "flamingo",
            ["bald eagle"] = "eagle",
            ["butterfly"] = "butterfly",
            ["ladybug"] = "ladybug",
            ["bee"] = "bee",
            ["seashore"] = "beach",
            ["lakeside"] = "lake",
            ["valley"] = "valley",
            ["volcano"] = "volcano",
            ["alp"] = "mountain",
            ["cliff"] = "cliff",
            ["coral reef"] = "coral reef",
            ["geyser"] = "geyser",
            ["sandbar"] = "beach",
            ["greenhouse"] = "greenhouse",
            ["palace"] = "palace",
            ["castle"] = "castle",
            ["monastery"] = "monastery",
            ["church"] = "church",
            ["barn"] = "barn",
            ["boathouse"] = "boathouse",
            ["lighthouse"] = "lighthouse",
            ["fountain"] = "fountain",
            ["suspension bridge"] = "bridge",
            ["steel arch bridge"] = "bridge",
            ["viaduct"] = "bridge",
            ["airliner"] = "airplane",
            ["warplane"] = "airplane",
            ["sports car"] = "car",
            ["minivan"] = "car",
            ["pickup"] = "truck",
            ["school bus"] = "bus",
            ["streetcar"] = "streetcar",
            ["steam locomotive"] = "train",
            ["mountain bike"] = "bicycle",
            ["canoe"] = "canoe",
            ["kayak"] = "kayak",
            ["sailboat"] = "sailboat",
            ["speedboat"] = "boat",
            ["gondola"] = "gondola",
            ["hot pot"] = "food",
            ["pizza"] = "pizza",
            ["cheeseburger"] = "burger",
            ["ice cream"] = "ice cream",
            ["strawberry"] = "strawberry",
            ["orange"] = "orange",
            ["lemon"] = "lemon",
            ["pineapple"] = "pineapple",
            ["banana"] = "banana",
            ["broccoli"] = "broccoli",
            ["bell pepper"] = "pepper",
            ["mushroom"] = "mushroom",
            ["daisy"] = "flower",
            ["yellow lady's slipper"] = "flower",
            ["acorn"] = "acorn",
            ["rapeseed"] = "flowers",
            ["bolete"] = "mushroom"
        };

}

public enum AutoTagTensorLayout
{
    Nchw,
    Nhwc
}

public enum AutoTagPixelNormalization
{
    ImageNet,
    EfficientNetLite,
    Unit,
    Clip
}

public enum AutoTagOutputKind
{
    SingleLabelClassification,
    DirectMultiLabel,
    CosineEmbedding,
    BinaryTagMask
}

public enum AutoTagVocabularySafety
{
    CuratedAllowlist,
    Unreviewed
}

public sealed record AutoTagAssetDefinition(
    string FileName,
    Uri? DownloadUri,
    string Sha256,
    long DownloadSizeBytes)
{
    public bool HasExpectedHash(Stream stream)
    {
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return hash.Equals(Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public bool RequiresImport => DownloadUri is null;
}

public sealed record AutoTagModelDefinition(
    string Id,
    string DisplayName,
    string Purpose,
    AutoTagAssetDefinition ModelAsset,
    AutoTagAssetDefinition LabelsAsset,
    int InputSize,
    AutoTagTensorLayout TensorLayout,
    float DefaultConfidenceThreshold,
    int DefaultMaximumTags,
    int MaximumSupportedTags,
    string PipelineVersion,
    AutoTagVocabularySafety VocabularySafety,
    IReadOnlyDictionary<string, string> SafeLabelMappings,
    AutoTagPixelNormalization PixelNormalization =
        AutoTagPixelNormalization.ImageNet,
    bool PreserveAspectRatio = false,
    AutoTagOutputKind OutputKind =
        AutoTagOutputKind.SingleLabelClassification,
    bool UseCpuOnly = false,
    bool UsesCalibratedThresholds = false,
    bool UsesTagHierarchy = false,
    string? OutputName = null)
{
    public bool UsesFixedThresholds =>
        OutputKind == AutoTagOutputKind.BinaryTagMask;

    public long DownloadSizeBytes =>
        ModelAsset.DownloadSizeBytes + LabelsAsset.DownloadSizeBytes;

    public string ApprovalKey => $"{Id}|{PipelineVersion}";

    public bool CanBeApproved =>
        VocabularySafety == AutoTagVocabularySafety.CuratedAllowlist &&
        SafeLabelMappings.Count > 0;

    public bool RequiresLocalImport =>
        ModelAsset.RequiresImport || LabelsAsset.RequiresImport;
}
