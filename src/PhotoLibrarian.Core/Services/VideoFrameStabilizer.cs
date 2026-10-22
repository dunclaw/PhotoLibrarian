// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Services;

/// <summary>
/// Two-pass rigid video stabilization for NV12 frames.
/// <list type="number">
/// <item><see cref="Analyze"/> estimates the camera motion between consecutive frames.</item>
/// <item><see cref="PlanCorrections"/> smooths the whole camera path with a centered Gaussian
/// window (so intentional pans are followed without lag) and keeps every correction inside the
/// available crop budget.</item>
/// <item><see cref="Render"/> warps each frame by its planned correction.</item>
/// </list>
/// </summary>
internal sealed class VideoFrameStabilizer
{
    private const double CropRatio = 0.08;
    private const double MaxTranslationRatio = 0.08;
    private const double MaxAngle = 0.06;
    private const int SearchRadius = 20;
    private const int CoarseRadius = 16;

    private readonly int _width, _height;
    private readonly bool _autoCrop;
    private readonly int _cropX, _cropY;
    private byte[]? _previous;
    private (int X, int Y)? _predicted;

    /// <summary>Camera motion from a frame to its predecessor, in full-resolution pixels and radians.</summary>
    public readonly record struct Motion(double X, double Y, double Angle);

    /// <summary>The rigid warp applied to one output frame.</summary>
    public readonly record struct Correction(double X, double Y, double Angle);

    private readonly record struct Track(double X, double Y, double Dx, double Dy);

    public VideoFrameStabilizer(int width, int height, bool autoCrop = false)
    {
        _width = width;
        _height = height;
        _autoCrop = autoCrop && width >= 16 && height >= 16;
        _cropX = _autoCrop ? GetCropOffset(width) : 0;
        _cropY = _autoCrop ? GetCropOffset(height) : 0;
    }

    /// <summary>
    /// Estimates the motion from <paramref name="frame"/> to the previously analyzed frame.
    /// Returns zero motion for the first frame and null when the motion cannot be measured
    /// reliably (for example a scene cut, a flat frame or heavy blur).
    /// </summary>
    public Motion? Analyze(byte[] frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFrame(frame);

        int scale = Math.Max(1, (int)Math.Ceiling(Math.Max(_width / 640.0, _height / 360.0)));
        int smallWidth = _width / scale, smallHeight = _height / scale;
        var luma = Downsample(frame, _width, smallWidth, smallHeight, scale, cancellationToken);

        var previous = _previous;
        _previous = luma;
        if (previous is null)
        {
            _predicted = null;
            return new Motion(0, 0, 0);
        }

        // Search around the previous frame's motion first so steady pans stay inside the search
        // window, then around a coarse whole-frame estimate, then around zero motion.
        var centers = new List<(int X, int Y)>(3);
        if (_predicted is { } predicted) centers.Add(predicted);
        var coarse = EstimateCoarseShift(luma, previous, smallWidth, smallHeight);
        if (!centers.Contains(coarse)) centers.Add(coarse);
        if (!centers.Contains((0, 0))) centers.Add((0, 0));

        foreach (var center in centers)
        {
            var motion = EstimateMotion(luma, previous, smallWidth, smallHeight, center, cancellationToken);
            if (motion is not { } found) continue;
            _predicted = ((int)Math.Round(found.X), (int)Math.Round(found.Y));
            return new Motion(found.X * scale, found.Y * scale, found.Angle);
        }

        // Motion blur during quick camera moves smears fine texture so small patches stop matching.
        // At half the analysis resolution the blur spans fewer pixels and the patches cover more
        // scene, which usually recovers a reliable estimate instead of leaving a gap to guess.
        if (smallWidth >= 128 && smallHeight >= 128)
        {
            int halfWidth = smallWidth / 2, halfHeight = smallHeight / 2;
            var halfLuma = Downsample(luma, smallWidth, halfWidth, halfHeight, 2, cancellationToken);
            var halfPrevious = Downsample(previous, smallWidth, halfWidth, halfHeight, 2, cancellationToken);
            foreach (var center in centers)
            {
                var halfCenter = ((int)Math.Round(center.X / 2.0), (int)Math.Round(center.Y / 2.0));
                var motion = EstimateMotion(halfLuma, halfPrevious, halfWidth, halfHeight, halfCenter,
                    cancellationToken);
                if (motion is not { } found) continue;
                _predicted = ((int)Math.Round(found.X * 2), (int)Math.Round(found.Y * 2));
                return new Motion(found.X * 2 * scale, found.Y * 2 * scale, found.Angle);
            }
        }
        _predicted = null;
        return null;
    }

