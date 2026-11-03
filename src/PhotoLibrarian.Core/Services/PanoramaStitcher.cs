// Copyright (c) Duncan Lawler. All Rights Reserved.
namespace PhotoLibrarian.Core.Services;

internal static class PanoramaStitcher
{
    private const int FeatureMaximumDimension = 640;
    private const int MaximumFeatures = 900;
    private const int MinimumInliers = 12;
    private const int RansacIterations = 900;
    private const double RansacThresholdPixels = 3.5;

    internal static PanoramaStitchResult Stitch(
        WindowsImagingCodec.DecodedImage first,
        WindowsImagingCodec.DecodedImage second,
        string firstPath,
        string secondPath,
        IProgress<PanoramaStitchProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new("Finding image features", 25));
        var firstFeatures = FeatureExtractor.Detect(first, cancellationToken);
        var secondFeatures = FeatureExtractor.Detect(second, cancellationToken);
        if (firstFeatures.Features.Count < MinimumInliers ||
            secondFeatures.Features.Count < MinimumInliers)
            throw new InvalidDataException("These photos do not contain enough distinct detail to align safely.");

        progress?.Report(new("Matching overlapping detail", 42));
        var matches = MatchFeatures(firstFeatures.Features, secondFeatures.Features);
        if (matches.Count < MinimumInliers)
            throw new InvalidDataException("No reliable overlap was found. Choose two photos with a clear shared area.");

        progress?.Report(new("Checking alignment", 54));
        var homography = FindHomography(
            matches, firstFeatures, secondFeatures, cancellationToken);
        if (homography.Inliers.Count < MinimumInliers ||
            (double)homography.Inliers.Count / matches.Count < 0.2)
            throw new InvalidDataException("The photos could not be aligned with enough confidence.");
        if (HasCompetingAlignment(
                matches,
                homography,
                firstFeatures,
                secondFeatures,
                cancellationToken))
            throw new InvalidDataException(
                "Different parts of the scene suggest incompatible alignments. This can happen with strong parallax or moving subjects.");
        EnsureInlierCoverage(homography.Inliers, matches, firstFeatures, secondFeatures);

        var projected = ProjectSecondCorners(
            homography.Matrix,
            (int)second.Width,
            (int)second.Height,
            (int)first.Width,
            (int)first.Height);
        var minX = Math.Min(0, projected.Min(point => point.X));
        var minY = Math.Min(0, projected.Min(point => point.Y));
        var maxX = Math.Max(first.Width - 1, projected.Max(point => point.X));
        var maxY = Math.Max(first.Height - 1, projected.Max(point => point.Y));
        if (!double.IsFinite(minX) || !double.IsFinite(minY) ||
            !double.IsFinite(maxX) || !double.IsFinite(maxY))
            throw new InvalidDataException("The alignment produced invalid image bounds.");
        if (maxX - minX > 6400 || maxY - minY > 6400)
            throw new InvalidDataException("The alignment would create an excessively stretched panorama.");

        var canvasWidth = checked((int)Math.Ceiling(maxX - minX + 1));
        var canvasHeight = checked((int)Math.Ceiling(maxY - minY + 1));
        var canvasPixels = checked((long)canvasWidth * canvasHeight);
        if (canvasWidth <= 0 || canvasHeight <= 0 ||
            canvasPixels > PanoramaStitchingService.MaximumOutputPixels ||
            canvasWidth > 6400 || canvasHeight > 6400)
            throw new InvalidDataException("The panorama would exceed the safe output size limit.");

        var inverse = Invert(homography.Matrix)
            ?? throw new InvalidDataException("The computed photo alignment is unstable.");
        var gains = EstimateExposureGains(
            matches, homography.Inliers, firstFeatures, secondFeatures, first, second);

