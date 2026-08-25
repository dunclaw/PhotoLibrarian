namespace PhotoLibrarian.Core.Services;

/// <summary>Pure geometry used by straighten preview and rendering.</summary>
public static class AutoCropGeometry
{
    public readonly record struct CropSize(double Width, double Height);

    /// <summary>
    /// Returns the largest centered axis-aligned rectangle contained by a rotated rectangle.
    /// </summary>
    public static CropSize GetLargestRectangle(
        double sourceWidth,
        double sourceHeight,
        double rotationDegrees)
    {
        if (sourceWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceHeight));

        var angle = Math.Abs(rotationDegrees % 180) * Math.PI / 180;
        if (angle > Math.PI / 2)
            angle = Math.PI - angle;

        var sin = Math.Abs(Math.Sin(angle));
        var cos = Math.Abs(Math.Cos(angle));
        if (sin < 1e-12)
            return new CropSize(sourceWidth, sourceHeight);

        var widthIsLonger = sourceWidth >= sourceHeight;
        var longer = widthIsLonger ? sourceWidth : sourceHeight;
        var shorter = widthIsLonger ? sourceHeight : sourceWidth;

        double cropWidth;
        double cropHeight;

        if (shorter <= 2 * sin * cos * longer || Math.Abs(sin - cos) < 1e-12)
        {
            var halfShorter = 0.5 * shorter;
            if (widthIsLonger)
            {
                cropWidth = halfShorter / sin;
                cropHeight = halfShorter / cos;
            }
            else
            {
                cropWidth = halfShorter / cos;
                cropHeight = halfShorter / sin;
            }
        }
        else
        {
            var cosDoubleAngle = (cos * cos) - (sin * sin);
            cropWidth = ((sourceWidth * cos) - (sourceHeight * sin)) / cosDoubleAngle;
            cropHeight = ((sourceHeight * cos) - (sourceWidth * sin)) / cosDoubleAngle;
        }

        return new CropSize(
            Math.Clamp(cropWidth, 0, sourceWidth),
            Math.Clamp(cropHeight, 0, sourceHeight));
    }

    /// <summary>
    /// Returns the scale needed for a rotated source to cover its original frame without corners.
    /// </summary>
    public static double GetCoverScale(
        double sourceWidth,
        double sourceHeight,
        double rotationDegrees)
    {
        var crop = GetLargestRectangle(sourceWidth, sourceHeight, rotationDegrees);
        return Math.Max(sourceWidth / crop.Width, sourceHeight / crop.Height);
    }
}
