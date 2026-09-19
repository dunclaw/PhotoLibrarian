using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PhotoLibrarian.Core.Services;

internal static class WindowsImagingCodec
{
    internal sealed record DecodedImage(byte[] Pixels, uint Width, uint Height);

    internal static async Task<DecodedImage> DecodeAsync(
        string path,
        uint maximumDimension = uint.MaxValue,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream()).AsTask(cancellationToken);
        var scale = Math.Min(1d, (double)maximumDimension /
            Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken);
        var orientedWidth = Math.Max(1, (uint)Math.Round(decoder.OrientedPixelWidth * scale));
        var orientedHeight = Math.Max(1, (uint)Math.Round(decoder.OrientedPixelHeight * scale));
        return new DecodedImage(pixels.DetachPixelData(), orientedWidth, orientedHeight);
    }

    internal static async Task<byte[]> EncodeAsync(
        string path,
        byte[] pixels,
        uint width,
        uint height,
        CancellationToken cancellationToken = default)
    {
        var encoderId = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => BitmapEncoder.PngEncoderId,
            ".tif" or ".tiff" => BitmapEncoder.TiffEncoderId,
            ".bmp" => BitmapEncoder.BmpEncoderId,
            _ => BitmapEncoder.JpegEncoderId
        };
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream).AsTask(cancellationToken);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            width,
            height,
            96,
            96,
            pixels);
        await encoder.FlushAsync().AsTask(cancellationToken);
        stream.Seek(0);
        using var output = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(output, cancellationToken);
        return output.ToArray();
    }

    internal static byte[] Crop(byte[] pixels, uint sourceWidth, uint sourceHeight, CropRectangle crop)
    {
        var rowBytes = checked((int)crop.Width * 4);
        var result = new byte[checked(rowBytes * (int)crop.Height)];
        for (var row = 0; row < crop.Height; row++)
        {
            var sourceOffset = checked(((int)crop.Y + row) * (int)sourceWidth * 4 + (int)crop.X * 4);
            System.Buffer.BlockCopy(pixels, sourceOffset, result, row * rowBytes, rowBytes);
        }
        return result;
    }

    internal static byte[] ResizeBicubic(byte[] pixels, uint sourceWidth, uint sourceHeight, uint width, uint height)
    {
        using var source = SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(), BitmapPixelFormat.Bgra8, (int)sourceWidth, (int)sourceHeight,
            BitmapAlphaMode.Premultiplied);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).GetAwaiter().GetResult();
        encoder.SetSoftwareBitmap(source);
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
        stream.Seek(0);
        var decoder = BitmapDecoder.CreateAsync(stream).GetAwaiter().GetResult();
        var transform = new BitmapTransform
        {
            ScaledWidth = width,
            ScaledHeight = height,
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        var data = decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.ColorManageToSRgb).GetAwaiter().GetResult();
        return data.DetachPixelData();
    }
}
