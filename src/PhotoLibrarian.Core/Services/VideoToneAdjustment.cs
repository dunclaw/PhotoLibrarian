// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Services;

internal static class VideoToneAdjustment
{
    internal static void ApplyBrightnessContrastNv12(
        byte[] frame, int width, int height, uint nominalRange, int brightness, int contrast)
    {
        if (width <= 0 || (width & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width), "NV12 frame width must be positive and even.");
        if (height <= 0 || (height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(height), "NV12 frame height must be positive and even.");

        int lumaLength = checked(width * height);
        int expectedLength = checked(lumaLength + lumaLength / 2);
        if (frame.Length != expectedLength)
            throw new InvalidDataException("Windows returned an incomplete NV12 video frame.");

        if (brightness is < -100 or > 100)
            throw new ArgumentOutOfRangeException(nameof(brightness));
        if (contrast is < 0 or > 200)
            throw new ArgumentOutOfRangeException(nameof(contrast));
        if (brightness == 0 && contrast == 100)
            return;

        (int minimum, int maximum) = nominalRange switch
        {
            1 => (0, 255),
            2 => (16, 235),
            3 => (48, 208),
            4 => (64, 127),
            _ => throw new NotSupportedException(
                "Windows did not report a supported NV12 nominal range for brightness and contrast.")
        };
        int midpointTwice = minimum + maximum;
        int lumaRange = maximum - minimum;

        for (int index = 0; index < lumaLength; index++)
        {
            int numerator = midpointTwice * 100 +
                (frame[index] * 2 - midpointTwice) * contrast + brightness * (lumaRange * 2);
            int adjusted = numerator >= 0
                ? (numerator + 100) / 200
                : (numerator - 100) / 200;
            frame[index] = (byte)Math.Clamp(adjusted, minimum, maximum);
        }
    }
}
