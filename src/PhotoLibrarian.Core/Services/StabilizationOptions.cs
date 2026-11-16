// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Services;

/// <summary>How strongly camera motion is smoothed.</summary>
public enum StabilizationSmoothness
{
    /// <summary>Removes jitter but follows camera moves closely.</summary>
    Low,
    /// <summary>Balanced default.</summary>
    Medium,
    /// <summary>Very gradual camera moves; uses more of the crop margin.</summary>
    High,
}

/// <summary>User-selectable stabilization settings.</summary>
/// <param name="AutoCrop">Zoom in slightly so no invalid edge pixels are shown.</param>
/// <param name="Smoothness">Smoothing strength when <paramref name="LockFrame"/> is off.</param>
/// <param name="CorrectRotation">Also correct camera roll, not only horizontal and vertical shake.</param>
/// <param name="LockFrame">
/// Hold the framing still like a tripod instead of smoothing the camera path. When the camera
/// drifts further than the correction margin allows, the framing follows gradually.
/// </param>
public sealed record StabilizationOptions(
    bool AutoCrop = true,
    StabilizationSmoothness Smoothness = StabilizationSmoothness.Medium,
    bool CorrectRotation = true,
    bool LockFrame = false)
{
    public static StabilizationOptions Default { get; } = new();

    /// <summary>Gaussian smoothing width as a fraction of the frame rate (seconds).</summary>
    internal double SmoothingSeconds => Smoothness switch
    {
        StabilizationSmoothness.Low => 0.25,
        StabilizationSmoothness.High => 1.0,
        _ => 0.5,
    };
}
