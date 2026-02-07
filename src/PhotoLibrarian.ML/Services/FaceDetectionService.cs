using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PhotoLibrarian.ML.Services;

/// <summary>
/// Face detection using SCRFD ONNX model. Detects face bounding boxes and landmarks.
/// </summary>
public sealed class FaceDetectionService
{
    private readonly OnnxSessionManager _sessionManager;
    private InferenceSession? _session;

    public string ModelFileName { get; set; } = "scrfd_10g.onnx";
    public int InputSize { get; set; } = 640;
    public float ConfidenceThreshold { get; set; } = 0.5f;
    public float NmsThreshold { get; set; } = 0.4f;

    public FaceDetectionService(OnnxSessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public bool IsModelAvailable => _sessionManager.ModelExists(ModelFileName);

    public void LoadModel()
    {
        _session = _sessionManager.LoadModel(ModelFileName);
    }

    /// <summary>
    /// Detects faces in an image. Returns normalized bounding boxes (0-1 range).
    /// </summary>
    public async Task<List<DetectedFace>> DetectFacesAsync(string imagePath)
    {
        if (_session is null)
            throw new InvalidOperationException("Model not loaded.");

        return await Task.Run(() =>
        {
            // Preprocess: resize to InputSize maintaining aspect ratio, pad to square
            using var stream = File.OpenRead(imagePath);
            var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(
                stream.AsRandomAccessStream()).AsTask().Result;

            var origW = (int)decoder.PixelWidth;
            var origH = (int)decoder.PixelHeight;
            var scale = Math.Min((float)InputSize / origW, (float)InputSize / origH);
            var newW = (int)(origW * scale);
            var newH = (int)(origH * scale);

            var transform = new Windows.Graphics.Imaging.BitmapTransform
            {
                ScaledWidth = (uint)newW,
                ScaledHeight = (uint)newH,
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

            // Create padded tensor (InputSize x InputSize)
            var tensor = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
            for (int y = 0; y < newH; y++)
            {
                for (int x = 0; x < newW; x++)
                {
                    int idx = (y * newW + x) * 4;
                    tensor[0, 0, y, x] = pixels[idx + 2]; // R
                    tensor[0, 1, y, x] = pixels[idx + 1]; // G
                    tensor[0, 2, y, x] = pixels[idx];     // B
                }
            }

            var inputName = _session.InputNames[0];
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) };
            using var results = _session.Run(inputs);

            return ParseDetections(results, scale, origW, origH);
        });
    }

    private List<DetectedFace> ParseDetections(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, float scale, int origW, int origH)
    {
        var faces = new List<DetectedFace>();

        // SCRFD outputs vary by model variant; process generic bounding box output
        var outputTensors = results.ToList();
        if (outputTensors.Count == 0) return faces;

        // Simple parsing: assume first output contains [N, 5+] where columns are [x1, y1, x2, y2, score, ...]
        var output = outputTensors[0].AsTensor<float>();
        var dims = output.Dimensions;

        if (dims.Length == 2)
        {
            int numDetections = dims[0];
            for (int i = 0; i < numDetections; i++)
            {
                var score = output[i, 4];
                if (score < ConfidenceThreshold) continue;

                // Convert from padded coords back to normalized (0-1)
                float x1 = output[i, 0] / scale / origW;
                float y1 = output[i, 1] / scale / origH;
                float x2 = output[i, 2] / scale / origW;
                float y2 = output[i, 3] / scale / origH;

                faces.Add(new DetectedFace
                {
                    X = Math.Clamp(x1, 0, 1),
                    Y = Math.Clamp(y1, 0, 1),
                    Width = Math.Clamp(x2 - x1, 0, 1),
                    Height = Math.Clamp(y2 - y1, 0, 1),
                    Confidence = score
                });
            }
        }

        return NMS(faces);
    }

    /// <summary>
    /// Non-maximum suppression to remove overlapping detections.
    /// </summary>
    private List<DetectedFace> NMS(List<DetectedFace> faces)
    {
        var sorted = faces.OrderByDescending(f => f.Confidence).ToList();
        var keep = new List<DetectedFace>();

        while (sorted.Count > 0)
        {
            var best = sorted[0];
            keep.Add(best);
            sorted.RemoveAt(0);
            sorted.RemoveAll(f => IoU(best, f) > NmsThreshold);
        }

        return keep;
    }

    private static float IoU(DetectedFace a, DetectedFace b)
    {
        float x1 = Math.Max(a.X, b.X);
        float y1 = Math.Max(a.Y, b.Y);
        float x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        float y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        float intersection = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        float union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union > 0 ? intersection / union : 0;
    }
}

public sealed class DetectedFace
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float Confidence { get; set; }
}
