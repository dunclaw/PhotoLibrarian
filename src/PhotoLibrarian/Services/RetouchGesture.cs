// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Services;

/// <summary>Transient gesture state. Only Complete produces a recipe; cancellation never produces an edit.</summary>
internal sealed class RetouchGesture
{
    private readonly List<RetouchPoint> _points = [];
    private int _width;
    private int _height;
    public uint? PointerId { get; private set; }
    public int Radius { get; private set; }
    public IReadOnlyList<RetouchPoint> Points => _points;

    public void Begin(uint pointerId, int width, int height, int radius, RetouchPoint point)
    {
        Cancel();
        _width = width;
        _height = height;
        Radius = radius;
        PointerId = pointerId;
        Add(point);
    }

    public bool Add(RetouchPoint point)
    {
        if (PointerId is null) return false;
        if (_points.Count > 0 && _points[^1] == point) return true;
        if (point.X < 0 || point.Y < 0 ||
            point.X >= _width || point.Y >= _height)
        {
            Cancel();
            return false;
        }
        _points.Add(point);
        return true;
    }

    public RetouchStroke? Complete(uint pointerId)
    {
        if (PointerId != pointerId) return null;
        var stroke = new RetouchStroke(_width, _height, Radius, _points.ToArray());
        Cancel();
        return stroke;
    }

    public void Cancel()
    {
        PointerId = null;
        _points.Clear();
    }
}
