// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Numerics;

namespace PhotoLibrarian.Services;

/// <summary>Unpublished parameter contract for independently testing the final-output effects.</summary>
internal sealed record PostCropEffectSettings
{
    internal double VignetteAmount { get; init; }
    internal double Feather { get; init; } = 0.5;
    internal double Roundness { get; init; }
    internal double HighlightPriority { get; init; } = 0.5;
    internal double GrainAmount { get; init; }
    internal double GrainSize { get; init; } = 0.5;
    internal double Roughness { get; init; } = 0.5;
    internal uint Seed { get; init; } = 194;

    internal bool TryValidate(out string error)
    {
        if (!InRange(VignetteAmount, -1, 1) || !InRange(Feather, 0, 1) ||
            !InRange(Roundness, 0, 1) || !InRange(HighlightPriority, 0, 1) ||
            !InRange(GrainAmount, 0, 1) || !InRange(GrainSize, 0, 1) || !InRange(Roughness, 0, 1))
        {
            error = "Vignette amount must be in [-1, 1]; all other effect values must be in [0, 1] and finite.";
            return false;
        }
        error = "";
        return true;
    }

    private static bool InRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;
}

/// <summary>
/// Independent CPU reference, not a live rendering path. Coordinates and pixel footprints
/// are in final cropped output pixels, never viewport or tile-local coordinates.
/// </summary>
internal static class PostCropEffectReference
{
    internal static bool TryEvaluate(Vector4 premultiplied, PostCropEffectSettings settings,
        double outputWidth, double outputHeight, double centerX, double centerY,
        double footprintWidth, double footprintHeight, out Vector4 result, out string error,
        CancellationToken cancellationToken = default)
    {
        result = default;
        if (!settings.TryValidate(out error)) return false;
        if (cancellationToken.IsCancellationRequested)
        {
            error = "Effect evaluation was cancelled.";
            return false;
        }
        if (!PositiveFinite(outputWidth) || !PositiveFinite(outputHeight) ||
            !PositiveFinite(footprintWidth) || !PositiveFinite(footprintHeight) ||
            !double.IsFinite(centerX) || !double.IsFinite(centerY) ||
            centerX < 0 || centerX > outputWidth || centerY < 0 || centerY > outputHeight)
        {
            error = "Output dimensions, global sample coordinates and pixel footprints are invalid.";
            return false;
        }
        if (!Unit(premultiplied.W) || !Channel(premultiplied.X, premultiplied.W) ||
            !Channel(premultiplied.Y, premultiplied.W) || !Channel(premultiplied.Z, premultiplied.W))
        {
            error = "Input must be finite normalized premultiplied RGBA.";
            return false;
        }
        if (premultiplied.W == 0 || (settings.VignetteAmount == 0 && settings.GrainAmount == 0))
        {
            result = premultiplied;
            return true;
        }

        var color = new Vector3(premultiplied.X, premultiplied.Y, premultiplied.Z) / premultiplied.W;
        var luminance = Vector3.Dot(color, new Vector3(0.2126f, 0.7152f, 0.0722f));
        var shortSide = Math.Min(outputWidth, outputHeight);
        var axisX = (outputWidth * (1 - settings.Roundness) + shortSide * settings.Roundness) / 2;
        var axisY = (outputHeight * (1 - settings.Roundness) + shortSide * settings.Roundness) / 2;
        if (!PositiveFinite(axisX) || !PositiveFinite(axisY))
        {
            error = "The vignette axes cannot be represented by the reference geometry.";
            return false;
        }
        var x = (centerX - outputWidth / 2) / axisX;
        var y = (centerY - outputHeight / 2) / axisY;
        var radius = Math.Sqrt(x * x + y * y) / Math.Sqrt(2);
        var mask = SmoothStep(1 - 0.9 * settings.Feather, 1, radius);
        if (settings.VignetteAmount < 0)
        {
            var protection = 1 - settings.HighlightPriority * SmoothStep(0.6, 1, luminance);
            color *= (float)(1 + 0.8 * settings.VignetteAmount * mask * protection);
        }
        else
        {
            color += (Vector3.One - color) * (float)(0.8 * settings.VignetteAmount * mask);
        }

        if (settings.GrainAmount > 0)
        {
            var cell = shortSide * (0.0005 + 0.0095 * settings.GrainSize);
            if (!TryAverageNoise(centerX / cell, centerY / cell,
                footprintWidth / cell, footprintHeight / cell, settings.Seed,
                cancellationToken, out var coarse, out error))
                return false;
            double fine = 0;
            if (settings.Roughness > 0 &&
                !TryAverageNoise(centerX / cell * 2, centerY / cell * 2,
                    footprintWidth / cell * 2, footprintHeight / cell * 2, settings.Seed ^ 0x9e3779b9,
                    cancellationToken, out fine, out error))
                return false;
            var noise = (coarse + settings.Roughness * 0.5 * fine) / (1 + settings.Roughness * 0.5);
            color += new Vector3((float)(settings.GrainAmount * 0.07 * noise));
        }
        color = Vector3.Clamp(color, Vector3.Zero, Vector3.One);
        result = new Vector4(color * premultiplied.W, premultiplied.W);
        return true;
    }

