// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Services;

/// <summary>
/// A blurred luma image with gradients and a half-resolution level, prepared once per frame
/// for <see cref="RigidAligner"/>.
/// </summary>
internal sealed class AlignmentImage
{
    private const float MinimumGradient = 3f;

    public int Width { get; }
    public int Height { get; }
    public float[] Pixels { get; }
    public float[] GradientX { get; }
    public float[] GradientY { get; }

    /// <summary>The next coarser pyramid level, or null at the top.</summary>
    public AlignmentImage? Coarser { get; }

    /// <summary>Indices of textured pixels used as alignment samples.</summary>
    public int[] Samples { get; }

    private AlignmentImage(float[] pixels, int width, int height, int levels)
    {
        Width = width;
        Height = height;
        Pixels = Blur(pixels, width, height);
        (GradientX, GradientY) = Gradients(Pixels, width, height);
        Samples = SelectSamples(GradientX, GradientY, width, height);
        if (levels > 1 && width >= 160 && height >= 120)
            Coarser = new AlignmentImage(Halve(Pixels, width, height), width / 2, height / 2, levels - 1);
    }

    /// <summary>Box-downsamples the luma plane of an NV12 frame by <paramref name="scale"/>.</summary>
    public static AlignmentImage FromNv12(byte[] frame, int width, int height, int scale,
        CancellationToken cancellationToken)
    {
        int smallWidth = width / scale, smallHeight = height / scale;
        var pixels = new float[smallWidth * smallHeight];
        float normalize = 1f / (scale * scale);
        Parallel.For(0, smallHeight, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            for (int x = 0; x < smallWidth; x++)
            {
                int sum = 0;
                for (int row = 0; row < scale; row++)
                {
                    int offset = (y * scale + row) * width + x * scale;
                    for (int column = 0; column < scale; column++)
                        sum += frame[offset + column];
                }
                pixels[y * smallWidth + x] = sum * normalize;
            }
        });
        return new AlignmentImage(pixels, smallWidth, smallHeight, levels: 2);
    }

    private static float[] Halve(float[] pixels, int width, int height)
    {
        int halfWidth = width / 2, halfHeight = height / 2;
        var result = new float[halfWidth * halfHeight];
        Parallel.For(0, halfHeight, y =>
        {
            for (int x = 0; x < halfWidth; x++)
            {
                int i = 2 * y * width + 2 * x;
                result[y * halfWidth + x] = (pixels[i] + pixels[i + 1] + pixels[i + width] + pixels[i + width + 1]) / 4;
            }
        });
        return result;
    }

    /// <summary>Separable [1 2 1] blur; widens the alignment's convergence range and suppresses noise.</summary>
    private static float[] Blur(float[] pixels, int width, int height)
    {
        var horizontal = new float[pixels.Length];
        Parallel.For(0, height, y =>
        {
            int row = y * width;
            horizontal[row] = pixels[row];
            horizontal[row + width - 1] = pixels[row + width - 1];
            for (int x = 1; x < width - 1; x++)
                horizontal[row + x] = (pixels[row + x - 1] + 2 * pixels[row + x] + pixels[row + x + 1]) / 4;
        });
        var result = new float[pixels.Length];
        Parallel.For(0, height, y =>
        {
            int up = Math.Max(0, y - 1) * width, row = y * width, down = Math.Min(height - 1, y + 1) * width;
            for (int x = 0; x < width; x++)
                result[row + x] = (horizontal[up + x] + 2 * horizontal[row + x] + horizontal[down + x]) / 4;
        });
        return result;
    }

    private static (float[] X, float[] Y) Gradients(float[] pixels, int width, int height)
    {
        var gradientX = new float[pixels.Length];
        var gradientY = new float[pixels.Length];
        Parallel.For(1, height - 1, y =>
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                gradientX[i] = (pixels[i + 1] - pixels[i - 1]) / 2;
                gradientY[i] = (pixels[i + width] - pixels[i - width]) / 2;
            }
        });
        return (gradientX, gradientY);
    }

    private static int[] SelectSamples(float[] gradientX, float[] gradientY, int width, int height)
    {
        const int maxSamples = 80000;
        int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(width * (double)height / (maxSamples * 3.0))));
        var samples = new List<int>();
        int margin = Math.Max(2, Math.Min(width, height) / 40);
        for (int y = margin; y < height - margin; y += step)
        for (int x = margin; x < width - margin; x += step)
        {
            int i = y * width + x;
            if (Math.Abs(gradientX[i]) + Math.Abs(gradientY[i]) >= MinimumGradient) samples.Add(i);
        }
        if (samples.Count <= maxSamples) return samples.ToArray();
        var thinned = new int[maxSamples];
        for (int i = 0; i < maxSamples; i++) thinned[i] = samples[(int)((long)i * samples.Count / maxSamples)];
        return thinned;
    }
}

