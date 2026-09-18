using Microsoft.ML.OnnxRuntime.Tensors;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace PhotoLibrarian.Inference;

public sealed record ZeroShotInput(DenseTensor<float> Tensor, int Width, int Height);

public static class ZeroShotImagePreprocessor
{
    public static Task<ZeroShotInput> CreateAsync(
        string imagePath, ZeroShotModelBundle bundle,
        CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(imagePath);
            var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
            var width = checked((int)decoder.OrientedPixelWidth);
            var height = checked((int)decoder.OrientedPixelHeight);
            var size = bundle.InputSize;
            var decodeWidth = size;
            var decodeHeight = size;
            if (bundle.ResizeMode == "shortest-center-crop")
            {
                if (width >= height)
                    decodeWidth = checked((int)((long)width * size / height));
                else
                    decodeHeight = checked((int)((long)height * size / width));
            }

            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            cancellationToken.ThrowIfCancellationRequested();
            var pixels = pixelData.DetachPixelData();
            if (pixels.Length != checked(width * height * 4))
                throw new InvalidDataException("Unexpected decoded image dimensions.");
            pixels = ImageResampler.Resize(pixels, width, height, decodeWidth, decodeHeight,
                bundle.Interpolation == "cubic", cancellationToken);

            var left = (decodeWidth - size) / 2;
            var top = (decodeHeight - size) / 2;
            var tensor = new DenseTensor<float>([1, 3, size, size]);
            for (var y = 0; y < size; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < size; x++)
                {
                    var offset = ((y + top) * decodeWidth + x + left) * 4;
                    for (var channel = 0; channel < 3; channel++)
                    {
                        tensor[0, channel, y, x] = (pixels[offset + 2 - channel] / 255f -
                            bundle.NormalizationMean[channel]) / bundle.NormalizationStd[channel];
                    }
                }
            }
            return new ZeroShotInput(tensor, width, height);
        }, cancellationToken);
}
