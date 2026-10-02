namespace PhotoLibrarian.Services;

/// <summary>
/// Pulls a dragged slider value onto its neutral position when it comes within a small distance,
/// so adjustments are easy to return to "no change".
/// </summary>
public static class SliderSnap
{
    /// <summary>Fraction of the slider range that snaps to neutral.</summary>
    public const double DefaultRangeFraction = 0.02;

    public static double Apply(double value, double neutral, double threshold) =>
        Math.Abs(value - neutral) <= threshold ? neutral : value;

    public static double ThresholdFor(double minimum, double maximum, double rangeFraction = DefaultRangeFraction) =>
        Math.Abs(maximum - minimum) * rangeFraction;
}
