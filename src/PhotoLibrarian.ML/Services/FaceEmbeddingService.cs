using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PhotoLibrarian.ML.Services;

/// <summary>
/// Face embedding generation using ArcFace/AdaFace ONNX model.
/// Produces 512-dimensional vectors for face recognition/clustering.
/// </summary>
public sealed class FaceEmbeddingService
{
    private readonly OnnxSessionManager _sessionManager;
    private InferenceSession? _session;

    public string ModelFileName { get; set; } = "arcface_r100.onnx";
    public int InputSize { get; set; } = 112;
    public int EmbeddingDimension { get; set; } = 512;

    public FaceEmbeddingService(OnnxSessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public bool IsModelAvailable => _sessionManager.ModelExists(ModelFileName);

    public void LoadModel()
    {
        _session = _sessionManager.LoadModel(ModelFileName);
    }

    /// <summary>
    /// Generates a face embedding from a cropped face region of an image.
    /// </summary>
    public async Task<float[]?> GenerateEmbeddingAsync(string imagePath, DetectedFace faceRegion)
    {
        if (_session is null)
            throw new InvalidOperationException("Model not loaded.");

        return await Task.Run(() =>
        {
            // Decode full image
            using var stream = File.OpenRead(imagePath);
            var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(
                stream.AsRandomAccessStream()).AsTask().Result;

            var imgW = (int)decoder.PixelWidth;
            var imgH = (int)decoder.PixelHeight;

            // Calculate crop bounds with margin
            float margin = 0.1f;
            int cropX = Math.Max(0, (int)((faceRegion.X - margin) * imgW));
            int cropY = Math.Max(0, (int)((faceRegion.Y - margin) * imgH));
            int cropW = Math.Min(imgW - cropX, (int)((faceRegion.Width + 2 * margin) * imgW));
            int cropH = Math.Min(imgH - cropY, (int)((faceRegion.Height + 2 * margin) * imgH));

            var bounds = new Windows.Graphics.Imaging.BitmapBounds
            {
                X = (uint)cropX,
                Y = (uint)cropY,
                Width = (uint)Math.Max(1, cropW),
                Height = (uint)Math.Max(1, cropH)
            };

            var transform = new Windows.Graphics.Imaging.BitmapTransform
            {
                Bounds = bounds,
                ScaledWidth = (uint)InputSize,
                ScaledHeight = (uint)InputSize,
                InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Linear
            };

            var pixelData = decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb
            ).AsTask().Result;

            var pixels = pixelData.DetachPixelData();

            // Build tensor (NCHW, normalized)
            var tensor = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
            for (int y = 0; y < InputSize; y++)
            {
                for (int x = 0; x < InputSize; x++)
                {
                    int idx = (y * InputSize + x) * 4;
                    tensor[0, 0, y, x] = (pixels[idx + 2] - 127.5f) / 128f; // R
                    tensor[0, 1, y, x] = (pixels[idx + 1] - 127.5f) / 128f; // G
                    tensor[0, 2, y, x] = (pixels[idx] - 127.5f) / 128f;     // B
                }
            }

            var inputName = _session.InputNames[0];
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) };
            using var results = _session.Run(inputs);

            var output = results.First().AsTensor<float>();
            var embedding = new float[EmbeddingDimension];
            for (int i = 0; i < EmbeddingDimension && i < output.Length; i++)
                embedding[i] = output[0, i];

            // L2 normalize
            var norm = MathF.Sqrt(embedding.Sum(e => e * e));
            if (norm > 0)
                for (int i = 0; i < embedding.Length; i++)
                    embedding[i] /= norm;

            return embedding;
        });
    }

    /// <summary>
    /// Computes cosine similarity between two face embeddings.
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom > 0 ? dot / denom : 0;
    }
}