    /// <summary>
    /// Plans one correction per analyzed frame. Short tracking dropouts are bridged; longer ones
    /// start a new path segment so a scene cut never drags the previous shot's correction along.
    /// </summary>
    public Correction[] PlanCorrections(IReadOnlyList<Motion?> motions, double framesPerSecond)
    {
        int count = motions.Count;
        var corrections = new Correction[count];
        if (count == 0) return corrections;

        double fps = double.IsFinite(framesPerSecond) && framesPerSecond > 0 ? framesPerSecond : 30;
        var filled = BridgeShortGaps(motions, Math.Max(2, (int)Math.Round(fps * 0.2)));
        double sigma = Math.Clamp(fps * 0.5, 3, 60);

        int start = 0;
        for (int i = 1; i <= count; i++)
        {
            if (i < count && filled[i] is not null) continue;
            PlanSegment(filled, start, i, sigma, corrections);
            start = i;
        }
        return corrections;
    }

    /// <summary>Warps a frame by a planned correction (and auto-crops when enabled).</summary>
    public byte[] Render(byte[] frame, Correction correction, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFrame(frame);
        if (!_autoCrop && correction == default) return frame;

        var (sin, cos) = Math.SinCos(correction.Angle);
        var output = new byte[frame.Length];
        // Warp and crop/rescale are composed into one sampling pass to avoid double interpolation.
        WarpPlane(frame, output, _width, _height, 0, 1, cos, sin, correction.X, correction.Y,
            _cropX, _cropY, cancellationToken);
        WarpPlane(frame, output, _width / 2, _height / 2, _width * _height, 2,
            cos, sin, correction.X / 2, correction.Y / 2, _cropX / 2, _cropY / 2, cancellationToken);
        return output;
    }

    /// <summary>Runs all three stages over in-memory frames (used for small clips and tests).</summary>
    internal static List<byte[]> StabilizeAll(IReadOnlyList<byte[]> frames, int width, int height,
        bool autoCrop, double framesPerSecond, CancellationToken cancellationToken)
    {
        var stabilizer = new VideoFrameStabilizer(width, height, autoCrop);
        var motions = new List<Motion?>(frames.Count);
        foreach (var frame in frames)
            motions.Add(stabilizer.Analyze(frame, cancellationToken));
        var corrections = stabilizer.PlanCorrections(motions, framesPerSecond);
        var output = new List<byte[]>(frames.Count);
        for (int i = 0; i < frames.Count; i++)
            output.Add(stabilizer.Render(frames[i], corrections[i], cancellationToken));
        return output;
    }

    private void ValidateFrame(byte[] frame)
    {
        if (_width < 2 || _height < 2 || (_width & 1) != 0 || (_height & 1) != 0 ||
            frame.Length != checked(_width * _height * 3 / 2))
            throw new ArgumentException("The decoder must supply an even-sized NV12 frame.");
    }

    private static ParallelOptions Options(CancellationToken cancellationToken) =>
        new() { CancellationToken = cancellationToken };

    private static byte[] Downsample(byte[] frame, int width, int smallWidth, int smallHeight, int scale,
        CancellationToken cancellationToken)
    {
        var luma = new byte[smallWidth * smallHeight];
        Parallel.For(0, smallHeight, Options(cancellationToken), y =>
        {
            for (int x = 0; x < smallWidth; x++)
            {
                int sum = 0;
                for (int row = 0; row < scale; row++)
                    for (int column = 0; column < scale; column++)
                        sum += frame[(y * scale + row) * width + x * scale + column];
                luma[y * smallWidth + x] = (byte)(sum / (scale * scale));
            }
        });
        return luma;
    }

    #region Motion estimation

