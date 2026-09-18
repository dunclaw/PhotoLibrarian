using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PhotoLibrarian.ML.Services;

/// <summary>
/// Image preprocessing utilities for ONNX model inference.
/// Handles resize, normalization, and tensor conversion.
/// </summary>
public static class ImagePreprocessor
{
    /// <summary>
    /// Loads an image file and converts it to a normalized float tensor
    /// suitable for model input (NCHW format).
    /// </summary>
    /// <param name="filePath">Path to image file</param>
    /// <param name="targetSize">Target square size (e.g., 384 for RAM++)</param>
    /// <param name="mean">Per-channel normalization mean (RGB)</param>
    /// <param name="std">Per-channel normalization std (RGB)</param>
    public static async Task<DenseTensor<float>> PreprocessImageAsync(
        string filePath, int targetSize,
        float[] mean, float[] std,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Use WIC for fast decode + resize
            using var stream = File.OpenRead(filePath);
            var decoder =
                await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(
                    stream.AsRandomAccessStream());

            var transform = new Windows.Graphics.Imaging.BitmapTransform
            {
                ScaledWidth = (uint)targetSize,
                ScaledHeight = (uint)targetSize,
                InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Linear
            };

            var pixelData = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb
            );

            var pixels = pixelData.DetachPixelData();
            cancellationToken.ThrowIfCancellationRequested();
            return PixelsToTensor(pixels, targetSize, targetSize, mean, std);
        }, cancellationToken);
    }

    public static async Task<DenseTensor<float>> PreprocessImageNhwcAsync(
        string filePath,
        int targetSize,
        float mean,
        float scale,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(filePath);
            var decoder =
                await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(
                    stream.AsRandomAccessStream());
            var transform = new Windows.Graphics.Imaging.BitmapTransform
            {
                ScaledWidth = (uint)targetSize,
                ScaledHeight = (uint)targetSize,
                InterpolationMode =
                    Windows.Graphics.Imaging.BitmapInterpolationMode.Linear
            };
            var pixelData = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb
            );

            var pixels = pixelData.DetachPixelData();
            var tensor = new DenseTensor<float>(
                [1, targetSize, targetSize, 3]);
            for (var y = 0; y < targetSize; y++)
            {
                for (var x = 0; x < targetSize; x++)
                {
                    var index = (y * targetSize + x) * 4;
                    tensor[0, y, x, 0] = (pixels[index + 2] - mean) / scale;
                    tensor[0, y, x, 1] = (pixels[index + 1] - mean) / scale;
                    tensor[0, y, x, 2] = (pixels[index] - mean) / scale;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return tensor;
        }, cancellationToken);
    }

    public static async Task<DenseTensor<float>>
        PreprocessImageUnitNchwLetterboxAsync(
            string filePath,
            int targetSize,
            CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(filePath);
            var decoder =
                await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(
                    stream.AsRandomAccessStream());
            var width = checked((int)decoder.OrientedPixelWidth);
            var height = checked((int)decoder.OrientedPixelHeight);
            var resizeScale = Math.Min(
                targetSize / (float)width,
                targetSize / (float)height);
            var resizedWidth = Math.Max(
                1,
                (int)Math.Round(width * resizeScale));
            var resizedHeight = Math.Max(
                1,
                (int)Math.Round(height * resizeScale));
            var paddingX = (targetSize - resizedWidth) / 2;
            var paddingY = (targetSize - resizedHeight) / 2;
            var transform = new Windows.Graphics.Imaging.BitmapTransform
            {
                ScaledWidth = (uint)resizedWidth,
                ScaledHeight = (uint)resizedHeight,
                InterpolationMode =
                    Windows.Graphics.Imaging.BitmapInterpolationMode.Linear
            };
            var pixelData = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb
            );
            var sourcePixels = pixelData.DetachPixelData();
            var pixels = new byte[targetSize * targetSize * 4];

            for (var y = 0; y < resizedHeight; y++)
            {
                Buffer.BlockCopy(
                    sourcePixels,
                    y * resizedWidth * 4,
                    pixels,
                    ((y + paddingY) * targetSize + paddingX) * 4,
                    resizedWidth * 4);
            }

            var tensor = new DenseTensor<float>(
                [1, 3, targetSize, targetSize]);
            for (var y = 0; y < targetSize; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < targetSize; x++)
                {
                    var index = (y * targetSize + x) * 4;
                    tensor[0, 0, y, x] =
                        pixels[index + 2] / 255f;
                    tensor[0, 1, y, x] =
                        pixels[index + 1] / 255f;
                    tensor[0, 2, y, x] =
                        pixels[index] / 255f;
                }
            }

            return tensor;
        }, cancellationToken);
    }

    /// <summary>
    /// Converts BGRA pixel data to NCHW float tensor with normalization.
    /// </summary>
    private static DenseTensor<float> PixelsToTensor(
        byte[] bgra, int width, int height,
        float[] mean, float[] std)
    {
        var tensor = new DenseTensor<float>([1, 3, height, width]);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = (y * width + x) * 4; // BGRA
                float b = bgra[idx] / 255f;
                float g = bgra[idx + 1] / 255f;
                float r = bgra[idx + 2] / 255f;

                // Normalize: (pixel - mean) / std
                tensor[0, 0, y, x] = (r - mean[0]) / std[0]; // R channel
                tensor[0, 1, y, x] = (g - mean[1]) / std[1]; // G channel
                tensor[0, 2, y, x] = (b - mean[2]) / std[2]; // B channel
            }
        }

        return tensor;
    }

    /// <summary>
    /// Creates a named ONNX input value from a tensor.
    /// </summary>
    public static NamedOnnxValue CreateInput(string name, DenseTensor<float> tensor)
    {
        return NamedOnnxValue.CreateFromTensor(name, tensor);
    }

    /// <summary>
    /// Standard ImageNet normalization parameters.
    /// </summary>
    public static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];
    public static readonly float[] ImageNetStd = [0.229f, 0.224f, 0.225f];
}