    // Exact box average of bilinear lattice noise. Integrating tent weights makes preview
    // downsampling deterministic and rejects subpixel noise rather than point-sampling it.
    private static bool TryAverageNoise(double x, double y, double width, double height, uint seed,
        CancellationToken cancellationToken, out double average, out string error)
    {
        average = 0;
        error = "";
        var left = x - width / 2;
        var right = x + width / 2;
        var top = y - height / 2;
        var bottom = y + height / 2;
        // These are arithmetic representability checks, not an image-size or sample-count cap.
        if (!PositiveFinite(width) || !PositiveFinite(height) ||
            !LatticeCoordinate(left) || !LatticeCoordinate(right) ||
            !LatticeCoordinate(top) || !LatticeCoordinate(bottom) ||
            right <= left || bottom <= top)
        {
            error = "The grain sampling footprint cannot be represented by the reference lattice.";
            return false;
        }
        double sum = 0;
        for (var row = (long)Math.Floor(top); row <= (long)Math.Ceiling(bottom); row++)
        {
            var wy = TentIntegral(bottom - row) - TentIntegral(top - row);
            if (wy == 0) continue;
            for (var column = (long)Math.Floor(left); column <= (long)Math.Ceiling(right); column++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    error = "Effect evaluation was cancelled.";
                    return false;
                }
                var wx = TentIntegral(right - column) - TentIntegral(left - column);
                sum += HashNoise(column, row, seed) * (wx / width) * (wy / height);
            }
        }
        average = sum;
        return true;
    }

    private static double TentIntegral(double x) => x switch
    {
        <= -1 => 0,
        < 0 => (x + 1) * (x + 1) / 2,
        < 1 => 1 - (1 - x) * (1 - x) / 2,
        _ => 1
    };

    private static double HashNoise(long x, long y, uint seed)
    {
        unchecked
        {
            uint hash = (uint)x * 0x8da6b343 ^ (uint)y * 0xd8163841 ^ seed;
            hash ^= hash >> 16;
            hash *= 0x7feb352d;
            hash ^= hash >> 15;
            hash *= 0x846ca68b;
            hash ^= hash >> 16;
            return hash / (double)uint.MaxValue * 2 - 1;
        }
    }

    private static double SmoothStep(double start, double end, double value)
    {
        if (start == end) return value >= end ? 1 : 0;
        var t = Math.Clamp((value - start) / (end - start), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static bool PositiveFinite(double value) => double.IsFinite(value) && value > 0;
    private static bool LatticeCoordinate(double value) =>
        double.IsFinite(value) && value > long.MinValue && value < long.MaxValue;
    private static bool Unit(float value) => float.IsFinite(value) && value is >= 0 and <= 1;
    private static bool Channel(float value, float alpha) => float.IsFinite(value) && value >= 0 && value <= alpha;
}
