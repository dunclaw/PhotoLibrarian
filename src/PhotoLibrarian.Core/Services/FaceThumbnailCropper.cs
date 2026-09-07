using PhotoLibrarian.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PhotoLibrarian.Core.Services;

public static class FaceThumbnailCropper
{
    private const int RawDecodeMaximumDimension = 2048;
    private static readonly SemaphoreSlim DecodeGate = new(2, 2);

    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".arw", ".cr2", ".cr3", ".dng", ".nef", ".orf", ".rw2"
    };

    public static async Task<byte[]> CreateAsync(
        string imagePath,
        FaceRegion face,
        int size,
        CancellationToken cancellationToken = default)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));

        await DecodeGate.WaitAsync(cancellationToken);
        try
        {
            using var image = await LoadImageAsync(imagePath, cancellationToken);
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                image.Mutate(context => context.AutoOrient());

                var faceWidth = Math.Max(1, face.Width * image.Width);
                var faceHeight = Math.Max(1, face.Height * image.Height);
                var cropSize = Math.Max(faceWidth, faceHeight) * 1.6;
                var centerX = (face.X + face.Width / 2) * image.Width;
                var centerY = (face.Y + face.Height / 2) * image.Height;
                var left = (int)Math.Floor(centerX - cropSize / 2);
                var top = (int)Math.Floor(centerY - cropSize / 2);
                var right = (int)Math.Ceiling(centerX + cropSize / 2);
                var bottom = (int)Math.Ceiling(centerY + cropSize / 2);

                left = Math.Clamp(left, 0, image.Width - 1);
                top = Math.Clamp(top, 0, image.Height - 1);
                right = Math.Clamp(right, left + 1, image.Width);
                bottom = Math.Clamp(bottom, top + 1, image.Height);

                image.Mutate(context => context
                    .Crop(new Rectangle(left, top, right - left, bottom - top))
                    .Resize(new ResizeOptions
                    {
                        Size = new Size(size, size),
                        Mode = ResizeMode.Crop,
                        Sampler = KnownResamplers.Bicubic
                    }));

                using var output = new MemoryStream();
                image.Save(output, new PngEncoder());
                return output.ToArray();
            }, cancellationToken);
        }
        finally
        {
            DecodeGate.Release();
        }
    }

    private static async Task<Image<Bgra32>> LoadImageAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        if (RawExtensions.Contains(Path.GetExtension(imagePath)))
        {
            return await LoadWithWindowsImagingAsync(imagePath, cancellationToken);
        }

        try
        {
            return await Task.Run(
                () => Image.Load<Bgra32>(imagePath),
                cancellationToken);
        }
        catch (UnknownImageFormatException)
        {
            return await LoadWithWindowsImagingAsync(imagePath, cancellationToken);
        }
    }

    private static async Task<Image<Bgra32>> LoadWithWindowsImagingAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(imagePath);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream())
            .AsTask(cancellationToken);
        var scale = Math.Min(
            1.0,
            (double)RawDecodeMaximumDimension /
            Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb)
            .AsTask(cancellationToken);
        var byteCount = checked((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
        var buffer = new Windows.Storage.Streams.Buffer(byteCount);
        bitmap.CopyToBuffer(buffer);
        var pixels = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(pixels);
        return Image.LoadPixelData<Bgra32>(
            pixels,
            bitmap.PixelWidth,
            bitmap.PixelHeight);
    }
}