        progress?.Report(new("Blending and cropping panorama", 67));
        var canvas = Blend(
            first, second, inverse,
            minX, minY, canvasWidth, canvasHeight, gains, cancellationToken);
        var crop = FindLargestOpaqueRectangle(canvas.Mask, canvasWidth, canvasHeight, cancellationToken);
        var cropPixels = checked((long)crop.Width * crop.Height);
        if (crop.Width < 64 || crop.Height < 64 || cropPixels < canvasPixels * 0.35)
            throw new InvalidDataException(
                "The alignment leaves too little clean, rectangular image area. Choose photos with more overlap and less perspective or parallax.");

        var output = new byte[checked(crop.Width * crop.Height * 4)];
        for (var y = 0; y < crop.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Buffer.BlockCopy(
                canvas.Pixels,
                checked(((crop.Top + y) * canvasWidth + crop.Left) * 4),
                output,
                checked(y * crop.Width * 4),
                checked(crop.Width * 4));
        }

        var ratio = (double)homography.Inliers.Count / matches.Count;
        var averageGain = gains.Average();
        progress?.Report(new("Panorama preview ready", 100));
        return new PanoramaStitchResult(
            output,
            crop.Width,
            crop.Height,
            firstPath,
            secondPath,
            matches.Count,
            homography.Inliers.Count,
            ratio,
            averageGain);
    }

    private static List<Match> MatchFeatures(
        IReadOnlyList<Feature> first,
        IReadOnlyList<Feature> second)
    {
        var firstToSecond = FindNearest(first, second, ratioTest: true);
        var secondToFirst = FindNearest(second, first, ratioTest: true);
        var matches = new List<Match>();
        for (var index = 0; index < firstToSecond.Length; index++)
        {
            var other = firstToSecond[index];
            if (other >= 0 && secondToFirst[other] == index)
                matches.Add(new(index, other));
        }
        return matches;
    }

    private static int[] FindNearest(
        IReadOnlyList<Feature> query,
        IReadOnlyList<Feature> target,
        bool ratioTest)
    {
        var nearest = new int[query.Count];
        Array.Fill(nearest, -1);
        for (var queryIndex = 0; queryIndex < query.Count; queryIndex++)
        {
            var best = int.MaxValue;
            var secondBest = int.MaxValue;
            var bestIndex = -1;
            for (var targetIndex = 0; targetIndex < target.Count; targetIndex++)
            {
                var distance = HammingDistance(query[queryIndex], target[targetIndex]);
                if (distance < best)
                {
                    secondBest = best;
                    best = distance;
                    bestIndex = targetIndex;
                }
                else if (distance < secondBest)
                {
                    secondBest = distance;
                }
            }

            if (bestIndex >= 0 && best <= 112 &&
                (!ratioTest || secondBest == int.MaxValue || best < secondBest * 0.78))
                nearest[queryIndex] = bestIndex;
        }
        return nearest;
    }

    private static int HammingDistance(Feature left, Feature right) =>
        System.Numerics.BitOperations.PopCount(left.Descriptor0 ^ right.Descriptor0) +
        System.Numerics.BitOperations.PopCount(left.Descriptor1 ^ right.Descriptor1) +
        System.Numerics.BitOperations.PopCount(left.Descriptor2 ^ right.Descriptor2) +
        System.Numerics.BitOperations.PopCount(left.Descriptor3 ^ right.Descriptor3);

    private static HomographyResult FindHomography(
        IReadOnlyList<Match> matches,
        FeatureSet first,
        FeatureSet second,
        CancellationToken cancellationToken)
    {
        var random = new Random(0x50414e);
        double[]? bestMatrix = null;
        List<int> bestInliers = [];
        var sample = new int[4];
        for (var iteration = 0; iteration < RansacIterations; iteration++)
        {
            if ((iteration & 31) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < sample.Length; index++)
            {
                int candidate;
                bool duplicate;
                do
                {
                    candidate = random.Next(matches.Count);
                    duplicate = false;
                    for (var prior = 0; prior < index; prior++)
                        duplicate |= sample[prior] == candidate;
                } while (duplicate);
                sample[index] = candidate;
            }

            var matrix = EstimateMatrix(matches, sample, first, second);
            if (matrix is null)
                continue;
            var inliers = FindInliers(matches, matrix, first, second);
            if (inliers.Count > bestInliers.Count)
            {
                bestMatrix = matrix;
                bestInliers = inliers;
            }
        }

        if (bestMatrix is null || bestInliers.Count < MinimumInliers)
            throw new InvalidDataException("The photos do not have a stable shared feature pattern.");
        var refined = EstimateMatrix(matches, bestInliers, first, second)
            ?? throw new InvalidDataException("The photo alignment became unstable during refinement.");
        var refinedInliers = FindInliers(matches, refined, first, second);
        return new(refined, refinedInliers);
    }

    private static bool HasCompetingAlignment(
        IReadOnlyList<Match> matches,
        HomographyResult best,
        FeatureSet first,
        FeatureSet second,
        CancellationToken cancellationToken)
    {
        var excluded = best.Inliers.ToHashSet();
        var remaining = Enumerable.Range(0, matches.Count)
            .Where(index => !excluded.Contains(index))
            .ToArray();
        if (remaining.Length < MinimumInliers * 2)
            return false;

        var random = new Random(0x50414e + 1);
        double[]? bestAlternate = null;
        List<int> alternateInliers = [];
        var sample = new int[4];
        for (var iteration = 0; iteration < 260; iteration++)
        {
            if ((iteration & 31) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < sample.Length; index++)
            {
                int candidate;
                bool duplicate;
                do
                {
                    candidate = remaining[random.Next(remaining.Length)];
                    duplicate = false;
                    for (var prior = 0; prior < index; prior++)
                        duplicate |= sample[prior] == candidate;
                } while (duplicate);
                sample[index] = candidate;
            }

            var matrix = EstimateMatrix(matches, sample, first, second);
            if (matrix is null)
                continue;
            var inliers = FindInliers(matches, matrix, first, second)
                .Where(index => !excluded.Contains(index))
                .ToList();
            if (inliers.Count > alternateInliers.Count)
            {
                bestAlternate = matrix;
                alternateInliers = inliers;
            }
        }

        var requiredSupport = Math.Max(MinimumInliers, (int)Math.Ceiling(matches.Count * 0.18));
        if (bestAlternate is null || alternateInliers.Count < requiredSupport)
            return false;

        var totalDifference = 0d;
        foreach (var index in alternateInliers)
        {
            var source = second.Features[matches[index].SecondIndex];
            var x = source.X / (second.Width - 1d);
            var y = source.Y / (second.Height - 1d);
            if (!TryProject(best.Matrix, x, y, out var bestX, out var bestY) ||
                !TryProject(bestAlternate, x, y, out var alternateX, out var alternateY))
                continue;
            var differenceX = (bestX - alternateX) * (first.Width - 1);
            var differenceY = (bestY - alternateY) * (first.Height - 1);
            totalDifference += Math.Sqrt(differenceX * differenceX + differenceY * differenceY);
        }
        return alternateInliers.Count > 0 &&
            totalDifference / alternateInliers.Count > 8;
    }

    private static List<int> FindInliers(
        IReadOnlyList<Match> matches,
        double[] matrix,
        FeatureSet first,
        FeatureSet second)
    {
        var inliers = new List<int>();
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            var source = second.Features[match.SecondIndex];
            var destination = first.Features[match.FirstIndex];
            if (!TryProject(matrix, source.X / (second.Width - 1d),
                    source.Y / (second.Height - 1d), out var x, out var y))
                continue;
            var errorX = (x - destination.X / (first.Width - 1d)) * (first.Width - 1);
            var errorY = (y - destination.Y / (first.Height - 1d)) * (first.Height - 1);
            if (errorX * errorX + errorY * errorY <= RansacThresholdPixels * RansacThresholdPixels)
                inliers.Add(index);
        }
        return inliers;
    }

    private static double[]? EstimateMatrix(
        IReadOnlyList<Match> matches,
        IReadOnlyList<int> selected,
        FeatureSet first,
        FeatureSet second)
    {
        if (selected.Count < 4)
            return null;
        var normal = new double[8, 8];
        var rhs = new double[8];
        foreach (var matchIndex in selected)
        {
            var match = matches[matchIndex];
            var source = second.Features[match.SecondIndex];
            var destination = first.Features[match.FirstIndex];
            var x = source.X / (second.Width - 1d);
            var y = source.Y / (second.Height - 1d);
            var u = destination.X / (first.Width - 1d);
            var v = destination.Y / (first.Height - 1d);
            AccumulateEquation(normal, rhs, [x, y, 1, 0, 0, 0, -u * x, -u * y], u);
            AccumulateEquation(normal, rhs, [0, 0, 0, x, y, 1, -v * x, -v * y], v);
        }

        return Solve(normal, rhs);
    }

    private static void AccumulateEquation(double[,] normal, double[] rhs, double[] row, double value)
    {
        for (var left = 0; left < 8; left++)
        {
            rhs[left] += row[left] * value;
            for (var right = 0; right < 8; right++)
                normal[left, right] += row[left] * row[right];
        }
    }

    private static double[]? Solve(double[,] matrix, double[] rhs)
    {
        var augmented = new double[8, 9];
        for (var row = 0; row < 8; row++)
        {
            for (var column = 0; column < 8; column++)
                augmented[row, column] = matrix[row, column];
            augmented[row, 8] = rhs[row];
        }

        for (var column = 0; column < 8; column++)
        {
            var pivot = column;
            for (var row = column + 1; row < 8; row++)
            {
                if (Math.Abs(augmented[row, column]) > Math.Abs(augmented[pivot, column]))
                    pivot = row;
            }
            if (Math.Abs(augmented[pivot, column]) < 1e-10)
                return null;
            if (pivot != column)
            {
                for (var item = column; item < 9; item++)
                    (augmented[column, item], augmented[pivot, item]) =
                        (augmented[pivot, item], augmented[column, item]);
            }

            var divisor = augmented[column, column];
            for (var item = column; item < 9; item++)
                augmented[column, item] /= divisor;
            for (var row = 0; row < 8; row++)
            {
                if (row == column)
                    continue;
                var factor = augmented[row, column];
                for (var item = column; item < 9; item++)
                    augmented[row, item] -= factor * augmented[column, item];
            }
        }

        var result = new double[9];
        for (var index = 0; index < 8; index++)
            result[index] = augmented[index, 8];
        result[8] = 1;
        return result;
    }

    private static void EnsureInlierCoverage(
        IReadOnlyList<int> inliers,
        IReadOnlyList<Match> matches,
        FeatureSet first,
        FeatureSet second)
    {
        var firstPoints = new List<Feature>(inliers.Count);
        var secondPoints = new List<Feature>(inliers.Count);
        foreach (var index in inliers)
        {
            var match = matches[index];
            firstPoints.Add(first.Features[match.FirstIndex]);
            secondPoints.Add(second.Features[match.SecondIndex]);
        }
        if (CoverageTooSmall(firstPoints, first.Width, first.Height) ||
            CoverageTooSmall(secondPoints, second.Width, second.Height))
            throw new InvalidDataException(
                "The matching detail is concentrated in a small area, so the panorama alignment is not trustworthy.");
    }

    private static bool CoverageTooSmall(List<Feature> points, int width, int height)
    {
        var spanX = points.Max(point => point.X) - points.Min(point => point.X);
        var spanY = points.Max(point => point.Y) - points.Min(point => point.Y);
        return spanX < width * 0.08 || spanY < height * 0.08;
    }

    private static List<Point> ProjectSecondCorners(
        double[] matrix,
        int secondWidth,
        int secondHeight,
        int firstWidth,
        int firstHeight)
    {
        var corners = new[]
        {
            new Point(0, 0),
            new Point(secondWidth - 1, 0),
            new Point(0, secondHeight - 1),
            new Point(secondWidth - 1, secondHeight - 1)
        };
        var projected = new List<Point>(corners.Length);
        foreach (var corner in corners)
        {
            if (!TryProject(matrix, corner.X / (secondWidth - 1d),
                    corner.Y / (secondHeight - 1d), out var x, out var y))
                throw new InvalidDataException("The photo alignment projects image edges to infinity.");
            projected.Add(new Point(x * (firstWidth - 1), y * (firstHeight - 1)));
        }
        return projected;
    }

    private static bool TryProject(
        double[] matrix,
        double x,
        double y,
        out double projectedX,
        out double projectedY)
    {
        var denominator = matrix[6] * x + matrix[7] * y + matrix[8];
        if (!double.IsFinite(denominator) || Math.Abs(denominator) < 1e-8)
        {
            projectedX = 0;
            projectedY = 0;
            return false;
        }
        projectedX = (matrix[0] * x + matrix[1] * y + matrix[2]) / denominator;
        projectedY = (matrix[3] * x + matrix[4] * y + matrix[5]) / denominator;
        return double.IsFinite(projectedX) && double.IsFinite(projectedY);
    }

    private static double[]? Invert(double[] matrix)
    {
        var a = matrix[0];
        var b = matrix[1];
        var c = matrix[2];
        var d = matrix[3];
        var e = matrix[4];
        var f = matrix[5];
        var g = matrix[6];
        var h = matrix[7];
        var i = matrix[8];
        var determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-10)
            return null;
        var inverse = new[]
        {
            (e * i - f * h) / determinant, (c * h - b * i) / determinant, (b * f - c * e) / determinant,
            (f * g - d * i) / determinant, (a * i - c * g) / determinant, (c * d - a * f) / determinant,
            (d * h - e * g) / determinant, (b * g - a * h) / determinant, (a * e - b * d) / determinant
        };
        return inverse.All(double.IsFinite) ? inverse : null;
    }

    private static double[] EstimateExposureGains(
        IReadOnlyList<Match> matches,
        IReadOnlyList<int> inliers,
        FeatureSet firstFeatures,
        FeatureSet secondFeatures,
        WindowsImagingCodec.DecodedImage first,
        WindowsImagingCodec.DecodedImage second)
    {
        var ratios = new[] { new List<double>(), new List<double>(), new List<double>() };
        foreach (var matchIndex in inliers)
        {
            var match = matches[matchIndex];
            var firstFeature = firstFeatures.Features[match.FirstIndex];
            var secondFeature = secondFeatures.Features[match.SecondIndex];
            var firstX = Math.Clamp(
                (int)Math.Round(firstFeature.X * first.Width / firstFeatures.Width), 0, (int)first.Width - 1);
            var firstY = Math.Clamp(
                (int)Math.Round(firstFeature.Y * first.Height / firstFeatures.Height), 0, (int)first.Height - 1);
            var secondX = Math.Clamp(
                (int)Math.Round(secondFeature.X * second.Width / secondFeatures.Width), 0, (int)second.Width - 1);
            var secondY = Math.Clamp(
                (int)Math.Round(secondFeature.Y * second.Height / secondFeatures.Height), 0, (int)second.Height - 1);
            var firstOffset = (firstY * (int)first.Width + firstX) * 4;
            var secondOffset = (secondY * (int)second.Width + secondX) * 4;
            for (var channel = 0; channel < 3; channel++)
            {
                var a = Flatten(first.Pixels, firstOffset, channel);
                var b = Flatten(second.Pixels, secondOffset, channel);
                if (a is > 12 and < 242 && b is > 12 and < 242)
                    ratios[channel].Add(
                        SrgbToLinear(a / 255d) / SrgbToLinear(b / 255d));
            }
        }

        var gains = new double[3];
        for (var channel = 0; channel < gains.Length; channel++)
        {
            if (ratios[channel].Count == 0)
            {
                gains[channel] = 1;
                continue;
            }
            ratios[channel].Sort();
            gains[channel] = Math.Clamp(
                ratios[channel][ratios[channel].Count / 2], 0.65, 1.54);
        }
        return gains;
    }

    private static RenderedCanvas Blend(
        WindowsImagingCodec.DecodedImage first,
        WindowsImagingCodec.DecodedImage second,
        double[] inverse,
        double minX,
        double minY,
        int width,
        int height,
        double[] gains,
        CancellationToken cancellationToken)
    {
        var output = new byte[checked(width * height * 4)];
        var mask = new byte[checked(width * height)];
        var firstWidth = (int)first.Width;
        var firstHeight = (int)first.Height;
        var secondWidth = (int)second.Width;
        var secondHeight = (int)second.Height;

        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var globalY = y + minY;
            for (var x = 0; x < width; x++)
            {
                var globalX = x + minX;
                var inFirst = globalX >= 0 && globalY >= 0 &&
                    globalX <= firstWidth - 1 && globalY <= firstHeight - 1;
                var secondX = 0d;
                var secondY = 0d;
                var inSecond = TryProject(
                    inverse,
                    globalX / (firstWidth - 1d),
                    globalY / (firstHeight - 1d),
                    out var secondXNormalized,
                    out var secondYNormalized);
                if (inSecond)
                {
                    secondX = secondXNormalized * (secondWidth - 1);
                    secondY = secondYNormalized * (secondHeight - 1);
                    inSecond = secondX >= 0 && secondY >= 0 &&
                        secondX <= secondWidth - 1 && secondY <= secondHeight - 1;
                }

                if (!inFirst && !inSecond)
                    continue;

                var outputOffset = (y * width + x) * 4;
                var maskOffset = y * width + x;
                mask[maskOffset] = 1;
                if (inFirst && inSecond)
                {
                    var firstEdge = EdgeDistance(globalX, globalY, firstWidth, firstHeight);
                    var secondEdge = EdgeDistance(secondX, secondY, secondWidth, secondHeight);
                    var firstWeight = Math.Clamp(firstEdge / 72d, 0.15, 1);
                    var secondWeight = Math.Clamp(secondEdge / 72d, 0.15, 1);
                    var total = firstWeight + secondWeight;
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var a = SampleFlattened(first, globalX, globalY, channel);
                        var b = SampleFlattened(second, secondX, secondY, channel) / 255d;
                        var blended = (SrgbToLinear(a / 255d) * firstWeight +
                            Math.Min(1, SrgbToLinear(b) * gains[channel]) * secondWeight) / total;
                        output[outputOffset + channel] = (byte)Math.Clamp(
                            Math.Round(LinearToSrgb(blended) * 255), 0, 255);
                    }
                }
                else if (inFirst)
                {
                    for (var channel = 0; channel < 3; channel++)
                        output[outputOffset + channel] =
                            (byte)SampleFlattened(first, globalX, globalY, channel);
                }
                else
                {
                    for (var channel = 0; channel < 3; channel++)
                        output[outputOffset + channel] =
                            (byte)Math.Clamp(Math.Round(LinearToSrgb(Math.Min(
                                1,
                                SrgbToLinear(SampleFlattened(second, secondX, secondY, channel) / 255d) *
                                    gains[channel])) * 255), 0, 255);
                }
                output[outputOffset + 3] = 255;
            }
        }
        return new(output, mask);
    }

    private static double EdgeDistance(double x, double y, int width, int height) =>
        Math.Min(Math.Min(x, y), Math.Min(width - 1 - x, height - 1 - y));

    private static double SampleFlattened(
        WindowsImagingCodec.DecodedImage image,
        double x,
        double y,
        int channel)
    {
        var width = (int)image.Width;
        var height = (int)image.Height;
        var left = Math.Clamp((int)Math.Floor(x), 0, width - 1);
        var top = Math.Clamp((int)Math.Floor(y), 0, height - 1);
        var right = Math.Min(left + 1, width - 1);
        var bottom = Math.Min(top + 1, height - 1);
        var fractionX = x - left;
        var fractionY = y - top;
        var topLeft = Flatten(image.Pixels, (top * width + left) * 4, channel);
        var topRight = Flatten(image.Pixels, (top * width + right) * 4, channel);
        var bottomLeft = Flatten(image.Pixels, (bottom * width + left) * 4, channel);
        var bottomRight = Flatten(image.Pixels, (bottom * width + right) * 4, channel);
        var topValue = topLeft + (topRight - topLeft) * fractionX;
        var bottomValue = bottomLeft + (bottomRight - bottomLeft) * fractionX;
        return topValue + (bottomValue - topValue) * fractionY;
    }

    private static int Flatten(byte[] pixels, int offset, int channel) =>
        Math.Min(255, pixels[offset + channel] + 255 - pixels[offset + 3]);

    private static double SrgbToLinear(double value) =>
        value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    private static double LinearToSrgb(double value) =>
        value <= 0.0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;

    private static CropRectangle FindLargestOpaqueRectangle(
        byte[] mask,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var heights = new int[width];
        var stack = new int[width];
        var best = new CropRectangle(0, 0, 0, 0);
        long bestArea = 0;
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowOffset = y * width;
            for (var x = 0; x < width; x++)
                heights[x] = mask[rowOffset + x] == 0 ? 0 : heights[x] + 1;

            var top = -1;
            for (var x = 0; x <= width; x++)
            {
                var currentHeight = x == width ? 0 : heights[x];
                while (top >= 0 && heights[stack[top]] > currentHeight)
                {
                    var rectangleHeight = heights[stack[top--]];
                    var left = top < 0 ? 0 : stack[top] + 1;
                    var rectangleWidth = x - left;
                    var area = (long)rectangleWidth * rectangleHeight;
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = new CropRectangle(
                            left, y - rectangleHeight + 1, rectangleWidth, rectangleHeight);
                    }
                }
                if (x < width)
                    stack[++top] = x;
            }
        }
        return best;
    }

    private readonly record struct Feature(
        float X, float Y, ulong Descriptor0, ulong Descriptor1, ulong Descriptor2, ulong Descriptor3);
    private readonly record struct Match(int FirstIndex, int SecondIndex);
    private readonly record struct Point(double X, double Y);
    private readonly record struct CropRectangle(int Left, int Top, int Width, int Height);
    private sealed record FeatureSet(List<Feature> Features, int Width, int Height);
    private sealed record HomographyResult(double[] Matrix, List<int> Inliers);
    private sealed record RenderedCanvas(byte[] Pixels, byte[] Mask);

    private static class FeatureExtractor
    {
        private static readonly int[] DescriptorPattern = CreateDescriptorPattern();

        internal static FeatureSet Detect(
            WindowsImagingCodec.DecodedImage image,
            CancellationToken cancellationToken)
        {
            var scale = Math.Min(
                1d, FeatureMaximumDimension / (double)Math.Max(image.Width, image.Height));
            var width = Math.Max(1, (int)Math.Round(image.Width * scale));
            var height = Math.Max(1, (int)Math.Round(image.Height * scale));
            var gray = new byte[checked(width * height)];
            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceY = Math.Min((int)((y + 0.5) / scale), (int)image.Height - 1);
                for (var x = 0; x < width; x++)
                {
                    var sourceX = Math.Min((int)((x + 0.5) / scale), (int)image.Width - 1);
                    var offset = (sourceY * (int)image.Width + sourceX) * 4;
                    gray[y * width + x] = (byte)(
                        (Flatten(image.Pixels, offset, 2) * 77 +
                         Flatten(image.Pixels, offset, 1) * 150 +
                         Flatten(image.Pixels, offset, 0) * 29) >> 8);
                }
            }

            var gradientsX = new short[gray.Length];
            var gradientsY = new short[gray.Length];
            for (var y = 1; y < height - 1; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 1; x < width - 1; x++)
                {
                    var index = y * width + x;
                    gradientsX[index] = (short)(gray[index + 1] - gray[index - 1]);
                    gradientsY[index] = (short)(gray[index + width] - gray[index - width]);
                }
            }

            var scores = new float[gray.Length];
            var maximumScore = 0f;
            for (var y = 8; y < height - 8; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 8; x < width - 8; x++)
                {
                    var sumX = 0d;
                    var sumY = 0d;
                    var sumXY = 0d;
                    for (var py = -2; py <= 2; py++)
                    {
                        for (var px = -2; px <= 2; px++)
                        {
                            var index = (y + py) * width + x + px;
                            var gx = gradientsX[index];
                            var gy = gradientsY[index];
                            sumX += gx * gx;
                            sumY += gy * gy;
                            sumXY += gx * gy;
                        }
                    }
                    var score = sumX * sumY - sumXY * sumXY -
                        0.045 * (sumX + sumY) * (sumX + sumY);
                    if (score > 0)
                    {
                        scores[y * width + x] = (float)score;
                        maximumScore = Math.Max(maximumScore, (float)score);
                    }
                }
            }

            if (maximumScore <= 0)
                return new FeatureSet([], width, height);
            var candidates = new List<Candidate>();
            var threshold = maximumScore * 0.003f;
            for (var y = 12; y < height - 12; y++)
            {
                for (var x = 12; x < width - 12; x++)
                {
                    var score = scores[y * width + x];
                    if (score < threshold)
                        continue;
                    var localMaximum = true;
                    for (var py = -3; py <= 3 && localMaximum; py++)
                    {
                        for (var px = -3; px <= 3; px++)
                        {
                            if ((px != 0 || py != 0) && scores[(y + py) * width + x + px] > score)
                            {
                                localMaximum = false;
                                break;
                            }
                        }
                    }
                    if (localMaximum)
                        candidates.Add(new Candidate(x, y, score));
                }
            }
            candidates.Sort((left, right) => right.Score.CompareTo(left.Score));

            var features = new List<Feature>(MaximumFeatures);
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (features.Count >= MaximumFeatures)
                    break;
                if (features.Any(feature =>
                        Math.Abs(feature.X - candidate.X) < 9 &&
                        Math.Abs(feature.Y - candidate.Y) < 9))
                    continue;
                features.Add(CreateFeature(gray, width, candidate.X, candidate.Y));
            }
            return new FeatureSet(features, width, height);
        }

        private static Feature CreateFeature(byte[] gray, int width, int x, int y)
        {
            var momentX = 0d;
            var momentY = 0d;
            for (var py = -8; py <= 8; py++)
            {
                for (var px = -8; px <= 8; px++)
                {
                    var value = gray[(y + py) * width + x + px];
                    momentX += px * value;
                    momentY += py * value;
                }
            }
            var angle = Math.Atan2(momentY, momentX);
            var cosine = Math.Cos(angle);
            var sine = Math.Sin(angle);
            Span<ulong> descriptor = stackalloc ulong[4];
            for (var bit = 0; bit < 256; bit++)
            {
                var offset = bit * 4;
                var x1 = DescriptorPattern[offset];
                var y1 = DescriptorPattern[offset + 1];
                var x2 = DescriptorPattern[offset + 2];
                var y2 = DescriptorPattern[offset + 3];
                var sample1X = x + (int)Math.Round(x1 * cosine - y1 * sine);
                var sample1Y = y + (int)Math.Round(x1 * sine + y1 * cosine);
                var sample2X = x + (int)Math.Round(x2 * cosine - y2 * sine);
                var sample2Y = y + (int)Math.Round(x2 * sine + y2 * cosine);
                if (gray[sample1Y * width + sample1X] < gray[sample2Y * width + sample2X])
                    descriptor[bit >> 6] |= 1UL << (bit & 63);
            }
            return new Feature(
                x, y, descriptor[0], descriptor[1], descriptor[2], descriptor[3]);
        }

        private static int[] CreateDescriptorPattern()
        {
            var pattern = new int[256 * 4];
            var state = 0x1f123bb5u;
            for (var index = 0; index < pattern.Length; index++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                pattern[index] = (int)(state % 17) - 8;
            }
            return pattern;
        }

        private readonly record struct Candidate(int X, int Y, float Score);
    }
}
