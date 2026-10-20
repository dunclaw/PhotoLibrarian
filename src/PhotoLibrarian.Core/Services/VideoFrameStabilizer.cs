namespace PhotoLibrarian.Core.Services;

/// <summary>Translation-only motion estimation and trajectory smoothing for NV12 frames.</summary>
internal sealed class VideoFrameStabilizer(int width, int height)
{
    private byte[]? _previous;
    private byte[]? _previousFrame;
    private double _pathX, _pathY, _smoothX, _smoothY;

    public byte[] Process(byte[] frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0 ||
            frame.Length != checked(width * height * 3 / 2))
            throw new ArgumentException("The decoder must supply an even-sized NV12 frame.");

        int scale = Math.Max(1, (int)Math.Ceiling(Math.Max(width / 320.0, height / 240.0)));
        int smallWidth = width / scale, smallHeight = height / scale;
        var luma = new byte[smallWidth * smallHeight];
        for (int y = 0; y < smallHeight; y++)
            for (int x = 0; x < smallWidth; x++)
            {
                int sum = 0;
                for (int row = 0; row < scale; row++)
                    for (int column = 0; column < scale; column++)
                        sum += frame[(y * scale + row) * width + x * scale + column];
                luma[y * smallWidth + x] = (byte)(sum / (scale * scale));
            }

        if (_previous is null || _previousFrame is null)
        {
            _previous = luma;
            _previousFrame = frame;
            return frame;
        }

        int radius = Math.Min(12, Math.Min(smallWidth, smallHeight) / 8);
        var (dx, dy, error) = Match(luma, _previous, smallWidth, smallHeight,
            -radius, radius, -radius, radius, radius, 4, cancellationToken);
        dx *= scale;
        dy *= scale;
        if (scale > 1 && error <= 35)
        {
            (dx, dy, error) = Match(frame, _previousFrame, width, height,
                dx - scale + 1, dx + scale - 1, dy - scale + 1, dy + scale - 1,
                (radius + 1) * scale, scale * 4, cancellationToken);
        }
        _previous = luma;
        _previousFrame = frame;
        if (error > 35)
        {
            _pathX = _pathY = _smoothX = _smoothY = 0;
            return frame;
        }
        _pathX += dx;
        _pathY += dy;
        _smoothX += (_pathX - _smoothX) * 0.12;
        _smoothY += (_pathY - _smoothY) * 0.12;
        // Chroma is subsampled 2x2: keep both planes aligned, with a bounded correction.
        int offsetX = (int)Math.Round(Math.Clamp(_smoothX - _pathX, -width * 0.05, width * 0.05) / 2) * 2;
        int offsetY = (int)Math.Round(Math.Clamp(_smoothY - _pathY, -height * 0.05, height * 0.05) / 2) * 2;
        var output = new byte[frame.Length];
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int row = Mirror(y + offsetY, height) * width;
            for (int x = 0; x < width; x++)
                output[y * width + x] = frame[row + Mirror(x + offsetX, width)];
        }
        int chromaStart = width * height;
        for (int y = 0; y < height / 2; y++)
            for (int x = 0; x < width / 2; x++)
            {
                int source = chromaStart + Mirror(y + offsetY / 2, height / 2) * width +
                    Mirror(x + offsetX / 2, width / 2) * 2;
                int destination = chromaStart + y * width + x * 2;
                output[destination] = frame[source];
                output[destination + 1] = frame[source + 1];
            }
        return output;
    }

    private static (int X, int Y, double Error) Match(
        byte[] current, byte[] previous, int width, int height,
        int minX, int maxX, int minY, int maxY, int margin, int step, CancellationToken cancellationToken)
    {
        long best = long.MaxValue;
        int bestX = 0, bestY = 0, count = 0;
        for (int dy = minY; dy <= maxY; dy++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int dx = minX; dx <= maxX; dx++)
            {
                long error = 0;
                count = 0;
                for (int y = margin; y < height - margin; y += step)
                    for (int x = margin; x < width - margin; x += step)
                    {
                        error += Math.Abs(current[y * width + x] - previous[(y + dy) * width + x + dx]);
                        count++;
                    }
                if (error < best || (error == best && Math.Abs(dx) + Math.Abs(dy) < Math.Abs(bestX) + Math.Abs(bestY)))
                {
                    best = error;
                    bestX = dx;
                    bestY = dy;
                }
            }
        }
        return (bestX, bestY, count == 0 ? 0 : best / (double)count);
    }

    private static int Mirror(int coordinate, int length)
    {
        int wrapped = ((coordinate % (2 * length)) + 2 * length) % (2 * length);
        return wrapped < length ? wrapped : 2 * length - wrapped - 1;
    }
}
