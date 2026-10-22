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
        for (int y = 0; y < smallHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < smallWidth; x++)
            {
                int sum = 0;
                for (int row = 0; row < scale; row++)
                    for (int column = 0; column < scale; column++)
                        sum += frame[(y * scale + row) * width + x * scale + column];
                luma[y * smallWidth + x] = (byte)(sum / (scale * scale));
            }
        }

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
        var warped = new byte[frame.Length];
        WarpPlane(frame, warped, width, height, 0, 1, cos, sin, offsetX, offsetY, cancellationToken);
        WarpPlane(frame, warped, width / 2, height / 2, width * height, 2,
            cos, sin, offsetX / 2, offsetY / 2, cancellationToken);
        if (!_autoCrop) return warped;

        var output = new byte[frame.Length];
        CropAndResizePlane(warped, output, width, height, 0, 1, _cropX, _cropY, width, height, cancellationToken);
        CropAndResizePlane(warped, output, width / 2, height / 2, width * height, 2,
            _cropX / 2, _cropY / 2, width / 2, height / 2, cancellationToken);
        return output;
    }

    private readonly record struct Motion(double X, double Y, double Angle);
    private readonly record struct Track(double X, double Y, double Dx, double Dy);

    private static Motion? EstimateMotion(byte[] current, byte[] previous, int width, int height,
        CancellationToken cancellationToken)
    {
        const int patch = 6;
        int radius = Math.Min(20, Math.Min(width, height) / 8);
        int margin = radius + patch + 1;
        var tracks = new List<Track>();
        for (int gy = 0; gy < 5; gy++)
        for (int gx = 0; gx < 8; gx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int x = margin + (width - 2 * margin) * (2 * gx + 1) / 16;
            int y = margin + (height - 2 * margin) * (2 * gy + 1) / 10;
            if (x < margin || y < margin || x >= width - margin || y >= height - margin) continue;
            int texture = 0;
            for (int py = -patch; py <= patch; py += 2)
            for (int px = -patch; px <= patch; px += 2)
                texture += Math.Abs(current[(y + py) * width + x + px + 1] -
                                    current[(y + py) * width + x + px - 1]);
            if (texture < 120) continue;

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
            if (best > 25 || Math.Abs(bestX) >= radius || Math.Abs(bestY) >= radius) continue;
            double subX = Refine(best, PatchError(current, previous, width, x, y, bestX - 1, bestY, patch),
                PatchError(current, previous, width, x, y, bestX + 1, bestY, patch));
            double subY = Refine(best, PatchError(current, previous, width, x, y, bestX, bestY - 1, patch),
                PatchError(current, previous, width, x, y, bestX, bestY + 1, patch));
            tracks.Add(new Track(x - (width - 1) / 2.0, y - (height - 1) / 2.0, bestX + subX, bestY + subY));
        }
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

    private static void CropAndResizePlane(byte[] input, byte[] output, int width, int height, int start,
        int components, int cropX, int cropY, int outputWidth, int outputHeight,
        CancellationToken cancellationToken)
    {
        int cropWidth = width - 2 * cropX, cropHeight = height - 2 * cropY;
        for (int y = 0; y < outputHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double sourceY = cropY + (y + 0.5) * cropHeight / outputHeight - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sourceY), cropY, cropY + cropHeight - 1);
            int y1 = Math.Min(y0 + 1, cropY + cropHeight - 1);
            double fy = sourceY - Math.Floor(sourceY);
            for (int x = 0; x < outputWidth; x++)
            {
                double sourceX = cropX + (x + 0.5) * cropWidth / outputWidth - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sourceX), cropX, cropX + cropWidth - 1);
                int x1 = Math.Min(x0 + 1, cropX + cropWidth - 1);
                double fx = sourceX - Math.Floor(sourceX);
                int destination = start + (y * outputWidth + x) * components;
                int topLeft = start + (y0 * width + x0) * components;
                int topRight = start + (y0 * width + x1) * components;
                int bottomLeft = start + (y1 * width + x0) * components;
                int bottomRight = start + (y1 * width + x1) * components;
                for (int c = 0; c < components; c++)
                    output[destination + c] = (byte)Math.Clamp(Math.Round(
                        (input[topLeft + c] * (1 - fx) + input[topRight + c] * fx) * (1 - fy) +
                        (input[bottomLeft + c] * (1 - fx) + input[bottomRight + c] * fx) * fy), 0, 255);
            }
        }
    }

    private static void WarpPlane(byte[] input, byte[] output, int width, int height, int start,
        int components, double cos, double sin, double dx, double dy, CancellationToken cancellationToken)
    {
        double centerX = (width - 1) / 2.0, centerY = (height - 1) / 2.0;
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double sourceX = centerX - cos * centerX - sin * (y - centerY) + dx;
            double sourceY = centerY - sin * centerX + cos * (y - centerY) + dy;
            for (int x = 0; x < width; x++, sourceX += cos, sourceY += sin)
            {
                int ix = (int)Math.Floor(sourceX), iy = (int)Math.Floor(sourceY);
                double fx = sourceX - ix, fy = sourceY - iy;
                int x0 = Mirror(ix, width), x1 = Mirror(ix + 1, width);
                int row0 = start + Mirror(iy, height) * width * components;
                int row1 = start + Mirror(iy + 1, height) * width * components;
                int destination = start + (y * width + x) * components;
                for (int c = 0; c < components; c++)
                    output[destination + c] = (byte)Math.Clamp(Math.Round(
                        (input[row0 + x0 * components + c] * (1 - fx) + input[row0 + x1 * components + c] * fx) * (1 - fy) +
                        (input[row1 + x0 * components + c] * (1 - fx) + input[row1 + x1 * components + c] * fx) * fy), 0, 255);
            }
        }
    }

    private static int Mirror(int coordinate, int length)
    {
        int wrapped = ((coordinate % (2 * length)) + 2 * length) % (2 * length);
        return wrapped < length ? wrapped : 2 * length - wrapped - 1;
    }
}
