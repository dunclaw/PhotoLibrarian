namespace PhotoLibrarian.Core.Services;

/// <summary>Robust local motion tracking and rigid camera-path smoothing for NV12 frames.</summary>
internal sealed class VideoFrameStabilizer
{
    private const double CropRatio = 0.08;
    private readonly int width, height;
    private readonly bool _autoCrop;
    private readonly int _cropX, _cropY;
    private byte[]? _previous;
    private double _pathX, _pathY, _pathAngle, _smoothX, _smoothY, _smoothAngle;

    public VideoFrameStabilizer(int width, int height, bool autoCrop = false)
    {
        this.width = width;
        this.height = height;
        _autoCrop = autoCrop && width >= 16 && height >= 16;
        _cropX = _autoCrop ? GetCropOffset(width) : 0;
        _cropY = _autoCrop ? GetCropOffset(height) : 0;
    }

    public byte[] Process(byte[] frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0 ||
            frame.Length != checked(width * height * 3 / 2))
            throw new ArgumentException("The decoder must supply an even-sized NV12 frame.");

        int scale = Math.Max(1, (int)Math.Ceiling(Math.Max(width / 640.0, height / 360.0)));
        int smallWidth = width / scale, smallHeight = height / scale;
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

        var previous = _previous;
        _previous = luma;
        double offsetX = 0, offsetY = 0, angle = 0;
        if (previous is null && !_autoCrop) return frame;
        if (previous is not null)
        {
            var motion = EstimateMotion(luma, previous, smallWidth, smallHeight, cancellationToken);
            if (motion is null)
            {
                _pathX = _pathY = _pathAngle = _smoothX = _smoothY = _smoothAngle = 0;
                if (!_autoCrop) return frame;
            }
            else
            {
                // Compose current-to-previous motion with the accumulated camera pose.
                var (sinPath, cosPath) = Math.SinCos(_pathAngle);
                _pathX += (cosPath * motion.Value.X - sinPath * motion.Value.Y) * scale;
                _pathY += (sinPath * motion.Value.X + cosPath * motion.Value.Y) * scale;
                _pathAngle += motion.Value.Angle;
                _smoothX += (_pathX - _smoothX) * 0.08;
                _smoothY += (_pathY - _smoothY) * 0.08;
                _smoothAngle += (_pathAngle - _smoothAngle) * 0.08;
                (sinPath, cosPath) = Math.SinCos(_pathAngle);
                double deltaX = _smoothX - _pathX, deltaY = _smoothY - _pathY;
                offsetX = Math.Clamp(cosPath * deltaX + sinPath * deltaY, -width * 0.08, width * 0.08);
                offsetY = Math.Clamp(-sinPath * deltaX + cosPath * deltaY, -height * 0.08, height * 0.08);
                angle = Math.Clamp(_smoothAngle - _pathAngle, -0.06, 0.06);
            }
        }

        if (_autoCrop)
            (offsetX, offsetY, angle) = LimitCorrectionToCrop(width, height, _cropX, _cropY, offsetX, offsetY, angle);
        var (sin, cos) = Math.SinCos(angle);
        var output = new byte[frame.Length];
        // Warp and crop/rescale are composed into one sampling pass to avoid double interpolation.
        WarpPlane(frame, output, width, height, 0, 1, cos, sin, offsetX, offsetY,
            _cropX, _cropY, cancellationToken);
        WarpPlane(frame, output, width / 2, height / 2, width * height, 2,
            cos, sin, offsetX / 2, offsetY / 2, _cropX / 2, _cropY / 2, cancellationToken);
        return output;
    }

    private static ParallelOptions Options(CancellationToken cancellationToken) =>
        new() { CancellationToken = cancellationToken };

    private readonly record struct Motion(double X, double Y, double Angle);
    private readonly record struct Track(double X, double Y, double Dx, double Dy);

    private static Motion? EstimateMotion(byte[] current, byte[] previous, int width, int height,
        CancellationToken cancellationToken)
    {
        const int patch = 6;
        int radius = Math.Min(20, Math.Min(width, height) / 8);
        int margin = radius + patch + 1;
        var found = new Track?[40];
        Parallel.For(0, found.Length, Options(cancellationToken), cell =>
        {
            int gx = cell % 8, gy = cell / 8;
            int x = margin + (width - 2 * margin) * (2 * gx + 1) / 16;
            int y = margin + (height - 2 * margin) * (2 * gy + 1) / 10;
            if (x < margin || y < margin || x >= width - margin || y >= height - margin) return;
            int texture = 0;
            for (int py = -patch; py <= patch; py += 2)
            for (int px = -patch; px <= patch; px += 2)
                texture += Math.Abs(current[(y + py) * width + x + px + 1] -
                                    current[(y + py) * width + x + px - 1]);
            if (texture < 120) return;

            int bestX = 0, bestY = 0;
            double best = double.MaxValue;
            for (int dy = -radius; dy <= radius; dy += 2)
            for (int dx = -radius; dx <= radius; dx += 2)
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
            if (best > 25 || Math.Abs(bestX) >= radius || Math.Abs(bestY) >= radius) return;
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

    internal static int GetCropOffset(int length)
    {
        int maximum = ((length - 2) / 2) & ~1;
        int requested = ((int)Math.Ceiling(length * CropRatio) + 1) & ~1;
        return Math.Min(maximum, requested);
    }

    internal static (double X, double Y, double Angle) LimitCorrectionToCrop(
        int width, int height, int cropX, int cropY, double offsetX, double offsetY, double angle)
    {
        if (IsCropValid(width, height, cropX, cropY, offsetX, offsetY, angle))
            return (offsetX, offsetY, angle);

        double low = 0, high = 1;
        for (int i = 0; i < 20; i++)
        {
            double factor = (low + high) / 2;
            if (IsCropValid(width, height, cropX, cropY,
                    offsetX * factor, offsetY * factor, angle * factor))
                low = factor;
            else
                high = factor;
        }
        return (offsetX * low, offsetY * low, angle * low);
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
}