    /// <summary>
    /// Whole-frame translation search on a 4x-reduced image. Covers large pans that exceed the
    /// fine search window; the result is only used as a search center and is validated later.
    /// </summary>
    private static (int X, int Y) EstimateCoarseShift(byte[] current, byte[] previous, int width, int height)
    {
        const int factor = 4;
        int coarseWidth = width / factor, coarseHeight = height / factor;
        int margin = CoarseRadius + 1;
        if (coarseWidth <= 2 * margin + 4 || coarseHeight <= 2 * margin + 4) return (0, 0);

        var currentCoarse = Downsample(current, width, coarseWidth, coarseHeight, factor, CancellationToken.None);
        var previousCoarse = Downsample(previous, width, coarseWidth, coarseHeight, factor, CancellationToken.None);
        int side = 2 * CoarseRadius + 1;
        var errors = new long[side * side];
        Parallel.For(0, errors.Length, index =>
        {
            int dx = index % side - CoarseRadius, dy = index / side - CoarseRadius;
            long error = 0;
            for (int y = margin; y < coarseHeight - margin; y += 2)
            for (int x = margin; x < coarseWidth - margin; x += 2)
                error += Math.Abs(currentCoarse[y * coarseWidth + x] -
                                  previousCoarse[(y + dy) * coarseWidth + x + dx]);
            errors[index] = error;
        });
        int best = 0;
        for (int i = 1; i < errors.Length; i++)
            if (errors[i] < errors[best]) best = i;
        return ((best % side - CoarseRadius) * factor, (best / side - CoarseRadius) * factor);
    }

    private static Motion? EstimateMotion(byte[] current, byte[] previous, int width, int height,
        (int X, int Y) center, CancellationToken cancellationToken)
    {
        const int patch = 6;
        int radius = Math.Min(SearchRadius, Math.Min(width, height) / 8);
        // Every sampled pixel, including the +-1 refinement steps, must stay inside both frames.
        int reach = radius + patch + 2;
        int marginLeft = Math.Max(patch + 1, reach - center.X), marginRight = Math.Max(patch + 1, reach + center.X);
        int marginTop = Math.Max(patch + 1, reach - center.Y), marginBottom = Math.Max(patch + 1, reach + center.Y);
        int spanX = width - marginLeft - marginRight, spanY = height - marginTop - marginBottom;
        if (spanX < width / 4 || spanY < height / 4) return null;

        var found = new Track?[40];
        Parallel.For(0, found.Length, Options(cancellationToken), cell =>
        {
            int gx = cell % 8, gy = cell / 8;
            int x = marginLeft + spanX * (2 * gx + 1) / 16;
            int y = marginTop + spanY * (2 * gy + 1) / 10;
            int texture = 0;
            for (int py = -patch; py <= patch; py += 2)
            for (int px = -patch; px <= patch; px += 2)
                texture += Math.Abs(current[(y + py) * width + x + px + 1] -
                                    current[(y + py) * width + x + px - 1]);
            if (texture < 120) return;

            int bestX = center.X, bestY = center.Y;
            double best = double.MaxValue;
            for (int dy = center.Y - radius; dy <= center.Y + radius; dy += 2)
            for (int dx = center.X - radius; dx <= center.X + radius; dx += 2)
            {
                double error = PatchError(current, previous, width, x, y, dx, dy, patch);
                if (error < best)
                {
                    best = error;
                    bestX = dx;
                    bestY = dy;
                }
            }
            int coarseX = bestX, coarseY = bestY;
            for (int dy = coarseY - 1; dy <= coarseY + 1; dy++)
            for (int dx = coarseX - 1; dx <= coarseX + 1; dx++)
            {
                double error = PatchError(current, previous, width, x, y, dx, dy, patch);
                if (error < best)
                {
                    best = error;
                    bestX = dx;
                    bestY = dy;
                }
            }
            if (best > 25 || Math.Abs(bestX - center.X) >= radius || Math.Abs(bestY - center.Y) >= radius)
                return;
            double subX = Refine(best, PatchError(current, previous, width, x, y, bestX - 1, bestY, patch),
                PatchError(current, previous, width, x, y, bestX + 1, bestY, patch));
            double subY = Refine(best, PatchError(current, previous, width, x, y, bestX, bestY - 1, patch),
                PatchError(current, previous, width, x, y, bestX, bestY + 1, patch));
            found[cell] = new Track(x - (width - 1) / 2.0, y - (height - 1) / 2.0, bestX + subX, bestY + subY);
        });
        var tracks = found.Where(track => track.HasValue).Select(track => track!.Value).ToList();
        if (tracks.Count < 6) return null;

        // Robust consensus rejects independently moving subjects and ambiguous low-texture matches.
        Motion bestMotion = default;
        int bestCount = 0;
        foreach (var a in tracks)
        foreach (var b in tracks)
        {
            double x = b.X - a.X, y = b.Y - a.Y;
            double distance = x * x + y * y;
            if (distance < 400) continue;
            double angle = (x * (b.Dy - a.Dy) - y * (b.Dx - a.Dx)) / distance;
            if (Math.Abs(angle) > 0.08) continue;
            var model = new Motion(a.Dx + angle * a.Y, a.Dy - angle * a.X, angle);
            int count = tracks.Count(track => Residual(track, model) < 1.5);
            if (count > bestCount)
            {
                bestCount = count;
                bestMotion = model;
            }
        }
        if (bestCount < Math.Max(6, tracks.Count / 2)) return null;
        var inliers = tracks.Where(track => Residual(track, bestMotion) < 1.5).ToList();
        double meanX = inliers.Average(t => t.X), meanY = inliers.Average(t => t.Y);
        double meanDx = inliers.Average(t => t.Dx), meanDy = inliers.Average(t => t.Dy);
        double numerator = 0, denominator = 0;
        foreach (var track in inliers)
        {
            double x = track.X - meanX, y = track.Y - meanY;
            numerator += x * (track.Dy - meanDy) - y * (track.Dx - meanDx);
            denominator += x * x + y * y;
        }
        double fittedAngle = denominator > 0 ? numerator / denominator : 0;
        return new Motion(meanDx + fittedAngle * meanY, meanDy - fittedAngle * meanX, fittedAngle);
    }

