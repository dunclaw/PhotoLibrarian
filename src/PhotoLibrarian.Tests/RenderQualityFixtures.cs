using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PhotoLibrarian.Tests;

internal enum RenderQualityFixtureKind
{
    FlatNoise,
    ColorNoise,
    Edge,
    FineTexture,
    Gradient,
    KnownBlur
}

internal static class RenderQualityFixtures
{
    internal static readonly RenderQualityFixtureKind[] Kinds = Enum.GetValues<RenderQualityFixtureKind>();

    internal static byte[] CreatePixels(
        RenderQualityFixtureKind kind,
        uint width,
        uint height,
        int seed = 217,
        bool alpha = false)
    {
        if (width == 0 || height == 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Fixture dimensions must be nonzero.");

        var random = new Random(seed);
        var pixels = new byte[checked((int)((long)width * height * 4))];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = kind switch
                {
                    RenderQualityFixtureKind.FlatNoise => Noisy(random, 128, 128, 128, 18),
                    RenderQualityFixtureKind.ColorNoise => Noisy(random, 76, 137, 205, 22),
                    RenderQualityFixtureKind.Edge => x < width / 2 ? (24, 24, 24) : (232, 232, 232),
                    RenderQualityFixtureKind.FineTexture =>
                        ((x + y) % 2 == 0) ? (42, 162, 224) : (207, 91, 38),
                    RenderQualityFixtureKind.Gradient => (
                        Scale((uint)x, width),
                        Scale((uint)y, height),
                        Scale((uint)(x + y), width + height - 2)),
                    RenderQualityFixtureKind.KnownBlur => (0, 0, 0),
                    _ => throw new ArgumentOutOfRangeException(nameof(kind))
                };

                var index = checked((int)(((long)y * width + x) * 4));
                var a = alpha ? (byte)((x * 61 + y * 37) % 256) : (byte)255;
                pixels[index] = Premultiply(b, a);
                pixels[index + 1] = Premultiply(g, a);
                pixels[index + 2] = Premultiply(r, a);
                pixels[index + 3] = a;
            }
        }

        if (kind == RenderQualityFixtureKind.KnownBlur)
            ApplyKnownBoxBlur(pixels, width, height);

        return pixels;
    }

    internal static async Task WriteImageAsync(
        string path,
        uint width,
        uint height,
        byte[] pixels,
        double dpi = 96,
        ushort orientation = 1,
        bool jpeg = false)
    {
        jpeg |= orientation != 1;
        var encoderId = jpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId;
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            width,
            height,
            dpi,
            dpi,
            pixels);
        if (orientation != 1)
        {
            await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
            {
                ["System.Photo.Orientation"] = new BitmapTypedValue(orientation, PropertyType.UInt16)
            });
        }

        await encoder.FlushAsync();
        stream.Seek(0);
        using var output = File.Create(path);
        stream.AsStreamForRead().CopyTo(output);
    }

    internal static async Task<byte[]> ReadPngPixelsAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);
        return data.DetachPixelData();
    }

    private static (int R, int G, int B) Noisy(
        Random random, int red, int green, int blue, int amplitude) =>
        (Clamp(red + random.Next(-amplitude, amplitude + 1)),
         Clamp(green + random.Next(-amplitude, amplitude + 1)),
         Clamp(blue + random.Next(-amplitude, amplitude + 1)));

    private static int Scale(uint value, uint maximum) =>
        maximum == 0 ? 0 : (int)(255L * value / maximum);

    private static int Clamp(int value) => Math.Clamp(value, 0, 255);

    private static byte Premultiply(int value, byte alpha) =>
        (byte)((value * alpha + 127) / 255);

    private static void ApplyKnownBoxBlur(byte[] pixels, uint width, uint height)
    {
        var sharp = new byte[pixels.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var bright = (x / 3 + y / 3) % 2 == 0;
                var index = checked((int)(((long)y * width + x) * 4));
                var value = (byte)(bright ? 220 : 28);
                sharp[index] = sharp[index + 1] = sharp[index + 2] = value;
                sharp[index + 3] = 255;
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var blueSum = 0;
                var greenSum = 0;
                var redSum = 0;
                var count = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var sampleX = Math.Clamp((int)x + dx, 0, (int)width - 1);
                        var sampleY = Math.Clamp((int)y + dy, 0, (int)height - 1);
                        var sample = checked((sampleY * (int)width + sampleX) * 4);
                        blueSum += sharp[sample];
                        greenSum += sharp[sample + 1];
                        redSum += sharp[sample + 2];
                        count++;
                    }
                }

                var target = checked((int)(((long)y * width + x) * 4));
                pixels[target] = (byte)((blueSum + count / 2) / count);
                pixels[target + 1] = (byte)((greenSum + count / 2) / count);
                pixels[target + 2] = (byte)((redSum + count / 2) / count);
                pixels[target + 3] = 255;
            }
        }
    }
}
