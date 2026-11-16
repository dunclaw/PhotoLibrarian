// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Text.Json.Serialization;

namespace PhotoLibrarian.Core.Models;

public readonly record struct ToneCurvePoint(
    [property: JsonRequired] double X,
    [property: JsonRequired] double Y);

/// <summary>Four monotone cubic Hermite splines in normalized display-referred RGB.</summary>
public sealed class ToneCurves
{
    [JsonRequired] public List<ToneCurvePoint> Rgb { get; set; } = Identity();
    [JsonRequired] public List<ToneCurvePoint> Red { get; set; } = Identity();
    [JsonRequired] public List<ToneCurvePoint> Green { get; set; } = Identity();
    [JsonRequired] public List<ToneCurvePoint> Blue { get; set; } = Identity();

    public static List<ToneCurvePoint> Identity() => [new(0, 0), new(1, 1)];

    [JsonIgnore]
    public bool IsIdentity => IsIdentityCurve(Rgb) && IsIdentityCurve(Red) &&
        IsIdentityCurve(Green) && IsIdentityCurve(Blue);

    [JsonIgnore]
    public bool IsDefault => IsIdentity && Rgb.Count == 2 && Red.Count == 2 &&
        Green.Count == 2 && Blue.Count == 2;

    private static bool IsIdentityCurve(IReadOnlyList<ToneCurvePoint> points) =>
        points.All(point => point.X == point.Y);

    public ToneCurves Clone() => new()
    {
        Rgb = [.. Rgb], Red = [.. Red], Green = [.. Green], Blue = [.. Blue]
    };

    public bool ContentEquals(ToneCurves other) =>
        Rgb.SequenceEqual(other.Rgb) && Red.SequenceEqual(other.Red) &&
        Green.SequenceEqual(other.Green) && Blue.SequenceEqual(other.Blue);

    public void Validate()
    {
        ValidateCurve(Rgb);
        ValidateCurve(Red);
        ValidateCurve(Green);
        ValidateCurve(Blue);
    }

    private static void ValidateCurve(IReadOnlyList<ToneCurvePoint>? points)
    {
        if (points is null || points.Count < 2 || points[0].X != 0 || points[^1].X != 1)
            throw new InvalidDataException("A tone curve must span the full input range.");
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                point.X < 0 || point.X > 1 || point.Y < 0 || point.Y > 1 ||
                (i > 0 && (point.X <= points[i - 1].X || point.Y < points[i - 1].Y)))
                throw new InvalidDataException("Tone curve points must be ordered and monotone.");
        }
    }

    public static double Evaluate(IReadOnlyList<ToneCurvePoint> points, double x)
    {
        x = Math.Clamp(x, 0, 1);
        var index = 1;
        while (index < points.Count - 1 && x > points[index].X) index++;
        var left = points[index - 1];
        var right = points[index];
        var width = right.X - left.X;
        var t = (x - left.X) / width;
        var rise = right.Y - left.Y;
        if (rise == 0) return left.Y;

        var leftTangent = index == 1 ? rise : ScaledTangent(points, index - 1, width);
        var rightTangent = index == points.Count - 1 ? rise : ScaledTangent(points, index, width);
        var t2 = t * t;
        var t3 = t2 * t;
        var value = left.Y + rise * (3 * t2 - 2 * t3) +
            leftTangent * (t3 - 2 * t2 + t) + rightTangent * (t3 - t2);
        return Math.Clamp(value, left.Y, right.Y);
    }

    private static double ScaledTangent(IReadOnlyList<ToneCurvePoint> points, int index, double width)
    {
        var leftRise = points[index].Y - points[index - 1].Y;
        var rightRise = points[index + 1].Y - points[index].Y;
        if (leftRise == 0 || rightRise == 0) return 0;

        // The harmonic mean limits each knot slope to twice either neighboring secant,
        // preserving monotonicity without corners. Scale before dividing to avoid huge slopes
        // when input coordinates are very close together.
        var leftWidth = (points[index].X - points[index - 1].X) / width;
        var rightWidth = (points[index + 1].X - points[index].X) / width;
        return 2 / (leftWidth / leftRise + rightWidth / rightRise);
    }
}
