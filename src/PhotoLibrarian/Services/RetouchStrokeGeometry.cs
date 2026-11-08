// Copyright (c) Duncan Lawler. All Rights Reserved.
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using PhotoLibrarian.Core.Models;
using System.Numerics;

namespace PhotoLibrarian.Services;

internal static class RetouchStrokeGeometry
{
    internal static CanvasGeometry Create(ICanvasResourceCreator device, IReadOnlyList<RetouchPoint> points, int radius)
    {
        if (points.Count == 0) throw new ArgumentException("Expected a nonempty brush stroke.", nameof(points));
        if (points.Count == 1)
            return CanvasGeometry.CreateCircle(device, points[0].X + 0.5f, points[0].Y + 0.5f, radius);
        using var path = new CanvasPathBuilder(device);
        path.BeginFigure(new Vector2(points[0].X + 0.5f, points[0].Y + 0.5f));
        for (var i = 1; i < points.Count; i++)
            path.AddLine(new Vector2(points[i].X + 0.5f, points[i].Y + 0.5f));
        path.EndFigure(CanvasFigureLoop.Open);
        using var centerline = CanvasGeometry.CreatePath(path);
        using var style = new CanvasStrokeStyle
        {
            StartCap = CanvasCapStyle.Round,
            EndCap = CanvasCapStyle.Round,
            LineJoin = CanvasLineJoin.Round
        };
        return centerline.Stroke(radius * 2, style);
    }
}