/// <summary>
/// Dense, robust Gauss-Newton alignment (Lucas-Kanade) of a rigid motion plus a brightness offset.
/// Using tens of thousands of textured pixels gives hundredth-of-a-pixel precision, far better
/// than block matching, so stabilized output does not inherit per-frame measurement jitter.
/// </summary>
internal static class RigidAligner
{
    private const int MaxIterations = 20;
    private const int MinimumSamples = 400;

    /// <summary>The alignment result and how much of the image agreed with it.</summary>
    public readonly record struct Result(VideoFrameStabilizer.Motion Motion, double InlierFraction);

    /// <summary>
    /// Finds the motion that maps <paramref name="current"/> coordinates (centered, in this image's
    /// pixels) into <paramref name="reference"/>: q = R(angle)·p + (X, Y). Returns null when the
    /// alignment fails to converge or too little of the image agrees.
    /// </summary>
    public static Result? Align(AlignmentImage current, AlignmentImage reference,
        VideoFrameStabilizer.Motion guess, CancellationToken cancellationToken)
    {
        if (current.Coarser is { } coarseCurrent && reference.Coarser is { } coarseReference)
        {
            var coarseGuess = guess with { X = guess.X / 2, Y = guess.Y / 2 };
            if (AlignLevel(coarseCurrent, coarseReference, coarseGuess, new ArraySegment<int>(coarseCurrent.Samples),
                    false, cancellationToken) is { } coarse)
                guess = coarse.Motion with { X = coarse.Motion.X * 2, Y = coarse.Motion.Y * 2 };
        }
        return AlignLevel(current, reference, guess, new ArraySegment<int>(current.Samples), false, cancellationToken);
    }

    /// <summary>
    /// Refines only the translation of rows [<paramref name="rowStart"/>, <paramref name="rowEnd"/>)
    /// of <paramref name="current"/>, keeping the rotation of <paramref name="rigid"/>. Used to
    /// measure rolling-shutter wobble, where rows captured at different times move differently.
    /// </summary>
    public static Result? AlignRows(AlignmentImage current, AlignmentImage reference,
        VideoFrameStabilizer.Motion rigid, int rowStart, int rowEnd, CancellationToken cancellationToken)
    {
        var samples = current.Samples;
        int first = LowerBound(samples, rowStart * current.Width);
        int last = LowerBound(samples, rowEnd * current.Width);
        var rows = new ArraySegment<int>(samples, first, last - first);
        return AlignLevel(current, reference, rigid, rows, true, cancellationToken);
    }

    private static int LowerBound(int[] sorted, int value)
    {
        int index = Array.BinarySearch(sorted, value);
        return index >= 0 ? index : ~index;
    }

    private sealed class Accumulator
    {
        public readonly double[] Hessian = new double[16];
        public readonly double[] Gradient = new double[4];
        public int Valid, Inliers;
    }

