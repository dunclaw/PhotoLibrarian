using Microsoft.ML.OnnxRuntime.Tensors;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PhotoLibrarian.ML.Services;

internal sealed class ImagePixelData
{
    private ImagePixelData(int width, int height, byte[] bgra)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }

    public static async Task<ImagePixelData> LoadAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream()).AsTask(cancellationToken);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken);

        var byteCount = checked((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
        var buffer = new Windows.Storage.Streams.Buffer(byteCount);
        bitmap.CopyToBuffer(buffer);

        var pixels = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(pixels);
        return new ImagePixelData(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }

    public ResizedImage ResizeToFit(int maximumDimension, int divisor)
    {
        var scale = Math.Min(
            (float)maximumDimension / Width,
            (float)maximumDimension / Height);
        var scaledWidth = Math.Max(1, (int)MathF.Round(Width * scale));
        var scaledHeight = Math.Max(1, (int)MathF.Round(Height * scale));
        var paddedWidth = RoundUp(scaledWidth, divisor);
        var paddedHeight = RoundUp(scaledHeight, divisor);
        var resized = new byte[checked(paddedWidth * paddedHeight * 4)];

        for (var y = 0; y < scaledHeight; y++)
        {
            var sourceY = (y + 0.5f) / scale - 0.5f;
            for (var x = 0; x < scaledWidth; x++)
            {
                var sourceX = (x + 0.5f) / scale - 0.5f;
                SampleBilinear(sourceX, sourceY, resized, (y * paddedWidth + x) * 4);
            }
        }

        return new ResizedImage(paddedWidth, paddedHeight, scale, resized);
    }

    public DenseTensor<float> CreateAlignedFaceTensor(DetectedFace face, int targetSize)
    {
        if (face.Landmarks.Count != 5)
        {
            throw new ArgumentException("Five landmarks are required to align a face.", nameof(face));
        }

        ReadOnlySpan<(double X, double Y)> target =
        [
            (38.2946, 51.6963),
            (73.5318, 51.5014),
            (56.0252, 71.7366),
            (41.5493, 92.3655),
            (70.7299, 92.2041)
        ];

        var source = face.Landmarks
            .Select(point => ((double)(point.X * Width), (double)(point.Y * Height)))
            .ToArray();
        var (a, b, translateX, translateY) = SolveSimilarityTransform(source, target);
        var determinant = a * a + b * b;
        if (determinant < 1e-12)
        {
            throw new InvalidDataException("Face landmarks do not define a valid alignment.");
        }

        var tensor = new DenseTensor<float>([1, 3, targetSize, targetSize]);
        var sample = new byte[4];
        for (var y = 0; y < targetSize; y++)
        {
            for (var x = 0; x < targetSize; x++)
            {
                var translatedX = x - translateX;
                var translatedY = y - translateY;
                var sourceX = (a * translatedX + b * translatedY) / determinant;
                var sourceY = (-b * translatedX + a * translatedY) / determinant;
                SampleBilinear((float)sourceX, (float)sourceY, sample, 0);

                tensor[0, 0, y, x] = sample[2];
                tensor[0, 1, y, x] = sample[1];
                tensor[0, 2, y, x] = sample[0];
            }
        }

        return tensor;
    }

    private void SampleBilinear(float x, float y, byte[] destination, int destinationIndex)
    {
        if (x < 0 || y < 0 || x > Width - 1 || y > Height - 1)
        {
            destination.AsSpan(destinationIndex, 4).Clear();
            return;
        }

        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var x1 = Math.Min(x0 + 1, Width - 1);
        var y1 = Math.Min(y0 + 1, Height - 1);
        var xWeight = x - x0;
        var yWeight = y - y0;

        for (var channel = 0; channel < 4; channel++)
        {
            var topLeft = Bgra[(y0 * Width + x0) * 4 + channel];
            var topRight = Bgra[(y0 * Width + x1) * 4 + channel];
            var bottomLeft = Bgra[(y1 * Width + x0) * 4 + channel];
            var bottomRight = Bgra[(y1 * Width + x1) * 4 + channel];
            var top = topLeft + (topRight - topLeft) * xWeight;
            var bottom = bottomLeft + (bottomRight - bottomLeft) * xWeight;
            destination[destinationIndex + channel] =
                (byte)Math.Clamp(MathF.Round(top + (bottom - top) * yWeight), 0, 255);
        }
    }

    internal static (double A, double B, double TranslateX, double TranslateY)
        SolveSimilarityTransform(
            IReadOnlyList<(double X, double Y)> source,
            ReadOnlySpan<(double X, double Y)> target)
    {
        var normal = new double[4, 5];
        for (var index = 0; index < source.Count; index++)
        {
            AddEquation([source[index].X, -source[index].Y, 1, 0], target[index].X, normal);
            AddEquation([source[index].Y, source[index].X, 0, 1], target[index].Y, normal);
        }

        for (var pivot = 0; pivot < 4; pivot++)
        {
            var bestRow = pivot;
            for (var row = pivot + 1; row < 4; row++)
            {
                if (Math.Abs(normal[row, pivot]) > Math.Abs(normal[bestRow, pivot]))
                {
                    bestRow = row;
                }
            }

            if (Math.Abs(normal[bestRow, pivot]) < 1e-12)
            {
                throw new InvalidDataException("Face landmarks do not define a valid alignment.");
            }

            for (var column = pivot; column < 5; column++)
            {
                (normal[pivot, column], normal[bestRow, column]) =
                    (normal[bestRow, column], normal[pivot, column]);
            }

            var divisor = normal[pivot, pivot];
            for (var column = pivot; column < 5; column++)
            {
                normal[pivot, column] /= divisor;
            }

            for (var row = 0; row < 4; row++)
            {
                if (row == pivot)
                {
                    continue;
                }

                var factor = normal[row, pivot];
                for (var column = pivot; column < 5; column++)
                {
                    normal[row, column] -= factor * normal[pivot, column];
                }
            }
        }

        return (normal[0, 4], normal[1, 4], normal[2, 4], normal[3, 4]);
    }

    private static void AddEquation(
        ReadOnlySpan<double> coefficients,
        double target,
        double[,] normal)
    {
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                normal[row, column] += coefficients[row] * coefficients[column];
            }

            normal[row, 4] += coefficients[row] * target;
        }
    }

    private static int RoundUp(int value, int divisor) =>
        ((value + divisor - 1) / divisor) * divisor;
}

internal sealed record ResizedImage(int Width, int Height, float Scale, byte[] Bgra)
{
    public DenseTensor<float> ToBgrTensor()
    {
        var tensor = new DenseTensor<float>([1, 3, Height, Width]);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var index = (y * Width + x) * 4;
                tensor[0, 0, y, x] = Bgra[index];
                tensor[0, 1, y, x] = Bgra[index + 1];
                tensor[0, 2, y, x] = Bgra[index + 2];
            }
        }

        return tensor;
    }
}
