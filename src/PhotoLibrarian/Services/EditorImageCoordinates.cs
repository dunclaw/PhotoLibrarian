// Copyright (c) Duncan Lawler. All Rights Reserved.
using System.Numerics;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Services;

internal static class EditorImageCoordinates
{
    internal static Matrix3x2 SourceToCanvas(Vector2 sourceSize, double angle, float scale, Vector2 origin)
    {
        var (offset, _, _) = EditEffectGraph.ComputeOutputExtent(sourceSize.X, sourceSize.Y, angle);
        return Matrix3x2.CreateRotation((float)(angle * Math.PI / 180), sourceSize / 2) *
            Matrix3x2.CreateTranslation(offset) * Matrix3x2.CreateScale(scale) *
            Matrix3x2.CreateTranslation(origin);
    }

    internal static bool TryGetSourcePoint(Vector2 canvasPoint, Vector2 sourceSize,
        double angle, float scale, Vector2 origin, out RetouchPoint point)
    {
        point = default;
        if (!Matrix3x2.Invert(SourceToCanvas(sourceSize, angle, scale, origin), out var inverse)) return false;
        var source = Vector2.Transform(canvasPoint, inverse);
        if (!float.IsFinite(source.X) || !float.IsFinite(source.Y) ||
            source.X < 0 || source.Y < 0 || source.X >= sourceSize.X || source.Y >= sourceSize.Y) return false;
        point = new RetouchPoint((int)Math.Floor(source.X), (int)Math.Floor(source.Y));
        return true;
    }
}
