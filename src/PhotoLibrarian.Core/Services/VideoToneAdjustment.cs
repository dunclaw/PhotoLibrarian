// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Services;

internal static class VideoToneAdjustment
{
    internal static void ApplyBrightnessContrastNv12(
        byte[] frame, int width, int height, int brightness, int contrast)
    {
        int lumaLength = checked(width * height);
        if (width <= 0 || height <= 0 || frame.Length < checked(lumaLength + lumaLength / 2))
            throw new InvalidDataException("Windows returned an incomplete NV12 video frame.");

        if (brightness is < -100 or > 100)
            throw new ArgumentOutOfRangeException(nameof(brightness));
        if (contrast is < 0 or > 200)
            throw new ArgumentOutOfRangeException(nameof(contrast));
        if (brightness == 0 && contrast == 100)
            return;

        for (int index = 0; index < lumaLength; index++)
        {
            // NV12 video luma is limited to 16-235; contrast pivots around its midpoint.
            int numerator = 25_100 + (frame[index] * 2 - 251) * contrast + brightness * 438;
            int adjusted = numerator >= 0
                ? (numerator + 100) / 200
                : (numerator - 100) / 200;
            frame[index] = (byte)Math.Clamp(adjusted, 16, 235);
        }
    }
}
