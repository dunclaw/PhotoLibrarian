// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

public enum RetouchStatus
{
    Applied,
    TransparentPixels,
    NoSurroundingPixels
}

public readonly record struct RetouchResult(RetouchStatus Status)
{
    public bool Succeeded => Status == RetouchStatus.Applied;
    public string? Message => Status switch
    {
        RetouchStatus.TransparentPixels => "Retouch needs opaque pixels in the painted area and its immediate surroundings.",
        RetouchStatus.NoSurroundingPixels => "Leave some unpainted pixels around the area so retouch has a source to fill from.",
        _ => null
    };
}

/// <summary>
/// Local exemplar inpainting with a Poisson boundary blend.
/// Never samples the content being removed or writes outside the painted mask.
/// </summary>
public static class SpotRetouchProcessor
{
    public const int MaxRadius = 24;

    public static void Validate(IReadOnlyList<RetouchStroke> strokes)
    {
        foreach (var stroke in strokes)
        {
            if (stroke is null || stroke.ImageWidth <= 0 || stroke.ImageHeight <= 0 ||
                stroke.Radius is < 2 or > MaxRadius || stroke.Points is null || stroke.Points.Length == 0)
                throw new InvalidDataException("Invalid retouch stroke.");
            foreach (var point in stroke.Points)
                if (point.X < 0 || point.Y < 0 || point.X >= stroke.ImageWidth || point.Y >= stroke.ImageHeight)
                    throw new InvalidDataException("Retouch point is outside its source image.");
        }
    }