    private static Result? AlignLevel(AlignmentImage current, AlignmentImage reference,
        VideoFrameStabilizer.Motion guess, ArraySegment<int> samples, bool fixAngle,
        CancellationToken cancellationToken)
    {
        int width = current.Width, height = current.Height;
        if (reference.Width != width || reference.Height != height) return null;
        if (samples.Count < MinimumSamples) return null;

        double centerX = (width - 1) / 2.0, centerY = (height - 1) / 2.0;
        double x = guess.X, y = guess.Y, angle = guess.Angle, offset = 0;
        double scaleEstimate = 12;
        Accumulator? last = null;
        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (sin, cos) = Math.SinCos(angle);
            double threshold = Math.Max(4, 4.685 * scaleEstimate);
            var total = new Accumulator();
            var residuals = new float[samples.Count];
            object gate = new();
            Parallel.For(0, samples.Count,  new ParallelOptions { CancellationToken = cancellationToken },
                () => new Accumulator(),
                (s, _, local) =>
                {
                    int index = samples[s];
                    double px = index % width - centerX, py = index / width - centerY;
                    double qx = cos * px - sin * py + x + centerX;
                    double qy = sin * px + cos * py + y + centerY;
                    residuals[s] = float.NaN;
                    if (qx < 1 || qy < 1 || qx >= width - 2 || qy >= height - 2) return local;
                    int ix = (int)qx, iy = (int)qy;
                    float fx = (float)(qx - ix), fy = (float)(qy - iy);
                    int i = iy * width + ix;
                    double value = Bilinear(reference.Pixels, i, width, fx, fy);
                    double gx = Bilinear(reference.GradientX, i, width, fx, fy);
                    double gy = Bilinear(reference.GradientY, i, width, fx, fy);
                    double error = value + offset - current.Pixels[index];
                    residuals[s] = (float)error;
                    local.Valid++;
                    double magnitude = Math.Abs(error);
                    if (magnitude >= threshold) return local;
                    local.Inliers++;
                    // Tukey biweight down-weights moving subjects and occlusions.
                    double ratio = magnitude / threshold;
                    double weight = (1 - ratio * ratio) * (1 - ratio * ratio);
                    double jAngle = gx * (-sin * px - cos * py) + gy * (cos * px - sin * py);
                    Span<double> jacobian = stackalloc double[] { gx, gy, jAngle, 1 };
                    for (int r = 0; r < 4; r++)
                    {
                        local.Gradient[r] += weight * jacobian[r] * error;
                        for (int c = r; c < 4; c++) local.Hessian[r * 4 + c] += weight * jacobian[r] * jacobian[c];
                    }
                    return local;
                },
                local =>
                {
                    lock (gate)
                    {
                        for (int k = 0; k < 16; k++) total.Hessian[k] += local.Hessian[k];
                        for (int k = 0; k < 4; k++) total.Gradient[k] += local.Gradient[k];
                        total.Valid += local.Valid;
                        total.Inliers += local.Inliers;
                    }
                });
            last = total;
            if (total.Inliers < MinimumSamples) return null;
            scaleEstimate = Math.Max(1, RobustScale(residuals));

            for (int r = 0; r < 4; r++)
                for (int c = 0; c < r; c++) total.Hessian[r * 4 + c] = total.Hessian[c * 4 + r];
            if (fixAngle)
            {
                for (int k = 0; k < 4; k++) total.Hessian[2 * 4 + k] = total.Hessian[k * 4 + 2] = 0;
                total.Hessian[2 * 4 + 2] = 1;
                total.Gradient[2] = 0;
            }
            if (Solve(total.Hessian, total.Gradient) is not { } step) return null;
            x -= step[0];
            y -= step[1];
            angle -= step[2];
            offset -= step[3];
            if (!double.IsFinite(x) || Math.Abs(angle) > 0.3 ||
                Math.Abs(x - guess.X) > width / 8.0 || Math.Abs(y - guess.Y) > height / 8.0)
                return null;
            double radius = Math.Max(width, height) / 2.0;
            if (Math.Abs(step[0]) < 0.002 && Math.Abs(step[1]) < 0.002 && Math.Abs(step[2]) * radius < 0.002)
                break;
        }
        if (last is null || last.Valid == 0) return null;
        return new Result(new VideoFrameStabilizer.Motion(x, y, angle), last.Inliers / (double)samples.Count);
    }

    private static double Bilinear(float[] values, int index, int width, float fx, float fy)
    {
        float top = values[index] + (values[index + 1] - values[index]) * fx;
        float bottom = values[index + width] + (values[index + width + 1] - values[index + width]) * fx;
        return top + (bottom - top) * fy;
    }

    /// <summary>Median absolute residual scaled to a standard deviation, from a subsample.</summary>
    private static double RobustScale(float[] residuals)
    {
        int stride = Math.Max(1, residuals.Length / 4000);
        var magnitudes = new List<float>(residuals.Length / stride + 1);
        for (int i = 0; i < residuals.Length; i += stride)
            if (!float.IsNaN(residuals[i])) magnitudes.Add(Math.Abs(residuals[i]));
        if (magnitudes.Count == 0) return 12;
        magnitudes.Sort();
        return 1.4826 * magnitudes[magnitudes.Count / 2];
    }

    /// <summary>Solves the 4x4 system with Gaussian elimination and partial pivoting.</summary>
    private static double[]? Solve(double[] matrix, double[] vector)
    {
        var a = (double[])matrix.Clone();
        var b = (double[])vector.Clone();
        for (int column = 0; column < 4; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < 4; row++)
                if (Math.Abs(a[row * 4 + column]) > Math.Abs(a[pivot * 4 + column])) pivot = row;
            if (Math.Abs(a[pivot * 4 + column]) < 1e-9) return null;
            if (pivot != column)
            {
                for (int k = 0; k < 4; k++) (a[column * 4 + k], a[pivot * 4 + k]) = (a[pivot * 4 + k], a[column * 4 + k]);
                (b[column], b[pivot]) = (b[pivot], b[column]);
            }
            for (int row = column + 1; row < 4; row++)
            {
                double factor = a[row * 4 + column] / a[column * 4 + column];
                for (int k = column; k < 4; k++) a[row * 4 + k] -= factor * a[column * 4 + k];
                b[row] -= factor * b[column];
            }
        }
        var result = new double[4];
        for (int row = 3; row >= 0; row--)
        {
            double sum = b[row];
            for (int k = row + 1; k < 4; k++) sum -= a[row * 4 + k] * result[k];
            result[row] = sum / a[row * 4 + row];
        }
        return result;
    }
}
