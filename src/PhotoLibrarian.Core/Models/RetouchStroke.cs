// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Models;

public readonly record struct RetouchPoint(int X, int Y);

/// <summary>Version 1 smooth-surface retouch in EXIF-oriented source pixels, before editor rotation.</summary>
public sealed record RetouchStroke(int ImageWidth, int ImageHeight, int Radius, RetouchPoint[] Points)
{
    public RetouchStroke Copy() => this with { Points = Points.ToArray() };

    public static bool SequenceEqual(IReadOnlyList<RetouchStroke> a, IReadOnlyList<RetouchStroke> b) =>
        a.Count == b.Count && a.Zip(b).All(pair =>
            pair.First.ImageWidth == pair.Second.ImageWidth &&
            pair.First.ImageHeight == pair.Second.ImageHeight &&
            pair.First.Radius == pair.Second.Radius &&
            pair.First.Points.SequenceEqual(pair.Second.Points));
}