    private static double Residual(Track track, Motion model) =>
        Math.Sqrt(Math.Pow(track.Dx - model.X + model.Angle * track.Y, 2) +
                  Math.Pow(track.Dy - model.Y - model.Angle * track.X, 2));

    private static double Refine(double center, double left, double right)
    {
        double curvature = left + right - 2 * center;
        return curvature > 0 ? Math.Clamp((left - right) / (2 * curvature), -0.5, 0.5) : 0;
    }

    private static double PatchError(byte[] current, byte[] previous, int width,
        int x, int y, int dx, int dy, int patch)
    {
        int error = 0, count = 0;
        for (int py = -patch; py <= patch; py += 2)
        for (int px = -patch; px <= patch; px += 2)
        {
            error += Math.Abs(current[(y + py) * width + x + px] -
                              previous[(y + py + dy) * width + x + px + dx]);
            count++;
        }
        return error / (double)count;
    }

    #endregion

    #region Path planning

    /// <summary>Linearly interpolates motion across dropouts of at most <paramref name="maxGap"/> frames.</summary>
    private static Motion?[] BridgeShortGaps(IReadOnlyList<Motion?> motions, int maxGap)
    {
        var filled = motions.ToArray();
        int i = 1;
        while (i < filled.Length)
        {
            if (filled[i] is not null)
            {
                i++;
                continue;
            }
            int end = i;
            while (end < filled.Length && filled[end] is null) end++;
            var before = filled[i - 1];
            var after = end < filled.Length ? filled[end] : null;
            if (end - i <= maxGap && (before is not null || after is not null))
            {
                var from = before ?? after!.Value;
                var to = after ?? before!.Value;
                for (int k = i; k < end; k++)
                {
                    double t = (k - i + 1) / (double)(end - i + 1);
                    filled[k] = new Motion(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t,
                        from.Angle + (to.Angle - from.Angle) * t);
                }
            }
            i = end;
        }
        return filled;
    }

    /// <summary>Plans frames [start, end), whose first frame has no usable link to its predecessor.</summary>
    private void PlanSegment(Motion?[] motions, int start, int end, double sigma, Correction[] corrections)
    {
        int length = end - start;
        var pathX = new double[length];
        var pathY = new double[length];
        var pathAngle = new double[length];
        for (int i = 1; i < length; i++)
        {
            var motion = motions[start + i]!.Value;
            // Compose current-to-previous motion with the accumulated camera pose.
            var (sin, cos) = Math.SinCos(pathAngle[i - 1]);
            pathX[i] = pathX[i - 1] + cos * motion.X - sin * motion.Y;
            pathY[i] = pathY[i - 1] + sin * motion.X + cos * motion.Y;
            pathAngle[i] = pathAngle[i - 1] + motion.Angle;
        }

        var smoothX = GaussianSmooth(pathX, sigma);
        var smoothY = GaussianSmooth(pathY, sigma);
        var smoothAngle = GaussianSmooth(pathAngle, sigma);

        // Pull the smooth path toward the raw path wherever the correction would exceed the crop
        // budget, then re-smooth so the limited path stays smooth instead of clipping abruptly.
        for (int iteration = 0; iteration < 8; iteration++)
        {
            bool limited = false;
            for (int i = 0; i < length; i++)
            {
                double factor = LimitFactor(GetCorrection(pathX[i], pathY[i], pathAngle[i],
                    smoothX[i], smoothY[i], smoothAngle[i]));
                if (factor >= 1) continue;
                limited = true;
                smoothX[i] = pathX[i] + (smoothX[i] - pathX[i]) * factor;
                smoothY[i] = pathY[i] + (smoothY[i] - pathY[i]) * factor;
                smoothAngle[i] = pathAngle[i] + (smoothAngle[i] - pathAngle[i]) * factor;
            }
            if (!limited) break;
            smoothX = GaussianSmooth(smoothX, sigma / 3);
            smoothY = GaussianSmooth(smoothY, sigma / 3);
            smoothAngle = GaussianSmooth(smoothAngle, sigma / 3);
        }

        for (int i = 0; i < length; i++)
        {
            var correction = GetCorrection(pathX[i], pathY[i], pathAngle[i], smoothX[i], smoothY[i], smoothAngle[i]);
            double factor = LimitFactor(correction);
            corrections[start + i] = new Correction(correction.X * factor, correction.Y * factor,
                correction.Angle * factor);
        }
    }

