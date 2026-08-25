namespace PhotoLibrarian.Core.Services;

public static class StraightenGeometry
{
    public static double GetGuideCorrection(
        double deltaX,
        double deltaY,
        double maximumCorrectionDegrees = 45)
    {
        if (deltaX == 0 && deltaY == 0)
            throw new ArgumentException("The guide must have a non-zero length.");
        if (maximumCorrectionDegrees <= 0 || maximumCorrectionDegrees > 90)
            throw new ArgumentOutOfRangeException(nameof(maximumCorrectionDegrees));

        var guideAngle = Math.Atan2(deltaY, deltaX) * 180 / Math.PI;
        while (guideAngle >= 90)
            guideAngle -= 180;
        while (guideAngle < -90)
            guideAngle += 180;

        return Math.Clamp(-guideAngle, -maximumCorrectionDegrees, maximumCorrectionDegrees);
    }
}
