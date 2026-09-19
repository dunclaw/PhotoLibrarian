using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PhotoLibrarian.Tests;

internal static class WicTestImage
{
    internal static async Task CreateAsync(string path, uint width, uint height, bool jpeg = false)
        => await CreateAsync(path, width, height, jpeg, null);

    internal static async Task CreateAsync(
        string path, uint width, uint height, bool jpeg, Func<uint, uint, (byte B, byte G, byte R)>? color)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(
            jpeg ? BitmapEncoder.JpegEncoderId : GetEncoderId(path), stream);
        var pixels = new byte[checked((int)(width * height * 4))];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var pixel = color?.Invoke((uint)(i / 4 % width), (uint)(i / 4 / width)) ?? (237, 149, 100);
            pixels[i] = pixel.B;
            pixels[i + 1] = pixel.G;
            pixels[i + 2] = pixel.R;
            pixels[i + 3] = 255;
        }

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        await File.WriteAllBytesAsync(path, ReadAll(stream));
    }

    internal static async Task<(uint Width, uint Height)> ReadSizeAsync(string path)
    {
        await using var file = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(file.AsRandomAccessStream());
        return (decoder.PixelWidth, decoder.PixelHeight);
    }

    internal static async Task<(uint Width, uint Height)> ReadPngSizeAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return (decoder.PixelWidth, decoder.PixelHeight);
    }

    private static byte[] ReadAll(IRandomAccessStream stream)
    {
        using var output = new MemoryStream();
        stream.AsStreamForRead().CopyTo(output);
        return output.ToArray();
    }

    private static Guid GetEncoderId(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".tif" or ".tiff" => BitmapEncoder.TiffEncoderId,
            ".bmp" => BitmapEncoder.BmpEncoderId,
            _ => BitmapEncoder.PngEncoderId
        };
}
