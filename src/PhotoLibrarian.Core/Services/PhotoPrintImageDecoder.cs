// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Services;

public sealed record PhotoPrintImageData(byte[] Pixels, uint Width, uint Height);

public static class PhotoPrintImageDecoder
{
    public const uint MaximumPhotoDimension = 2048;

    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp" };

    public static bool IsSupported(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        SupportedExtensions.Contains(Path.GetExtension(path));

    public static async Task<PhotoPrintImageData> DecodeAsync(
        string path,
        uint maximumDimension,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!IsSupported(path))
            throw new NotSupportedException($"Printing is not supported for {Path.GetExtension(path)}.");
        if (maximumDimension == 0 || maximumDimension > MaximumPhotoDimension)
            throw new ArgumentOutOfRangeException(
                nameof(maximumDimension),
                $"The maximum dimension must be between 1 and {MaximumPhotoDimension} pixels.");

        var decoded = await WindowsImagingCodec.DecodeAsync(path, maximumDimension, cancellationToken);
        return new PhotoPrintImageData(decoded.Pixels, decoded.Width, decoded.Height);
    }
}
