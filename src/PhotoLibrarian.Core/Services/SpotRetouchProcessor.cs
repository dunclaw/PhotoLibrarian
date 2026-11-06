// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Bounded planar inpainting for small blemishes on smooth, opaque surfaces.
/// Fits each color channel to a two-pixel ring outside the entire stroke, never to its interior.
/// Rejects non-planar surroundings rather than inventing texture or continuing strong edges.
/// </summary>
public static class SpotRetouchProcessor
{
    public const int MaxPoints = 128;
    public const int MaxRadius = 24;
    public const int MaxExtent = 192;
    public const int MaxMaskPixels = 8192;

    public static void Validate(IReadOnlyList<RetouchStroke> strokes)
    {
        foreach (var stroke in strokes)
        {
            if (stroke is null || stroke.ImageWidth <= 0 || stroke.ImageHeight <= 0 ||
                stroke.Radius is < 2 or > MaxRadius || stroke.Points is null ||
                stroke.Points.Length is < 1 or > MaxPoints)
                throw new InvalidDataException("Invalid or oversized retouch stroke.");
            foreach (var point in stroke.Points)
                if (point.X < 0 || point.Y < 0 || point.X >= stroke.ImageWidth || point.Y >= stroke.ImageHeight)
                    throw new InvalidDataException("Retouch point is outside its source image.");
        }
    }

    /// <summary>Applies validated recipes in order. Call on an owned buffer; a rejected stroke writes nothing.</summary>
    public static void Apply(byte[] pixels, int width, int height, IReadOnlyList<RetouchStroke> strokes,
        CancellationToken cancellationToken = default)
    {
        Validate(strokes);
        if (width <= 0 || height <= 0 || (long)width * height * 4 != pixels.LongLength)
            throw new ArgumentException("Expected a complete BGRA8 source buffer.", nameof(pixels));
        foreach (var stroke in strokes)
        {
            if (stroke.ImageWidth != width || stroke.ImageHeight != height)
                throw new InvalidDataException("Retouch recipe dimensions do not match the oriented source.");
            cancellationToken.ThrowIfCancellationRequested();
            ApplyStroke(pixels, width, height, stroke, cancellationToken);
        }
    }

    private static void ApplyStroke(byte[] pixels, int width, int height, RetouchStroke stroke,
        CancellationToken cancellationToken)
    {
        var r = stroke.Radius;
        var left = stroke.Points.Min(p => p.X) - r - 2;
        var top = stroke.Points.Min(p => p.Y) - r - 2;
        var right = stroke.Points.Max(p => p.X) + r + 2;
        var bottom = stroke.Points.Max(p => p.Y) + r + 2;
        if (left < 0 || top < 0 || right >= width || bottom >= height)
            throw new InvalidDataException("Retouch needs an intact surrounding ring. Move away from the image edge.");
        var w = right - left + 1;
        var h = bottom - top + 1;
        if (w > MaxExtent || h > MaxExtent)
            throw new InvalidDataException("Retouch stroke is too long. Use a shorter stroke.");

        var mask = new bool[w * h];
        var ring = new bool[w * h];
        // Distance to connected segments makes a continuous capsule even with sparse pointer events.
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (x == 0) cancellationToken.ThrowIfCancellationRequested();
            double distance = double.MaxValue;
            var px = x + left;
            var py = y + top;
            for (var i = 0; i < stroke.Points.Length; i++)
            {
                var a = stroke.Points[Math.Max(0, i - 1)];
                var b = stroke.Points[i];
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                var lengthSquared = (double)dx * dx + (double)dy * dy;
                var t = lengthSquared == 0 ? 0 : Math.Clamp(
                    ((px - a.X) * (double)dx + (py - a.Y) * (double)dy) / lengthSquared, 0, 1);
                var ex = px - a.X - t * dx;
                var ey = py - a.Y - t * dy;
                distance = Math.Min(distance, ex * ex + ey * ey);
            }
            mask[y * w + x] = distance <= r * r;
            ring[y * w + x] = distance > r * r && distance <= (r + 2) * (r + 2);
        }
        if (mask.Count(value => value) > MaxMaskPixels)
            throw new InvalidDataException("Retouch area is too large. Use a smaller brush or shorter stroke.");

        var samples = new List<(double X, double Y, int Offset)>();
        double meanX = 0, meanY = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var index = y * w + x;
            if (!mask[index] && !ring[index]) continue;
            var offset = ((top + y) * width + left + x) * 4;
            if (pixels[offset + 3] != 255)
                throw new InvalidDataException("Retouch requires an opaque selection and surrounding ring.");
            if (!ring[index]) continue;
            samples.Add((x, y, offset));
            meanX += x;
            meanY += y;
        }
        meanX /= samples.Count;
        meanY /= samples.Count;
        double xx = 0, xy = 0, yy = 0;
        foreach (var sample in samples)
        {
            var x = sample.X - meanX;
            var y = sample.Y - meanY;
            xx += x * x;
            xy += x * y;
            yy += y * y;
        }
        var determinant = xx * yy - xy * xy;
        if (determinant <= 0)
            throw new InvalidDataException("Retouch cannot fit this surrounding ring.");
        var planes = new (double Mean, double X, double Y)[3];
        for (var channel = 0; channel < 3; channel++)
        {
            double sum = 0, xc = 0, yc = 0;
            foreach (var sample in samples)
            {
                var value = pixels[sample.Offset + channel];
                sum += value;
                xc += (sample.X - meanX) * value;
                yc += (sample.Y - meanY) * value;
            }
            var plane = (Mean: sum / samples.Count,
                X: (xc * yy - yc * xy) / determinant,
                Y: (yc * xx - xc * xy) / determinant);
            double squaredError = 0, maxError = 0;
            foreach (var sample in samples)
            {
                var error = Math.Abs(pixels[sample.Offset + channel] -
                    (plane.Mean + plane.X * (sample.X - meanX) + plane.Y * (sample.Y - meanY)));
                squaredError += error * error;
                maxError = Math.Max(maxError, error);
            }
            if (Math.Sqrt(squaredError / samples.Count) > 4 || maxError > 12 ||
                Math.Abs(plane.X) > 4 || Math.Abs(plane.Y) > 4)
                throw new InvalidDataException(
                    "Surroundings are too textured or cross an edge. Retouch supports small blemishes on smooth surfaces only.");
            planes[channel] = plane;
        }
        // No writes occur until every channel and the complete stroke have passed the quality gate.
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (!mask[y * w + x]) continue;
            var offset = ((top + y) * width + left + x) * 4;
            for (var channel = 0; channel < 3; channel++)
            {
                var plane = planes[channel];
                pixels[offset + channel] = (byte)Math.Clamp(Math.Round(
                    plane.Mean + plane.X * (x - meanX) + plane.Y * (y - meanY)), 0, 255);
            }
        }
    }
}
