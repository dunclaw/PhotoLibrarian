// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Services;

/// <summary>A rectangle in oriented base-preview pixels; frame zero is the base, not a donor.</summary>
public sealed record PhotoFuseRegion(int Frame, int Left, int Top, int Width, int Height);

internal static class PhotoFuseComposer
{
    internal const int MaximumRegions = 8;
    private static readonly double[] Linear = Enumerable.Range(0, 256).Select(value => ToLinear(value)).ToArray();

    internal static byte[] Render(IReadOnlyList<byte[]> frames, IReadOnlyList<PhotoFuseTransform> transforms,
        int width, int height, IReadOnlyList<PhotoFuseRegion> regions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (regions.Count > MaximumRegions)
            throw new ArgumentException($"Choose at most {MaximumRegions} regions.");
        for (int i = 0; i < regions.Count; i++)
        for (int j = i + 1; j < regions.Count; j++)
        {
            var region = regions[i];
            var other = regions[j];
            if (region.Left < other.Left + (long)other.Width && other.Left < region.Left + (long)region.Width &&
                region.Top < other.Top + (long)other.Height && other.Top < region.Top + (long)region.Height)
                throw new ArgumentException("Regions must not overlap. Remove or resize an existing region.");
        }
        var output = (byte[])frames[0].Clone();
        foreach (var region in regions)
        {
            Validate(region, transforms, width, height);
            var donor = frames[region.Frame];
            var transform = transforms[region.Frame];
            int feather = Math.Clamp(Math.Min(region.Width, region.Height) / 8, 2, 16);
            ValidateBoundary(donor, frames[0], transform, width, height, region, feather, token);
            for (int y = region.Top; y < region.Top + region.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = region.Left; x < region.Left + region.Width; x++)
                {
                    int edge = Math.Min(Math.Min(x - region.Left, region.Left + region.Width - 1 - x),
                        Math.Min(y - region.Top, region.Top + region.Height - 1 - y));
                    double weight = Math.Clamp(edge / (double)feather, 0, 1);
                    var p = transform.SourceAt(x, y, width, height);
                    int index = (y * width + x) * 4;
                    for (int c = 0; c < 3; c++)
                    {
                        double value = PhotoFuseRegistration.Sample(donor, width, p.X, p.Y, c);
                        // Feather in linear light rather than darkening the seam in gamma space.
                        double linear = ToLinear(value) * weight + Linear[output[index + c]] * (1 - weight);
                        output[index + c] = FromLinear(linear);
                    }
                }
            }
        }
        return output;
    }

    internal static byte[] AlignedPreview(byte[] donor, byte[] basis, PhotoFuseTransform transform,
        int width, int height, CancellationToken token)
    {
        var output = (byte[])basis.Clone();
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                var p = transform.SourceAt(x, y, width, height);
                if (!PhotoFuseRegistration.Inside(p.X, p.Y, width, height)) continue;
                for (int c = 0; c < 3; c++)
                    output[(y * width + x) * 4 + c] =
                        (byte)Math.Clamp(Math.Round(PhotoFuseRegistration.Sample(donor, width, p.X, p.Y, c)), 0, 255);
            }
        }
        return output;
    }

    private static void Validate(PhotoFuseRegion region,
        IReadOnlyList<PhotoFuseTransform> transforms, int width, int height)
    {
        if (region.Frame < 1 || region.Frame >= transforms.Count || region.Left < 0 || region.Top < 0 ||
            region.Width < 16 || region.Height < 16 || region.Width > width || region.Height > height ||
            region.Left > width - region.Width || region.Top > height - region.Height)
            throw new ArgumentException("Choose a donor and a region at least 16 pixels wide and high, inside the base.");
        foreach (int y in new[] { region.Top, region.Top + region.Height - 1 })
        foreach (int x in new[] { region.Left, region.Left + region.Width - 1 })
        {
            var p = transforms[region.Frame].SourceAt(x, y, width, height);
            if (!PhotoFuseRegistration.Inside(p.X, p.Y, width, height))
                throw new ArgumentException("This region extends outside the aligned donor. Move it away from the edge.");
        }
    }

    private static void ValidateBoundary(byte[] donor, byte[] basis, PhotoFuseTransform transform,
        int width, int height, PhotoFuseRegion region, int feather, CancellationToken token)
    {
        int count = 0, inliers = 0;
        for (int y = region.Top; y < region.Top + region.Height; y += 2)
        {
            token.ThrowIfCancellationRequested();
            for (int x = region.Left; x < region.Left + region.Width; x += 2)
            {
                int edge = Math.Min(Math.Min(x - region.Left, region.Left + region.Width - 1 - x),
                    Math.Min(y - region.Top, region.Top + region.Height - 1 - y));
                if (edge >= feather) continue;
                var p = transform.SourceAt(x, y, width, height);
                double error = 0;
                for (int c = 0; c < 3; c++)
                    error = Math.Max(error, Math.Abs(PhotoFuseRegistration.Sample(donor, width, p.X, p.Y, c) -
                        basis[(y * width + x) * 4 + c]));
                count++;
                if (error <= 24) inliers++;
            }
        }
        if (count == 0 || inliers < count * 0.90)
            throw new InvalidDataException("This region's border does not match the base. Include stationary " +
                "background around the entire subject, or use another shot. No composite was accepted.");
    }

    private static double ToLinear(double value)
    {
        value /= 255;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static byte FromLinear(double value) => (byte)Math.Clamp(Math.Round(255 *
        (value <= 0.0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055)), 0, 255);
}
