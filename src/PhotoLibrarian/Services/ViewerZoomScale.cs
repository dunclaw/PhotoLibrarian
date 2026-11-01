// Copyright (c) Duncan Lawler. All Rights Reserved.

namespace PhotoLibrarian.Services;

/// <summary>Viewer scale in DIPs per decoded image pixel, independent of XAML layout.</summary>
public sealed class ViewerZoomScale
{
    public const double MaxScale = 10;
    public double Scale { get; private set; } = 1;
    public bool IsFit { get; private set; } = true;
    public bool IsActualSize { get; private set; }

    public static double ActualSizeScale(double rasterizationScale)
    {
        if (!double.IsFinite(rasterizationScale) || rasterizationScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(rasterizationScale));
        return 1 / rasterizationScale;
    }

    public static double FitScale(int pixelWidth, int pixelHeight, double width, double height)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), "Image and viewport dimensions must be positive.");
        return Math.Clamp(Math.Min(width / pixelWidth, height / pixelHeight), 0.01, MaxScale);
    }

    public void Fit(double fitScale)
    {
        IsFit = true;
        IsActualSize = false;
        Scale = fitScale;
    }

    public void ActualSize(double rasterizationScale)
    {
        Scale = ActualSizeScale(rasterizationScale);
        IsFit = false;
        IsActualSize = true;
    }

    public void ZoomTo(double scale)
    {
        Scale = scale;
        IsFit = false;
        IsActualSize = false;
    }

    public double MinimumScale(double fitScale, double rasterizationScale) =>
        Math.Min(fitScale, ActualSizeScale(rasterizationScale));

    public void UpdateViewport(double fitScale, double rasterizationScale)
    {
        if (IsFit)
            Scale = fitScale;
        else if (IsActualSize)
            Scale = ActualSizeScale(rasterizationScale);
        else
            Scale = Math.Max(Scale, MinimumScale(fitScale, rasterizationScale));
    }
}
