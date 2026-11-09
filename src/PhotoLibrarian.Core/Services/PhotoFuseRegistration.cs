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
    internal sealed record Result(PhotoFuseTransform? Transform, string? Reason)
    {
        internal bool Accepted => Transform.HasValue;
    }

    // Unlike video stabilization, a rejected measurement is a result, never guessed motion.
    internal static Result TryRegister(byte[] donor, byte[] basis, int width, int height,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int scale = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / 640.0));
        var current = AlignmentImage.FromNv12(Luma(donor), width, height, scale, token);
        var reference = AlignmentImage.FromNv12(Luma(basis), width, height, scale, token);
        if (!HasDistributedTexture(current) || !HasDistributedTexture(reference))
            return new(null, $"Texture support: donor [{string.Join(",", TextureCounts(current))}], " +
                $"base [{string.Join(",", TextureCounts(reference))}].");
        var guesses = CoarseGuesses(current, reference, token);
        var fits = new List<(PhotoFuseTransform Transform, double Agreement)>();
        string reason = "Rigid fit did not converge.";
        foreach (var guess in guesses)
        {
            var result = RigidAligner.Align(current, reference, guess, token, fitShear: false);
            if (result is not { } found) continue;
            if (found.InlierFraction < 0.85 || Math.Abs(found.Motion.Angle) > Math.PI / 90)
            {
                reason = $"Rigid fit has insufficient support or too much rotation (inliers {found.InlierFraction:F3}, " +
                    $"angle {found.Motion.Angle:F5}).";
                continue;
            }

            // Box downsampling can omit a partial right/bottom cell. Account for that shifted center
            // when converting a centered rotation back to the full preview's coordinate system.
            double deltaX = -(width % scale) / 2.0, deltaY = -(height % scale) / 2.0;
            var (sin, cos) = Math.SinCos(found.Motion.Angle);
            var transform = new PhotoFuseTransform(found.Motion.X * scale + (1 - cos) * deltaX + sin * deltaY,
                found.Motion.Y * scale - sin * deltaX + (1 - cos) * deltaY, found.Motion.Angle);
            var agreement = ValidateAgreement(donor, basis, width, height, transform, token);
            if (agreement.Reason is null)
            {
                string? geometry = ValidateLocalGeometry(current, reference, found.Motion, token);
                if (geometry is null) fits.Add((transform, agreement.Fraction));
                else reason = geometry;
            }
            else reason = agreement.Reason;
        }
        if (fits.Count == 0) return new(null, reason);
        var best = fits.MaxBy(fit => fit.Agreement);
        foreach (var fit in fits)
            if (fit.Agreement >= best.Agreement - 0.02 &&
                LandmarkDifference(fit.Transform, best.Transform, width, height) > 1)
                return new(null, "Multiple incompatible refined alignments have similar RGB support.");
        if (Math.Abs(best.Transform.X) > width * 0.03 || Math.Abs(best.Transform.Y) > height * 0.03)
            return new(null, "Camera translation exceeds the supported overlap.");
        return new(best.Transform, null);
    }

    private static double LandmarkDifference(PhotoFuseTransform first, PhotoFuseTransform second, int width, int height)
    {
        double maximum = 0;
        foreach (var point in new[] { (0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1) })
        {
            var a = first.SourceAt(point.Item1, point.Item2, width, height);
            var b = second.SourceAt(point.Item1, point.Item2, width, height);
            maximum = Math.Max(maximum, Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)));
        }
        return maximum;
    }

    private static int[] TextureCounts(AlignmentImage image)
    {
        var counts = new int[9];
        foreach (int i in image.Samples)
        {
            int tileX = Math.Min(2, (i % image.Width) * 3 / image.Width);
            int tileY = Math.Min(2, (i / image.Width) * 3 / image.Height);
            counts[tileY * 3 + tileX]++;
        }
        return counts;
    }
    private static bool HasDistributedTexture(AlignmentImage image) => TextureCounts(image).All(count => count >= 100);

    private static byte[] Luma(byte[] bgra)
    {
        var result = new byte[bgra.Length / 4];
        for (int i = 0; i < result.Length; i++)
            result[i] = (byte)((29 * bgra[i * 4] + 150 * bgra[i * 4 + 1] + 77 * bgra[i * 4 + 2]) >> 8);
        return result;
    }

    private static IReadOnlyList<VideoFrameStabilizer.Motion> CoarseGuesses(AlignmentImage donor, AlignmentImage basis,
        CancellationToken token)
    {
        int radius = Math.Min(24, Math.Min(donor.Width, donor.Height) / 20);
        double best = double.MaxValue;
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
            }
        }
        // Refine distinct plausible basins, not neighboring points in the same basin. Broad coarse
        // minima from subject movement are not ambiguity unless the refined transforms disagree.
        var guesses = new List<VideoFrameStabilizer.Motion>();
        foreach (var candidate in candidates.OrderBy(candidate => candidate.Error))
        {
            if (candidate.Error > best * 1.10 + samples * 0.5) break;
            if (guesses.Any(guess => Math.Abs(guess.X - candidate.X) + Math.Abs(guess.Y - candidate.Y) < 6))
                continue;
            guesses.Add(new(candidate.X, candidate.Y, 0));
        }
        return guesses;
    }

    /// <summary>
    /// Independent RGB holdout grid, including low-gradient pixels. A good luma fit alone is not
    /// evidence of safe geometry or matching exposure/white balance. Require distributed support.
    /// </summary>
    private static (string? Reason, double Fraction) ValidateAgreement(byte[] donor, byte[] basis, int width, int height,
        PhotoFuseTransform transform, CancellationToken token)
    {
        int total = 0, supported = 0, supportedTiles = 0;
        var fractions = new List<double>();
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
            if (count < 100) return ("Too few RGB holdout samples.", 0);
            fractions.Add(inliers / (double)count);
            total += count;
            supported += inliers;
            if (inliers >= count * 0.70) supportedTiles++;
        }
        double fraction = supported / (double)total;
        // Moving people may occupy a tile. Still require a majority of distributed tiles and
        // most holdout pixels to agree; local replacement seams are checked independently.
        bool accepted = supportedTiles >= 5 && fraction >= 0.70 && fractions.All(value => value >= 0.40);
        return (accepted ? null : $"RGB support {fraction:F3}, tiles {supportedTiles}/9: " +
            string.Join(",", fractions.Select(value => value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))),
            fraction);
    }

    private static string? ValidateLocalGeometry(AlignmentImage donor, AlignmentImage basis,
        VideoFrameStabilizer.Motion motion, CancellationToken token)
    {
        int patchWidth = donor.Width / 3, patchHeight = donor.Height / 3;
        var transform = new PhotoFuseTransform(motion.X, motion.Y, motion.Angle);
        int supported = 0;
        for (int tileY = 0; tileY < 3; tileY++)
        for (int tileX = 0; tileX < 3; tileX++)
        {
            var first = new byte[patchWidth * patchHeight];
            var second = new byte[first.Length];
            for (int y = 0; y < patchHeight; y++)
            for (int x = 0; x < patchWidth; x++)
            {
                int bx = tileX * patchWidth + x, by = tileY * patchHeight + y;
                var point = transform.SourceAt(bx, by, donor.Width, donor.Height);
                if (Inside(point.X, point.Y, donor.Width, donor.Height))
                    first[y * patchWidth + x] = (byte)Math.Clamp(
                        Bilinear(donor.Pixels, donor.Width, point.X, point.Y), 0, 255);
                second[y * patchWidth + x] = (byte)basis.Pixels[by * basis.Width + bx];
            }
            var local = RigidAligner.Align(AlignmentImage.FromNv12(first, patchWidth, patchHeight, 1, token),
                AlignmentImage.FromNv12(second, patchWidth, patchHeight, 1, token), default, token, fitShear: false);
            if (local is not { InlierFraction: >= 0.85 } fit) continue;
            double displacement = Math.Sqrt(fit.Motion.X * fit.Motion.X + fit.Motion.Y * fit.Motion.Y);
            if (displacement <= 0.4 && Math.Abs(fit.Motion.Angle) * Math.Max(patchWidth, patchHeight) / 2 <= 0.4)
                supported++;
        }
        return supported >= 5 ? null : $"Local rigid correspondence support {supported}/9; zoom or parallax.";
    }

    private static double Bilinear(float[] pixels, int width, double x, double y)
    {
        int ix = (int)x, iy = (int)y;
        double fx = x - ix, fy = y - iy;
        int index = iy * width + ix;
        double top = pixels[index] + (pixels[index + 1] - pixels[index]) * fx;
        double bottom = pixels[index + width] + (pixels[index + width + 1] - pixels[index + width]) * fx;
        return top + (bottom - top) * fy;
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

}
