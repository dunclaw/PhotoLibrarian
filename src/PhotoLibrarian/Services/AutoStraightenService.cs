using PhotoLibrarian.Core.Services;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PhotoLibrarian.Services;

/// <summary>Decodes a bounded, display-oriented image for the pure Core line analyzer.</summary>
public static class AutoStraightenService
{
    private const uint MaximumAnalysisDimension = 640;

    public static async Task<StraightenAnalysisResult> AnalyzeAsync(string filePath)
    {
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);

        var orientedWidth = decoder.OrientedPixelWidth;
        var orientedHeight = decoder.OrientedPixelHeight;
        var scale = Math.Min(
            1.0,
            MaximumAnalysisDimension / (double)Math.Max(orientedWidth, orientedHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(3, Math.Round(orientedWidth * scale)),
            ScaledHeight = (uint)Math.Max(3, Math.Round(orientedHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant
        };

        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        var pixelWidth = bitmap.PixelWidth;
        var pixelHeight = bitmap.PixelHeight;
        var byteCount = checked((uint)(pixelWidth * pixelHeight * 4));
        var buffer = new Windows.Storage.Streams.Buffer(byteCount) { Length = byteCount };
        bitmap.CopyToBuffer(buffer);
        CryptographicBuffer.CopyToByteArray(buffer, out var pixels);

        return await Task.Run(() => StraightenAnalyzer.AnalyzeBgra(
            pixels,
            pixelWidth,
            pixelHeight));
    }
}
