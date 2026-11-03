// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Services;

/// <summary>Donor-to-base rigid transform about the center of the oriented, decoded image.</summary>
internal readonly record struct PhotoFuseTransform(double X, double Y, double Angle)
{
    internal (double X, double Y) SourceAt(double x, double y, int width, int height)
    {
        double cx = (width - 1) / 2.0, cy = (height - 1) / 2.0;
        var (sin, cos) = Math.SinCos(Angle);
        double dx = x - cx - X, dy = y - cy - Y;
        return (cos * dx + sin * dy + cx, -sin * dx + cos * dy + cy);
    }
}

internal static class PhotoFuseRegistration
{
    // Unlike video stabilization, a failed measurement must never fall back to guessed motion.
    internal static PhotoFuseTransform Register(byte[] donor, byte[] basis, int width, int height,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int scale = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / 640.0));
        var current = AlignmentImage.FromNv12(Luma(donor), width, height, scale, token);
        var reference = AlignmentImage.FromNv12(Luma(basis), width, height, scale, token);
        RequireDistributedTexture(current);
        RequireDistributedTexture(reference);
        var guess = CoarseGuess(current, reference, token);
        var result = RigidAligner.Align(current, reference, guess, token);
        if (result is not { } found || found.InlierFraction < 0.85 ||
            Math.Abs(found.Motion.X) > current.Width * 0.03 ||
            Math.Abs(found.Motion.Y) > current.Height * 0.03 ||
            Math.Abs(found.Motion.Angle) > Math.PI / 90 ||
            Math.Abs(found.Shear.X) > 0.35 || Math.Abs(found.Shear.Y) > 0.35)
            throw Rejected();

        // Box downsampling can omit a partial right/bottom cell. Account for that shifted center
        // when converting a centered rotation back to the full preview's coordinate system.
        double deltaX = -(width % scale) / 2.0, deltaY = -(height % scale) / 2.0;
        var (sin, cos) = Math.SinCos(found.Motion.Angle);
        var transform = new PhotoFuseTransform(found.Motion.X * scale + (1 - cos) * deltaX + sin * deltaY,
            found.Motion.Y * scale - sin * deltaX + (1 - cos) * deltaY, found.Motion.Angle);
        ValidateAgreement(donor, basis, width, height, transform, token);
        return transform;
    }

    private static void RequireDistributedTexture(AlignmentImage image)
    {
        var counts = new int[9];
        foreach (int i in image.Samples)
        {
            int tileX = Math.Min(2, (i % image.Width) * 3 / image.Width);
            int tileY = Math.Min(2, (i / image.Width) * 3 / image.Height);
            counts[tileY * 3 + tileX]++;
        }
        if (counts.Any(count => count < 100)) throw Rejected();
    }

    private static byte[] Luma(byte[] bgra)
    {
        var result = new byte[bgra.Length / 4];
        for (int i = 0; i < result.Length; i++)
            result[i] = (byte)((29 * bgra[i * 4] + 150 * bgra[i * 4 + 1] + 77 * bgra[i * 4 + 2]) >> 8);
        return result;
    }

    private static VideoFrameStabilizer.Motion CoarseGuess(AlignmentImage donor, AlignmentImage basis,
        CancellationToken token)
    {
        int radius = Math.Min(18, Math.Min(donor.Width, donor.Height) / 30);
        double best = double.MaxValue;
        int bestX = 0, bestY = 0;
        var candidates = new List<(int X, int Y, double Error)>();
        int samples = 0;
        for (int dy = -radius; dy <= radius; dy += 2)
        {
            token.ThrowIfCancellationRequested();
            for (int dx = -radius; dx <= radius; dx += 2)
            {
                double error = 0;
                int count = 0;
                for (int y = radius + 4; y < donor.Height - radius - 4; y += 6)
                for (int x = radius + 4; x < donor.Width - radius - 4; x += 6)
                {
                    error += Math.Min(30, Math.Abs(donor.Pixels[y * donor.Width + x] -
                        basis.Pixels[(y + dy) * basis.Width + x + dx]));
                    count++;
                }
                candidates.Add((dx, dy, error));
                samples = count;
                if (error >= best) continue;
                best = error;
                bestX = dx;
                bestY = dy;
            }
        }
        // Repeating textures (fences, tiled walls) may support two incompatible registrations.
        if (candidates.Any(candidate => Math.Abs(candidate.X - bestX) + Math.Abs(candidate.Y - bestY) >= 6 &&
            candidate.Error <= best * 1.10 + samples * 0.5))
            throw Rejected();
        return new(bestX, bestY, 0);
    }

    /// <summary>
    /// Independent RGB holdout grid, including low-gradient pixels. A good luma fit alone is not
    /// evidence of safe geometry or matching exposure/white balance. Require distributed support.
    /// </summary>
    private static void ValidateAgreement(byte[] donor, byte[] basis, int width, int height,
        PhotoFuseTransform transform, CancellationToken token)
    {
        for (int tileY = 0; tileY < 3; tileY++)
        for (int tileX = 0; tileX < 3; tileX++)
        {
            token.ThrowIfCancellationRequested();
            int count = 0, inliers = 0;
            for (int y = height * tileY / 3 + 8; y < height * (tileY + 1) / 3 - 8; y += 3)
            for (int x = width * tileX / 3 + 8; x < width * (tileX + 1) / 3 - 8; x += 3)
            {
                var p = transform.SourceAt(x, y, width, height);
                count++;
                if (!Inside(p.X, p.Y, width, height)) continue;
                double error = 0;
                for (int c = 0; c < 3; c++)
                    error = Math.Max(error, Math.Abs(Sample(donor, width, p.X, p.Y, c) -
                        basis[(y * width + x) * 4 + c]));
                if (error <= 18) inliers++;
            }
            // A small moving face is allowed; a moving foreground covering most of a tile is not.
            if (count < 100 || inliers < count * 0.70) throw Rejected();
        }
    }

    internal static bool Inside(double x, double y, int width, int height) =>
        double.IsFinite(x) && double.IsFinite(y) && x >= 0 && y >= 0 && x < width - 1 && y < height - 1;

    internal static double Sample(byte[] pixels, int width, double x, double y, int channel)
    {
        int ix = (int)x, iy = (int)y;
        double fx = x - ix, fy = y - iy;
        int i = (iy * width + ix) * 4 + channel;
        double top = pixels[i] + (pixels[i + 4] - pixels[i]) * fx;
        double bottom = pixels[i + width * 4] + (pixels[i + width * 4 + 4] - pixels[i + width * 4]) * fx;
        return top + (bottom - top) * fy;
    }

    private static InvalidDataException Rejected() => new(
        "These shots could not be aligned reliably. Use a textured, stationary background, the same " +
        "zoom and exposure, and only small camera shifts. Zoom, parallax and large subject movement are unsupported.");
}
