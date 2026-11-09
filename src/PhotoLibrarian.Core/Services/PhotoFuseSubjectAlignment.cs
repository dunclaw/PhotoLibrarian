// Copyright (c) Duncan Lawler. All Rights Reserved.
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.Core.Services;

public readonly record struct PhotoFuseFaceBounds(double X, double Y, double Width, double Height)
{
    public static PhotoFuseFaceBounds From(FaceRegion face) => new(face.X, face.Y, face.Width, face.Height);
    internal bool IsValid => double.IsFinite(X) && double.IsFinite(Y) &&
        double.IsFinite(Width) && double.IsFinite(Height) && X >= 0 && Y >= 0 &&
        Width > 0 && Height > 0 && X + Width <= 1 && Y + Height <= 1;
}

public sealed record PhotoFuseFaceSelection(int Frame, PhotoFuseFaceBounds Target, PhotoFuseFaceBounds Donor);

internal static class PhotoFuseSubjectAlignment
{
    private const int MaximumFitSamples = 4096;
    internal sealed record Result(PhotoFuseTransform? Transform, string? Rejection);
    private readonly record struct Fit(double X, double Y, double Angle, double Scale, double Inliers);

    internal static Result Align(byte[] donor, byte[] basis, int width, int height,
        PhotoFuseFaceSelection selection, PhotoFuseTransform? searchHint,
        IReadOnlyList<PhotoFuseFaceBounds> originalFaces, bool matchFacialPixels, CancellationToken token)
    {
        var target = selection.Target;
        var source = selection.Donor;
        if (!target.IsValid || !source.IsValid) return new(null, "A face box is outside its photo.");
        double faceWidth = target.Width * width, faceHeight = target.Height * height;
        if (Math.Min(faceWidth, faceHeight) < 24 ||
            Math.Min(source.Width * width, source.Height * height) < 24)
            return new(null, "This face is too small in the preview for a reliable replacement.");
        double initialScale = Math.Sqrt(target.Width * target.Height / (source.Width * source.Height));
        if (initialScale is < 0.75 or > 1.33)
            return new(null, "The replacement face differs too much in size.");
        double tx = (target.X + target.Width / 2) * width - (width - 1) / 2.0;
        double ty = (target.Y + target.Height / 2) * height - (height - 1) / 2.0;
        double sx = (source.X + source.Width / 2) * width - (width - 1) / 2.0;
        double sy = (source.Y + source.Height / 2) * height - (height - 1) / 2.0;
        var boxTransform = new PhotoFuseTransform(tx - initialScale * sx, ty - initialScale * sy, 0, initialScale - 1);
        var initial = matchFacialPixels ? boxTransform : searchHint ?? boxTransform;
        double baseScale = 1 + initial.ScaleDelta;
        double context = matchFacialPixels ? 1.3 : 2.5;
        int analysisScale = Math.Max(1, (int)Math.Ceiling(Math.Max(faceWidth, faceHeight) * context / 256));
        int aw = Math.Max(24, (int)Math.Round(faceWidth * context / analysisScale));
        int ah = Math.Max(24, (int)Math.Round(faceHeight * context / analysisScale));
        var first = new byte[aw * ah];
        var second = new byte[first.Length];
        var valid = new bool[first.Length];
        double cx = tx + (width - 1) / 2.0, cy = ty + (height - 1) / 2.0;
        for (int y = 0; y < ah; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < aw; x++)
            {
                double bx = cx + (x - (aw - 1) / 2.0) * analysisScale;
                double by = cy + (y - (ah - 1) / 2.0) * analysisScale;
                var p = initial.SourceAt(bx, by, width, height);
                if (!PhotoFuseRegistration.Inside(bx, by, width, height) ||
                    !PhotoFuseRegistration.Inside(p.X, p.Y, width, height)) continue;
                first[y * aw + x] = Luma(donor, width, p.X, p.Y);
                second[y * aw + x] = Luma(basis, width, bx, by);
                valid[y * aw + x] = (matchFacialPixels ||
                    !WithinFace(bx, by, target, width, height) && !WithinFace(p.X, p.Y, source, width, height)) &&
                    !WithinOtherFace(bx, by, originalFaces, target, width, height);
            }
        }
        var current = AlignmentImage.FromNv12(first, aw, ah, 1, token);
        var reference = AlignmentImage.FromNv12(second, aw, ah, 1, token);
        var samples = current.Samples.Where(index => valid[index] && valid[index - 1] &&
            valid[index + 1] && valid[index - aw] && valid[index + aw]).ToArray();
        if (samples.Length < 80)
            return new(null, "There is not enough surrounding detail to align this replacement reliably.");
        // Bound the normal-equation work, not photo/face counts. RGB and seam validation
        // still examine the complete independent full-resolution holdout and feather band.
        int sampleStride = Math.Max(1, (samples.Length + MaximumFitSamples - 1) / MaximumFitSamples);
        if (sampleStride > 1) samples = samples.Where((_, index) => index % sampleStride == 0).ToArray();
        double step = Math.Min(faceWidth, faceHeight) * 0.12 / analysisScale;
        var (hintSin, hintCos) = Math.SinCos(initial.Angle);
        double mappedX = baseScale * (hintCos * sx - hintSin * sy) + initial.X;
        double mappedY = baseScale * (hintSin * sx + hintCos * sy) + initial.Y;
        double boxScale = initialScale / baseScale;
        Fit[] seeds = [new(0, 0, 0, 1, 0), new(step, 0, 0, 1, 0), new(-step, 0, 0, 1, 0),
            new(0, step, 0, 1, 0), new(0, -step, 0, 1, 0),
            new(0, 0, Math.PI / 30, 1, 0), new(0, 0, -Math.PI / 30, 1, 0),
            new(-boxScale * (mappedX - tx) / analysisScale, -boxScale * (mappedY - ty) / analysisScale,
                -initial.Angle, boxScale, 0)];
        var fits = new List<(PhotoFuseTransform Transform, double Error)>();
        foreach (var seed in seeds)
        {
            var result = FitPixels(current, reference, samples, seed, token);
            if (result is not { Inliers: >= 0.80 } fit) continue;
            if (Math.Sqrt(fit.X * fit.X + fit.Y * fit.Y) * analysisScale >
                Math.Min(faceWidth, faceHeight) * 0.35 || Math.Abs(fit.Angle) > Math.PI / 12) continue;
            double totalScale = baseScale * fit.Scale;
            if (totalScale is < 0.75 or > 1.33) continue;
            var (sin, cos) = Math.SinCos(fit.Angle);
            var found = new PhotoFuseTransform(
                fit.Scale * (cos * initial.X - sin * initial.Y) + fit.X * analysisScale +
                    (1 - fit.Scale * cos) * tx + fit.Scale * sin * ty,
                fit.Scale * (sin * initial.X + cos * initial.Y) + fit.Y * analysisScale -
                    fit.Scale * sin * tx + (1 - fit.Scale * cos) * ty,
                initial.Angle + fit.Angle, totalScale - 1);
            var quality = CheckContext(donor, basis, width, height, selection, originalFaces, found, matchFacialPixels, token);
            if (quality.Accepted) fits.Add((found, quality.Error));
        }
        if (fits.Count == 0) return new(null, "The surrounding head, clothing or background does not align reliably.");
        var best = fits.MinBy(fit => fit.Error);
        foreach (var fit in fits)
        {
            if (fit.Error > best.Error + 0.5) continue;
            foreach (double y in new[] { target.Y * height, (target.Y + target.Height) * height })
            foreach (double x in new[] { target.X * width, (target.X + target.Width) * width })
            {
                var a = fit.Transform.SourceAt(x, y, width, height);
                var b = best.Transform.SourceAt(x, y, width, height);
                if (Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)) > 1.5)
                    return new(null, "This face has multiple competing alignments.");
            }
        }
        return new(best.Transform, null);
    }

    private static Fit? FitPixels(AlignmentImage current, AlignmentImage reference, int[] samples,
        Fit seed, CancellationToken token)
    {
        const int parameters = 5;
        int width = current.Width, height = current.Height;
        double cx = (width - 1) / 2.0, cy = (height - 1) / 2.0;
        double x = seed.X, y = seed.Y, angle = seed.Angle, logScale = Math.Log(seed.Scale), offset = 0, sigma = 12;
        int inliers = 0;
        Span<double> jacobian = stackalloc double[parameters];
        for (int iteration = 0; iteration < 30; iteration++)
        {
            token.ThrowIfCancellationRequested();
            var matrix = new double[parameters * parameters];
            var gradient = new double[parameters];
            var errors = new List<double>(2048);
            var (sin, cos) = Math.SinCos(angle);
            double scale = Math.Exp(logScale), threshold = Math.Max(4, 4.685 * sigma);
            inliers = 0;
            int sampleNumber = 0, stride = Math.Max(1, samples.Length / 2048);
            foreach (int index in samples)
            {
                double px = index % width - cx, py = index / width - cy;
                double rx = scale * (cos * px - sin * py), ry = scale * (sin * px + cos * py);
                double qx = rx + x + cx, qy = ry + y + cy;
                if (qx < 1 || qy < 1 || qx >= width - 2 || qy >= height - 2) continue;
                double error = Sample(reference.Pixels, width, qx, qy) + offset - current.Pixels[index];
                if (sampleNumber++ % stride == 0) errors.Add(Math.Abs(error));
                if (Math.Abs(error) >= threshold) continue;
                inliers++;
                double gx = Sample(reference.GradientX, width, qx, qy);
                double gy = Sample(reference.GradientY, width, qx, qy);
                jacobian[0] = gx;
                jacobian[1] = gy;
                jacobian[2] = -gx * ry + gy * rx;
                jacobian[3] = gx * rx + gy * ry;
                jacobian[4] = 1;
                double ratio = error / threshold;
                double weight = Math.Pow(1 - ratio * ratio, 2);
                for (int r = 0; r < parameters; r++)
                {
                    gradient[r] += weight * jacobian[r] * error;
                    for (int c = r; c < parameters; c++)
                        matrix[r * parameters + c] += weight * jacobian[r] * jacobian[c];
                }
            }
            if (inliers < 80 || errors.Count == 0) return null;
            errors.Sort();
            sigma = Math.Max(1, 1.4826 * errors[errors.Count / 2]);
            for (int r = 0; r < parameters; r++)
                for (int c = 0; c < r; c++) matrix[r * parameters + c] = matrix[c * parameters + r];
            if (RigidAligner.Solve(matrix, gradient) is not { } delta) return null;
            x -= delta[0]; y -= delta[1]; angle -= delta[2]; logScale -= delta[3]; offset -= delta[4];
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(angle) ||
                !double.IsFinite(logScale) || Math.Abs(x) > width * 0.35 || Math.Abs(y) > height * 0.35 ||
                Math.Abs(angle) > 0.3 || Math.Abs(logScale) > 0.3 || Math.Abs(offset) > 30) return null;
            if (Math.Abs(delta[0]) + Math.Abs(delta[1]) < 0.004 &&
                (Math.Abs(delta[2]) + Math.Abs(delta[3])) * Math.Max(width, height) < 0.008) break;
        }
        return new(x, y, angle, Math.Exp(logScale), inliers / (double)samples.Length);
    }

    private static (bool Accepted, double Error) CheckContext(byte[] donor, byte[] basis, int width, int height,
        PhotoFuseFaceSelection selection, IReadOnlyList<PhotoFuseFaceBounds> originalFaces,
        PhotoFuseTransform transform, bool matchFacialPixels, CancellationToken token)
    {
        var face = selection.Target;
        var source = selection.Donor;
        double scale = 1 + transform.ScaleDelta;
        var (sin, cos) = Math.SinCos(transform.Angle);
        double px = (source.X + source.Width / 2) * width - (width - 1) / 2.0;
        double py = (source.Y + source.Height / 2) * height - (height - 1) / 2.0;
        double mx = scale * (cos * px - sin * py) + transform.X + (width - 1) / 2.0;
        double my = scale * (sin * px + cos * py) + transform.Y + (height - 1) / 2.0;
        if (Math.Pow((mx - (face.X + face.Width / 2) * width) / (face.Width * width), 2) +
            Math.Pow((my - (face.Y + face.Height / 2) * height) / (face.Height * height), 2) > 0.5 * 0.5)
            return (false, double.MaxValue);
        int count = 0, inliers = 0;
        double total = 0;
        double padding = matchFacialPixels ? 0 : 0.75;
        int left = Math.Max(0, (int)Math.Ceiling((face.X - face.Width * padding) * width));
        int top = Math.Max(0, (int)Math.Ceiling((face.Y - face.Height * padding) * height));
        int right = Math.Min(width - 1, (int)Math.Floor((face.X + face.Width * (1 + padding)) * width));
        int bottom = Math.Min(height - 1, (int)Math.Floor((face.Y + face.Height * (1 + padding)) * height));
        for (int y = top; y <= bottom; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = left; x <= right; x++)
            {
                if (!matchFacialPixels && WithinFace(x, y, face, width, height) ||
                    WithinOtherFace(x, y, originalFaces, face, width, height)) continue;
                var point = transform.SourceAt(x, y, width, height);
                if (!PhotoFuseRegistration.Inside(point.X, point.Y, width, height)) continue;
                if (!matchFacialPixels && WithinFace(point.X, point.Y, source, width, height)) continue;
                double error = 0;
                for (int c = 0; c < 3; c++)
                    error = Math.Max(error, Math.Abs(PhotoFuseRegistration.Sample(donor, width, point.X, point.Y, c) -
                        basis[(y * width + x) * 4 + c]));
                total += Math.Min(40, error);
                count++;
                if (error <= 24) inliers++;
            }
        }
        return (count >= 400 && inliers >= count * 0.70, count == 0 ? double.MaxValue : total / count);
    }

    internal static bool WithinFace(double x, double y, PhotoFuseFaceBounds face, int width, int height) =>
        x >= face.X * width && x <= (face.X + face.Width) * width &&
        y >= face.Y * height && y <= (face.Y + face.Height) * height;

    private static bool WithinOtherFace(double x, double y, IReadOnlyList<PhotoFuseFaceBounds> faces,
        PhotoFuseFaceBounds selected, int width, int height)
    {
        foreach (var face in faces)
            if (face != selected && WithinFace(x, y, face, width, height)) return true;
        return false;
    }

    private static byte Luma(byte[] pixels, int width, double x, double y) =>
        (byte)((29 * PhotoFuseRegistration.Sample(pixels, width, x, y, 0) +
            150 * PhotoFuseRegistration.Sample(pixels, width, x, y, 1) +
            77 * PhotoFuseRegistration.Sample(pixels, width, x, y, 2)) / 256);

    private static double Sample(float[] pixels, int width, double x, double y)
    {
        int ix = (int)x, iy = (int)y, index = iy * width + ix;
        double fx = x - ix, fy = y - iy;
        double top = pixels[index] + (pixels[index + 1] - pixels[index]) * fx;
        double bottom = pixels[index + width] + (pixels[index + width + 1] - pixels[index + width]) * fx;
        return top + (bottom - top) * fy;
    }
}