    private static Correction GetCorrection(double pathX, double pathY, double pathAngle,
        double smoothX, double smoothY, double smoothAngle)
    {
        var (sin, cos) = Math.SinCos(pathAngle);
        double deltaX = smoothX - pathX, deltaY = smoothY - pathY;
        return new Correction(cos * deltaX + sin * deltaY, -sin * deltaX + cos * deltaY, smoothAngle - pathAngle);
    }

    /// <summary>The largest uniform scale (at most 1) that keeps a correction within the budget.</summary>
    private double LimitFactor(Correction correction)
    {
        double factor = 1;
        if (Math.Abs(correction.X) > _width * MaxTranslationRatio)
            factor = Math.Min(factor, _width * MaxTranslationRatio / Math.Abs(correction.X));
        if (Math.Abs(correction.Y) > _height * MaxTranslationRatio)
            factor = Math.Min(factor, _height * MaxTranslationRatio / Math.Abs(correction.Y));
        if (Math.Abs(correction.Angle) > MaxAngle)
            factor = Math.Min(factor, MaxAngle / Math.Abs(correction.Angle));
        if (_autoCrop)
            factor = Math.Min(factor, CropLimitFactor(_width, _height, _cropX, _cropY,
                correction.X * factor, correction.Y * factor, correction.Angle * factor) * factor);
        return factor;
    }

    /// <summary>
    /// Centered Gaussian smoothing. Ends are extended by point reflection about a local linear
    /// fit, which preserves pans at the clip edges while still removing jitter there.
    /// </summary>
    internal static double[] GaussianSmooth(double[] values, double sigma)
    {
        int length = values.Length;
        if (length < 3 || sigma <= 0) return (double[])values.Clone();
        int radius = Math.Min(length - 1, (int)Math.Ceiling(sigma * 3));
        var weights = new double[radius + 1];
        for (int k = 0; k <= radius; k++) weights[k] = Math.Exp(-0.5 * k * k / (sigma * sigma));

        int fitLength = Math.Clamp((int)Math.Ceiling(sigma * 2), 2, length);
        double startAnchor = FitLineAt(values, 0, fitLength, 0);
        double endAnchor = FitLineAt(values, length - fitLength, fitLength, length - 1);

        double Sample(int index)
        {
            if (index < 0) return 2 * startAnchor - values[-index];
            if (index >= length) return 2 * endAnchor - values[2 * (length - 1) - index];
            return values[index];
        }

        var result = new double[length];
        for (int i = 0; i < length; i++)
        {
            double sum = values[i] * weights[0], total = weights[0];
            for (int k = 1; k <= radius; k++)
            {
                sum += (Sample(i - k) + Sample(i + k)) * weights[k];
                total += 2 * weights[k];
            }
            result[i] = sum / total;
        }
        return result;
    }

    /// <summary>Least-squares line through values[start..start+count), evaluated at <paramref name="at"/>.</summary>
    private static double FitLineAt(double[] values, int start, int count, int at)
    {
        double meanT = start + (count - 1) / 2.0, meanV = 0;
        for (int i = 0; i < count; i++) meanV += values[start + i];
        meanV /= count;
        double numerator = 0, denominator = 0;
        for (int i = 0; i < count; i++)
        {
            double t = start + i - meanT;
            numerator += t * (values[start + i] - meanV);
            denominator += t * t;
        }
        double slope = denominator > 0 ? numerator / denominator : 0;
        return meanV + slope * (at - meanT);
    }

    #endregion

    #region Crop limits and warping

