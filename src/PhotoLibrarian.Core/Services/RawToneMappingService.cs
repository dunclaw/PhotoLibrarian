// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Full-resolution, monitor-independent SDR reference. Input is straight FP32 linear
/// LibRaw ProPhoto RGB (D65), already camera-white-balanced and oriented by the RAW decoder.
/// Exposure is in stops; the scene white point maps to SDR white with a global shoulder.
/// The inverse of LibRaw 0.22.3's output_color=4 matrix converts to linear sRGB D65.
/// Gamut clipping follows the shoulder; no additional white-point adaptation is applied.
/// No sensor highlight reconstruction, auto exposure, display adaptation, or raster edits.
/// </summary>
public static class RawToneMappingService
{
    public const double DefaultWhitePoint = 4;
    public const string OutputColorSpace = "Linear sRGB D65";
    public const string OutputTransfer = "IEC 61966-2-1 sRGB";
    public const string RecipeVersion = "GlobalLuminanceV1";

    public static bool IsRaw(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".cr2" or ".cr3" or ".nef" or ".arw" or ".dng" or ".orf" or ".rw2";

    public static Task<RawSdrImage> RenderAsync(
        RawDevelopedImage source, EditParameters parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return RenderAsync(source.RgbValues, checked((int)source.Metadata.OutputWidth),
            checked((int)source.Metadata.OutputHeight), parameters, cancellationToken);
    }

    public static Task<RawSdrImage> RenderAsync(
        ReadOnlyMemory<float> linearProPhotoRgb, int width, int height,
        EditParameters parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ValidateRecipe(parameters);
        if (width <= 0 || height <= 0 ||
            linearProPhotoRgb.Length % 3 != 0 || (long)width * height != linearProPhotoRgb.Length / 3)
            throw new ArgumentException("RAW render dimensions must be positive and match the RGB sample buffer.");

        var exposureEv = parameters.RawExposureEv;
        var whitePoint = parameters.RawWhitePoint;
        return Task.Run(() => Render(linearProPhotoRgb, width, height, exposureEv, whitePoint,
            cancellationToken), cancellationToken);
    }

    public static void ValidateRecipe(EditParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!double.IsFinite(parameters.RawExposureEv) || parameters.RawExposureEv is < -8 or > 8)
            throw new ArgumentOutOfRangeException(nameof(parameters), "RAW exposure must be finite and between -8 and +8 stops.");
        if (!double.IsFinite(parameters.RawWhitePoint) || parameters.RawWhitePoint is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(parameters), "RAW scene white point must be finite and between 1 and 16.");
    }

    /// <summary>Monotonically increasing until the explicit white point, then bounded at one.</summary>
    internal static double Shoulder(double luminance, double whitePoint) =>
        luminance >= whitePoint ? 1 : luminance * (1 + luminance / (whitePoint * whitePoint)) / (1 + luminance);

    private static RawSdrImage Render(ReadOnlyMemory<float> source, int width, int height,
        double exposureEv, double whitePoint, CancellationToken cancellationToken)
    {
        var input = source.Span;
        float[] output;
        try { output = new float[input.Length]; }
        catch (OutOfMemoryException)
        {
            throw new InvalidOperationException("Not enough memory to tone-map this RAW photo. Close other photos or applications and retry.");
        }
        var exposureScale = Math.Pow(2, exposureEv);
        for (var i = 0; i < input.Length; i += 3)
        {
            if (i % 12288 == 0) cancellationToken.ThrowIfCancellationRequested();
            double red = input[i], green = input[i + 1], blue = input[i + 2];
            if (!double.IsFinite(red) || !double.IsFinite(green) || !double.IsFinite(blue))
                throw new InvalidDataException($"The developed RAW contains nonfinite samples at pixel {i / 3}.");

            var luminance = 0.26836494690413276 * red +
                0.71510893400282061 * green + 0.016525610495629867 * blue;
            if (luminance <= 0) continue;
            var mapped = Shoulder(luminance * exposureScale, whitePoint);
            // Normalize before multiplying to avoid overflow for extreme finite FP32 inputs.
            red = red / luminance * mapped;
            green = green / luminance * mapped;
            blue = blue / luminance * mapped;
            output[i] = (float)Math.Clamp(
                2.0341926072954801 * red - 0.72741981962025604 * green - 0.30676553702329973 * blue, 0, 1);
            output[i + 1] = (float)Math.Clamp(
                -0.2288107628343688 * red + 1.231729226050609 * green - 0.0029216160285968689 * blue, 0, 1);
            output[i + 2] = (float)Math.Clamp(
                -0.0085649272363156857 * red - 0.15327259100038765 * green + 1.1618389609260644 * blue, 0, 1);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new RawSdrImage(width, height, output);
    }
}

/// <summary>
/// Reusable source-resolution output for derivative export. Opaque, oriented FP32 linear
/// sRGB, bounded to [0,1]. Quantization happens only at the final preview upload boundary.
/// </summary>
public sealed class RawSdrImage
{
    internal RawSdrImage(int width, int height, float[] linearRgb)
    {
        Width = width;
        Height = height;
        LinearRgb = linearRgb;
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<float> LinearRgb { get; }

    public byte[] ToPreviewBgra8(CancellationToken cancellationToken = default)
    {
        var input = LinearRgb.Span;
        var byteCount = (long)LinearRgb.Length / 3 * 4;
        if (byteCount > Array.MaxLength)
            throw new InvalidOperationException("The SDR byte buffer cannot be represented by this runtime. No megapixel cap is applied.");
        byte[] output;
        try { output = new byte[(int)byteCount]; }
        catch (OutOfMemoryException)
        {
            throw new InvalidOperationException("Not enough memory for the SDR preview or export. Close other photos or applications and retry.");
        }
        for (var i = 0; i < Width * Height; i++)
        {
            if (i % 4096 == 0) cancellationToken.ThrowIfCancellationRequested();
            output[i * 4] = Encode(input[i * 3 + 2]);
            output[i * 4 + 1] = Encode(input[i * 3 + 1]);
            output[i * 4 + 2] = Encode(input[i * 3]);
            output[i * 4 + 3] = 255;
        }
        return output;
    }

    private static byte Encode(double value)
    {
        var encoded = value <= 0.0031308 ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp(Math.Round(encoded * 255, MidpointRounding.AwayFromZero), 0, 255);
    }
}
