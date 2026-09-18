namespace PhotoLibrarian.Inference;

internal static class ImageResampler
{
    private const int Precision = 22;

    // Match the antialiased, two-pass byte-image resize used by the model
    // processors. WIC's resize kernels otherwise change the embedding scores.
    public static byte[] Resize(
        byte[] source, int width, int height,
        int targetWidth, int targetHeight, bool cubic,
        CancellationToken cancellationToken = default)
    {
        var horizontal = source;
        if (width != targetWidth)
        {
            var weights = CreateWeights(width, targetWidth, cubic);
            horizontal = new byte[checked(targetWidth * height * 4)];
            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < targetWidth; x++)
                {
                    for (var channel = 0; channel < 3; channel++)
                    {
                        long sum = 1 << (Precision - 1);
                        var weight = weights[x];
                        for (var k = 0; k < weight.Coefficients.Length; k++)
                        {
                            sum += (long)source[(y * width + weight.First + k) * 4 + channel] *
                                weight.Coefficients[k];
                        }
                        horizontal[(y * targetWidth + x) * 4 + channel] = ToByte(sum);
                    }
                }
            }
        }

        if (height == targetHeight)
        {
            return horizontal;
        }

        var vertical = new byte[checked(targetWidth * targetHeight * 4)];
        var verticalWeights = CreateWeights(height, targetHeight, cubic);
        for (var y = 0; y < targetHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var weight = verticalWeights[y];
            for (var x = 0; x < targetWidth; x++)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    long sum = 1 << (Precision - 1);
                    for (var k = 0; k < weight.Coefficients.Length; k++)
                    {
                        sum += (long)horizontal[((weight.First + k) * targetWidth + x) * 4 + channel] *
                            weight.Coefficients[k];
                    }
                    vertical[(y * targetWidth + x) * 4 + channel] = ToByte(sum);
                }
            }
        }

        return vertical;
    }

    private static byte ToByte(long sum) =>
        (byte)Math.Clamp(sum >> Precision, 0, 255);

    private static Weight[] CreateWeights(int sourceSize, int targetSize, bool cubic)
    {
        var scale = sourceSize / (double)targetSize;
        var filterScale = Math.Max(1, scale);
        var support = filterScale * (cubic ? 2 : 1);
        var weights = new Weight[targetSize];
        for (var index = 0; index < targetSize; index++)
        {
            var center = (index + 0.5) * scale;
            var first = Math.Max(0, (int)(center - support + 0.5));
            var last = Math.Min(sourceSize, (int)(center + support + 0.5));
            var coefficients = new double[last - first];
            for (var k = 0; k < coefficients.Length; k++)
            {
                var distance = Math.Abs((k + first - center + 0.5) / filterScale);
                coefficients[k] = cubic
                    ? Cubic(distance)
                    : Math.Max(0, 1 - distance);
            }
            var sum = coefficients.Sum();
            weights[index] = new Weight(first, coefficients
                .Select(value => (int)Math.Round(
                    value / sum * (1 << Precision), MidpointRounding.AwayFromZero))
                .ToArray());
        }
        return weights;
    }

    private static double Cubic(double distance) =>
        distance < 1
            ? ((1.5 * distance - 2.5) * distance) * distance + 1
            : distance < 2
                ? ((-0.5 * distance + 2.5) * distance - 4) * distance + 2
                : 0;

    private sealed record Weight(int First, int[] Coefficients);
}
