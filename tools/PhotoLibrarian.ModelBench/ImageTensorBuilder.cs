using Microsoft.ML.OnnxRuntime.Tensors;
using PhotoLibrarian.Inference;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace PhotoLibrarian.ModelBench;

public sealed record ImageTransform(
    int OriginalWidth,
    int OriginalHeight,
    int InputWidth,
    int InputHeight,
    float Scale,
    int PaddingX,
    int PaddingY);

public sealed record PreparedInput(
    DenseTensor<float> Tensor,
    ImageTransform Transform);

public static class ImageTensorBuilder
{
    private static readonly float[] ImageNetMean =
        [0.485f, 0.456f, 0.406f];
    private static readonly float[] ImageNetStd =
        [0.229f, 0.224f, 0.225f];

    public static PreparedInput Create(
        string imagePath,
        int inputWidth,
        int inputHeight,
        ModelDefinition model,
        ZeroShotModelBundle? bundle = null)
    {
        if (bundle is not null)
        {
            return CreateZeroShot(imagePath, bundle);
        }

        using var stream = File.OpenRead(imagePath);
        var decoder = BitmapDecoder.CreateAsync(
            stream.AsRandomAccessStream()).AsTask().GetAwaiter().GetResult();
        var originalWidth = checked((int)decoder.OrientedPixelWidth);
        var originalHeight = checked((int)decoder.OrientedPixelHeight);
        var transform = CreateTransform(
            originalWidth,
            originalHeight,
            inputWidth,
            inputHeight,
            model.PreserveAspectRatio);
        var decodeWidth = model.PreserveAspectRatio
            ? Math.Max(
                1,
                (int)Math.Round(originalWidth * transform.Scale))
            : inputWidth;
        var decodeHeight = model.PreserveAspectRatio
            ? Math.Max(
                1,
                (int)Math.Round(originalHeight * transform.Scale))
            : inputHeight;
        var bitmapTransform = new BitmapTransform
        {
            ScaledWidth = (uint)decodeWidth,
            ScaledHeight = (uint)decodeHeight,
            InterpolationMode = BitmapInterpolationMode.Linear
        };
        var pixelData = decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            bitmapTransform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        var decodedPixels = pixelData.DetachPixelData();
        var preparedPixels = model.PreserveAspectRatio
            ? Letterbox(
                decodedPixels,
                decodeWidth,
                decodeHeight,
                inputWidth,
                inputHeight,
                transform.PaddingX,
                transform.PaddingY)
            : decodedPixels;
        var tensor = ToTensor(
            preparedPixels,
            inputWidth,
            inputHeight,
            model.Layout,
            model.Normalization);
        return new PreparedInput(tensor, transform);
    }

    private static PreparedInput CreateZeroShot(
        string imagePath,
        ZeroShotModelBundle bundle)
    {
        var prepared = ZeroShotImagePreprocessor.CreateAsync(imagePath, bundle).GetAwaiter().GetResult();
        return new PreparedInput(prepared.Tensor, new ImageTransform(
            prepared.Width, prepared.Height, bundle.InputSize, bundle.InputSize,
            bundle.InputSize / (float)prepared.Width, 0, 0));
    }

    private static ImageTransform CreateTransform(
        int originalWidth,
        int originalHeight,
        int inputWidth,
        int inputHeight,
        bool letterbox)
    {
        if (!letterbox)
        {
            return new ImageTransform(
                originalWidth,
                originalHeight,
                inputWidth,
                inputHeight,
                inputWidth / (float)originalWidth,
                0,
                0);
        }

        var scale = Math.Min(
            inputWidth / (float)originalWidth,
            inputHeight / (float)originalHeight);
        var resizedWidth = Math.Max(
            1,
            (int)Math.Round(originalWidth * scale));
        var resizedHeight = Math.Max(
            1,
            (int)Math.Round(originalHeight * scale));
        return new ImageTransform(
            originalWidth,
            originalHeight,
            inputWidth,
            inputHeight,
            scale,
            (inputWidth - resizedWidth) / 2,
            (inputHeight - resizedHeight) / 2);
    }

    private static byte[] Letterbox(
        byte[] pixels,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        int paddingX,
        int paddingY)
    {
        var output = new byte[targetWidth * targetHeight * 4];
        for (var index = 0; index < output.Length; index += 4)
        {
            output[index + 3] = 255;
        }

        var sourceStride = sourceWidth * 4;
        var targetStride = targetWidth * 4;
        for (var y = 0; y < sourceHeight; y++)
        {
            Buffer.BlockCopy(
                pixels,
                y * sourceStride,
                output,
                (y + paddingY) * targetStride + paddingX * 4,
                sourceStride);
        }

        return output;
    }

    private static DenseTensor<float> ToTensor(
        byte[] pixels,
        int width,
        int height,
        TensorLayout layout,
        PixelNormalization normalization)
    {
        var tensor = layout == TensorLayout.Nchw
            ? new DenseTensor<float>(
                [1, 3, height, width])
            : new DenseTensor<float>(
                [1, height, width, 3]);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var red = Normalize(pixels[offset + 2], 0, normalization);
                var green = Normalize(pixels[offset + 1], 1, normalization);
                var blue = Normalize(pixels[offset], 2, normalization);
                if (layout == TensorLayout.Nchw)
                {
                    tensor[0, 0, y, x] = red;
                    tensor[0, 1, y, x] = green;
                    tensor[0, 2, y, x] = blue;
                }
                else
                {
                    tensor[0, y, x, 0] = red;
                    tensor[0, y, x, 1] = green;
                    tensor[0, y, x, 2] = blue;
                }
            }
        }

        return tensor;
    }

    private static float Normalize(
        byte value,
        int channel,
        PixelNormalization normalization) =>
        normalization switch
        {
            PixelNormalization.Unit => value / 255f,
            PixelNormalization.MinusOneToOne =>
                value / 127.5f - 1f,
            PixelNormalization.ImageNet =>
                (value / 255f - ImageNetMean[channel]) /
                ImageNetStd[channel],
            PixelNormalization.EfficientNetLite =>
                (value - 127f) / 128f,
            _ => throw new InvalidOperationException(
                $"Unsupported normalization {normalization}.")
        };
}