    /// <summary>
    /// Applies recipes in order on an owned buffer. Unsupported image content returns a result,
    /// not an exception. Discard the buffer on failure; earlier strokes may already be applied.
    /// </summary>
    public static RetouchResult Apply(byte[] pixels, int width, int height, IReadOnlyList<RetouchStroke> strokes,
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
            var result = ApplyStroke(pixels, width, height, stroke, cancellationToken);
            if (!result.Succeeded) return result;
        }
        return new(RetouchStatus.Applied);
    }

    private static RetouchResult ApplyStroke(byte[] pixels, int width, int height, RetouchStroke stroke,
        CancellationToken cancellationToken)
    {
        var radius = stroke.Radius;
        var left = Math.Max(0, stroke.Points.Min(p => p.X) - radius - 1);
        var top = Math.Max(0, stroke.Points.Min(p => p.Y) - radius - 1);
        var right = (int)Math.Min(width - 1L, (long)stroke.Points.Max(p => p.X) + radius + 1);
        var bottom = (int)Math.Min(height - 1L, (long)stroke.Points.Max(p => p.Y) + radius + 1);
        var w = right - left + 1;
        var h = bottom - top + 1;
        var indices = new int[checked(w * h)];
        Array.Fill(indices, -1);

        // Rasterize each capsule only in its own bounds, not points x entire bounding rectangle.
        for (var i = 0; i < stroke.Points.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var a = stroke.Points[Math.Max(0, i - 1)];
            var b = stroke.Points[i];
            var dx = (double)b.X - a.X;
            var dy = (double)b.Y - a.Y;
            var lengthSquared = dx * dx + dy * dy;
            var x0 = Math.Max(left, Math.Min(a.X, b.X) - radius);
            var y0 = Math.Max(top, Math.Min(a.Y, b.Y) - radius);
            var x1 = Math.Min(right, (long)Math.Max(a.X, b.X) + radius);
            var y1 = Math.Min(bottom, (long)Math.Max(a.Y, b.Y) + radius);
            for (var y = y0; y <= y1; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = x0; x <= x1; x++)
                {
                    var t = lengthSquared == 0 ? 0 : Math.Clamp(
                        ((x - a.X) * dx + (y - a.Y) * dy) / lengthSquared, 0, 1);
                    var ex = x - a.X - t * dx;
                    var ey = y - a.Y - t * dy;
                    if (ex * ex + ey * ey <= radius * radius)
                        indices[(y - top) * w + x - left] = -2;
                }
            }
        }

        var selected = new List<int>();
        for (var i = 0; i < indices.Length; i++)
        {
            if (i % w == 0) cancellationToken.ThrowIfCancellationRequested();
            if (indices[i] != -2) continue;
            indices[i] = selected.Count;
            selected.Add(i);
        }
        if (selected.Count == (long)width * height)
            return new(RetouchStatus.NoSurroundingPixels);

        var neighbours = new int[checked(selected.Count * 4)];
        Array.Fill(neighbours, -1);
        var degree = new int[selected.Count];
        var boundary = new double[checked(selected.Count * 3)];
        var offsets = new int[selected.Count];
        double sumB = 0, sumG = 0, sumR = 0;
        var boundaryCount = 0;
        var transparentBoundary = false;
        for (var i = 0; i < selected.Count; i++)
        {
            if (i % 256 == 0) cancellationToken.ThrowIfCancellationRequested();
            var x = selected[i] % w + left;
            var y = selected[i] / w + top;
            offsets[i] = (y * width + x) * 4;
            if (pixels[offsets[i] + 3] != 255) return new(RetouchStatus.TransparentPixels);
            AddNeighbour(i, x - 1, y, 0);
            AddNeighbour(i, x + 1, y, 1);
            AddNeighbour(i, x, y - 1, 2);
            AddNeighbour(i, x, y + 1, 3);
        }
        if (boundaryCount == 0) return new(RetouchStatus.NoSurroundingPixels);
        // Check the entire stencil before solving or writing any selected pixels.
        if (transparentBoundary) return new(RetouchStatus.TransparentPixels);

        var guidance = RetouchPatchSynthesis.CreateGuidance(pixels, width, height, left, top,
            w, h, indices, selected, radius, cancellationToken);
        for (var i = 0; i < boundary.Length; i++)
            boundary[i] += guidance[i];

        var solution = new double[boundary.Length];
        var residual = new double[selected.Count];
        var direction = new double[selected.Count];
        var product = new double[selected.Count];
        for (var channel = 0; channel < 3; channel++)
        {
            var mean = (channel == 0 ? sumB : channel == 1 ? sumG : sumR) / boundaryCount;
            Solve(channel, mean);
        }
        cancellationToken.ThrowIfCancellationRequested();
        for (var i = 0; i < selected.Count; i++)
        for (var channel = 0; channel < 3; channel++)
            pixels[offsets[i] + channel] = (byte)Math.Clamp(Math.Round(solution[i * 3 + channel]), 0, 255);
        return new(RetouchStatus.Applied);

        void AddNeighbour(int index, int x, int y, int side)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            degree[index]++;
            var neighbour = indices[(y - top) * w + x - left];
            neighbours[index * 4 + side] = neighbour;
            if (neighbour >= 0) return;
            var offset = (y * width + x) * 4;
            if (pixels[offset + 3] != 255)
                transparentBoundary = true;
            boundary[index * 3] += pixels[offset];
            boundary[index * 3 + 1] += pixels[offset + 1];
            boundary[index * 3 + 2] += pixels[offset + 2];
            sumB += pixels[offset];
            sumG += pixels[offset + 1];
            sumR += pixels[offset + 2];
            boundaryCount++;
        }

        void Solve(int channel, double initial)
        {
            double residualNorm = 0;
            for (var i = 0; i < selected.Count; i++)
            {
                solution[i * 3 + channel] = initial;
                var knownNeighbours = degree[i];
                for (var side = 0; side < 4; side++)
                    if (neighbours[i * 4 + side] >= 0) knownNeighbours--;
                residual[i] = boundary[i * 3 + channel] - knownNeighbours * initial;
                direction[i] = residual[i] / degree[i];
                residualNorm += residual[i] * direction[i];
            }
            // Preconditioned conjugate gradients on the positive-definite masked Laplacian.
            // The stopping condition measures numerical residual, not photo texture or a quality gate.
            for (var iteration = 0; residualNorm > 1e-8 * selected.Count; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double denominator = 0;
                for (var i = 0; i < selected.Count; i++)
                {
                    var value = degree[i] * direction[i];
                    for (var side = 0; side < 4; side++)
                    {
                        var neighbour = neighbours[i * 4 + side];
                        if (neighbour >= 0) value -= direction[neighbour];
                    }
                    product[i] = value;
                    denominator += direction[i] * value;
                }
                if (!double.IsFinite(denominator) || denominator <= 0 || iteration > selected.Count * 2L)
                    throw new ArithmeticException("The retouch interpolation solver did not converge.");
                var step = residualNorm / denominator;
                double nextNorm = 0;
                for (var i = 0; i < selected.Count; i++)
                {
                    solution[i * 3 + channel] += step * direction[i];
                    residual[i] -= step * product[i];
                    nextNorm += residual[i] * residual[i] / degree[i];
                }
                var beta = nextNorm / residualNorm;
                for (var i = 0; i < selected.Count; i++)
                    direction[i] = residual[i] / degree[i] + beta * direction[i];
                residualNorm = nextNorm;
            }
        }
    }
}
