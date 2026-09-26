namespace PhotoLibrarian.Core.Services;

/// <summary>Pixel bounds selected by the user for red-eye removal.</summary>
public readonly record struct RedEyeBounds(int Left, int Top, int Width, int Height);

/// <summary>
/// Removes red-eye from a BGRA8 pixel buffer without changing pixels outside the selection.
/// Red-eye candidates are red-dominant, sufficiently bright pupil pixels; neutral pixels are
/// deliberately left untouched to avoid desaturating skin or nearby highlights.
/// </summary>
public static class RedEyeRemovalProcessor
{
    public static int Apply(byte[] pixels, int imageWidth, int imageHeight, RedEyeBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth));
        if (pixels.Length < imageWidth * imageHeight * 4)
            throw new ArgumentException("The pixel buffer is smaller than the image.", nameof(pixels));

        var left = Math.Clamp(bounds.Left, 0, imageWidth);
        var top = Math.Clamp(bounds.Top, 0, imageHeight);
        var right = Math.Clamp((long)bounds.Left + bounds.Width, 0, imageWidth);
        var bottom = Math.Clamp((long)bounds.Top + bounds.Height, 0, imageHeight);
        if (right <= left || bottom <= top) return 0;

        var changed = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var offset = (y * imageWidth + x) * 4;
                var blue = pixels[offset];
                var green = pixels[offset + 1];
                var red = pixels[offset + 2];

                if (red < 55 || red <= green * 1.35f || red <= blue * 1.25f)
                    continue;

                pixels[offset + 2] = (byte)((green + blue) / 2);
                changed++;
            }
        }

        return changed;
    }
}
