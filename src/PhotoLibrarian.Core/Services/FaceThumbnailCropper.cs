using PhotoLibrarian.Core.Models;

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
            var image = await WindowsImagingCodec.DecodeAsync(
                imagePath, RawDecodeMaximumDimension, cancellationToken);
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var faceWidth = Math.Max(1, face.Width * image.Width);
                var faceHeight = Math.Max(1, face.Height * image.Height);
                var cropSize = Math.Max(faceWidth, faceHeight) * 1.6;
                var centerX = (face.X + face.Width / 2) * image.Width;
                var centerY = (face.Y + face.Height / 2) * image.Height;
                var left = (int)Math.Floor(centerX - cropSize / 2);
                var top = (int)Math.Floor(centerY - cropSize / 2);
                var right = (int)Math.Ceiling(centerX + cropSize / 2);
                var bottom = (int)Math.Ceiling(centerY + cropSize / 2);

                left = Math.Clamp(left, 0, (int)image.Width - 1);
                top = Math.Clamp(top, 0, (int)image.Height - 1);
                right = Math.Clamp(right, left + 1, (int)image.Width);
                bottom = Math.Clamp(bottom, top + 1, (int)image.Height);
                var crop = new CropRectangle((uint)left, (uint)top, (uint)(right - left), (uint)(bottom - top));
                var cropped = WindowsImagingCodec.Crop(image.Pixels, image.Width, image.Height, crop);
                var resized = WindowsImagingCodec.ResizeBicubic(
                    cropped, crop.Width, crop.Height, (uint)size, (uint)size);
                return WindowsImagingCodec.EncodeAsync(
                    $"{Path.GetFileNameWithoutExtension(imagePath)}.png",
                    resized, (uint)size, (uint)size, cancellationToken).GetAwaiter().GetResult();
            }, cancellationToken);
        }
        finally
        {
            DecodeGate.Release();
        }
    }

}