    internal static int GetCropOffset(int length)
    {
        int maximum = ((length - 2) / 2) & ~1;
        int requested = ((int)Math.Ceiling(length * CropRatio) + 1) & ~1;
        return Math.Min(maximum, requested);
    }

    internal static (double X, double Y, double Angle) LimitCorrectionToCrop(
        int width, int height, int cropX, int cropY, double offsetX, double offsetY, double angle)
    {
        double factor = CropLimitFactor(width, height, cropX, cropY, offsetX, offsetY, angle);
        return (offsetX * factor, offsetY * factor, angle * factor);
    }

    private static double CropLimitFactor(
        int width, int height, int cropX, int cropY, double offsetX, double offsetY, double angle)
    {
        if (IsCropValid(width, height, cropX, cropY, offsetX, offsetY, angle)) return 1;

        double low = 0, high = 1;
        for (int i = 0; i < 20; i++)
        {
            double factor = (low + high) / 2;
            if (IsCropValid(width, height, cropX, cropY, offsetX * factor, offsetY * factor, angle * factor))
                low = factor;
            else
                high = factor;
        }
        return low;
    }

    internal static bool IsCropValid(
        int width, int height, int cropX, int cropY, double offsetX, double offsetY, double angle)
    {
        double centerX = (width - 1) / 2.0, centerY = (height - 1) / 2.0;
        double halfWidth = centerX - cropX, halfHeight = centerY - cropY;
        var (sin, cos) = Math.SinCos(angle);
        for (int xIndex = 0; xIndex < 2; xIndex++)
        for (int yIndex = 0; yIndex < 2; yIndex++)
        {
            double x = xIndex == 0 ? -halfWidth : halfWidth;
            double y = yIndex == 0 ? -halfHeight : halfHeight;
            double sourceX = centerX + cos * x - sin * y + offsetX;
            double sourceY = centerY + sin * x + cos * y + offsetY;
            if (sourceX < 1 || sourceX > width - 2 || sourceY < 1 || sourceY > height - 2)
                return false;
        }
        return true;
    }

    private static void WarpPlane(byte[] input, byte[] output, int width, int height, int start,
        int components, double cos, double sin, double dx, double dy, int cropX, int cropY,
        CancellationToken cancellationToken)
    {
        double centerX = (width - 1) / 2.0, centerY = (height - 1) / 2.0;
        double scaleX = (width - 2.0 * cropX) / width, scaleY = (height - 2.0 * cropY) / height;
        double firstX = cropX + 0.5 * scaleX - 0.5 - centerX;
        double stepX = cos * scaleX, stepY = sin * scaleX;
        int rowLength = width * components;
        Parallel.For(0, height, Options(cancellationToken), y =>
        {
            double v = cropY + (y + 0.5) * scaleY - 0.5 - centerY;
            double sourceX = centerX + cos * firstX - sin * v + dx;
            double sourceY = centerY + sin * firstX + cos * v + dy;
            int destination = start + y * rowLength;
            for (int x = 0; x < width; x++, sourceX += stepX, sourceY += stepY, destination += components)
            {
                int ix = (int)Math.Floor(sourceX), iy = (int)Math.Floor(sourceY);
                double fx = sourceX - ix, fy = sourceY - iy;
                int x0, x1, row0, row1;
                if ((uint)ix < (uint)(width - 1) && (uint)iy < (uint)(height - 1))
                {
                    x0 = ix * components;
                    x1 = x0 + components;
                    row0 = start + iy * rowLength;
                    row1 = row0 + rowLength;
                }
                else
                {
                    x0 = Mirror(ix, width) * components;
                    x1 = Mirror(ix + 1, width) * components;
                    row0 = start + Mirror(iy, height) * rowLength;
                    row1 = start + Mirror(iy + 1, height) * rowLength;
                }
                for (int c = 0; c < components; c++)
                {
                    double top = input[row0 + x0 + c] + (input[row0 + x1 + c] - input[row0 + x0 + c]) * fx;
                    double bottom = input[row1 + x0 + c] + (input[row1 + x1 + c] - input[row1 + x0 + c]) * fx;
                    output[destination + c] = (byte)Math.Clamp((int)(top + (bottom - top) * fy + 0.5), 0, 255);
                }
            }
        });
    }

    private static int Mirror(int coordinate, int length)
    {
        int wrapped = ((coordinate % (2 * length)) + 2 * length) % (2 * length);
        return wrapped < length ? wrapped : 2 * length - wrapped - 1;
    }

    #endregion
}
