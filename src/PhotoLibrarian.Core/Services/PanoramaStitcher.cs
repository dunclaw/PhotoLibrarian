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
        => Stitch([first, second], [firstPath, secondPath], progress, cancellationToken);

    internal static PanoramaStitchResult Stitch(
        IReadOnlyList<WindowsImagingCodec.DecodedImage> images,
        IReadOnlyList<string> paths,
        IProgress<PanoramaStitchProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (images.Count < 2 || images.Count > PanoramaStitchingService.MaximumSourceCount ||
            images.Count != paths.Count)
            throw new ArgumentException("Supply 2 to 64 photos and a source path for each.");
        long decodedPixels = 0;
        var features = new FeatureSet[images.Count];
        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index];
            decodedPixels += (long)image.Width * image.Height;
            if (image.Width < 32 || image.Height < 32 ||
                image.Pixels.LongLength != (long)image.Width * image.Height * 4 ||
                decodedPixels > PanoramaStitchingService.MaximumDecodedPixels)
                throw new InvalidDataException("The selected photos exceed the decoded memory limit or have invalid dimensions.");
            progress?.Report(new("Finding image features", 15 + index * 10 / images.Count));
            features[index] = FeatureExtractor.Detect(image, cancellationToken);
            if (features[index].Features.Count < MinimumInliers)
                throw new InvalidDataException($"'{Path.GetFileName(paths[index])}' has too little distinct detail to align safely.");
        }

        // Features are extracted once; every pair is considered, regardless of selection or filename order.
        var edges = new List<AlignmentEdge>();
        var pairCount = images.Count * (images.Count - 1) / 2;
        var pairIndex = 0;
        for (var first = 0; first < images.Count; first++)
        {
            for (var second = first + 1; second < images.Count; second++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(
                    $"Checking overlap {++pairIndex} of {pairCount}", 25 + pairIndex * 35 / pairCount));
                var edge = TryAlign(first, second, features, images, cancellationToken);
                if (edge is not null)
                    edges.Add(edge);
            }
        }
        var anchor = ChooseAnchor(edges, paths);
        var placements = PlaceImages(edges, images.Count, anchor);
        VerifyGlobalAlignment(edges, placements, features, cancellationToken);
        var layout = CreateLayout(placements, images, anchor);
        progress?.Report(new("Blending and cropping panorama", 67));
        var canvas = Blend(images, placements, layout, progress, cancellationToken);
        var crop = FindLargestOpaqueRectangle(canvas.Mask, layout.Width, layout.Height, cancellationToken);
        var canvasPixels = (long)layout.Width * layout.Height;
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
                checked(((crop.Top + y) * layout.Width + crop.Left) * 4),
                output,
                checked(y * crop.Width * 4),
                checked(crop.Width * 4));
        }

        EnsureEverySourceRetained(placements, layout, crop, paths);
        var matchCount = edges.Sum(edge => edge.Matches.Count);
        var inlierCount = edges.Sum(edge => edge.Inliers.Count);
        var sourcePlacements = new PanoramaSourcePlacement[images.Count];
        var cropTransform = new double[] { 1, 0, -crop.Left, 0, 1, -crop.Top, 0, 0, 1 };
        for (var index = 0; index < images.Count; index++)
        {
            sourcePlacements[index] = new(
                (int)images[index].Width, (int)images[index].Height,
                Array.AsReadOnly(Multiply(cropTransform, layout.Sources[index].Matrix)),
                Array.AsReadOnly(placements[index].Gains));
        }
        progress?.Report(new("Panorama preview ready", 100));
        return new PanoramaStitchResult(
            output,
            crop.Width,
            crop.Height,
            Array.AsReadOnly(paths.ToArray()),
            matchCount,
            inlierCount,
            (double)inlierCount / matchCount,
            Array.AsReadOnly(placements.Select(placement => placement.Gains.Average()).ToArray()),
            layout.Scale,
            (double)cropPixels / canvasPixels,
            Array.AsReadOnly(sourcePlacements));
    }

    private static AlignmentEdge? TryAlign(
        int firstIndex, int secondIndex,
        FeatureSet[] features,
        IReadOnlyList<WindowsImagingCodec.DecodedImage> images,
        CancellationToken cancellationToken)
    {
        var first = features[firstIndex];
        var second = features[secondIndex];
        var matches = MatchFeatures(first.Features, second.Features, cancellationToken);
        if (matches.Count < MinimumInliers)
            return null;
        HomographyResult homography;
        try
        {
            homography = FindHomography(matches, first, second, cancellationToken);
            if (homography.Inliers.Count < MinimumInliers ||
                (double)homography.Inliers.Count / matches.Count < 0.3 ||
                HasCompetingAlignment(matches, homography, first, second, cancellationToken))
                return null;
            EnsureInlierCoverage(homography.Inliers, matches, first, second);
            ProjectCorners(homography.Matrix);
        }
        catch (InvalidDataException)
        {
            // A rejected pair is not an output: all selected photos must still form one safe graph.
            return null;
        }
        if (Invert(homography.Matrix) is null)
            return null;
        var gains = EstimateExposureGains(
            matches, homography.Inliers, first, second, images[firstIndex], images[secondIndex]);
        return new(firstIndex, secondIndex, homography.Matrix, matches, homography.Inliers, gains);
    }

    private static int ChooseAnchor(List<AlignmentEdge> edges, IReadOnlyList<string> paths)
    {
        var best = 0;
        var bestDistance = int.MaxValue;
        for (var source = 0; source < paths.Count; source++)
        {
            var distances = Enumerable.Repeat(-1, paths.Count).ToArray();
            var queue = new Queue<int>();
            queue.Enqueue(source);
            distances[source] = 0;
            while (queue.TryDequeue(out var current))
            {
                foreach (var edge in edges)
                {
                    var other = edge.First == current ? edge.Second : edge.Second == current ? edge.First : -1;
                    if (other < 0 || distances[other] >= 0)
                        continue;
                    distances[other] = distances[current] + 1;
                    queue.Enqueue(other);
                }
            }
            if (distances.Any(distance => distance < 0))
            {
                var missing = Enumerable.Range(0, paths.Count)
                    .Where(index => distances[index] < 0)
                    .Select(index => Path.GetFileName(paths[index]));
                throw new InvalidDataException(
                    $"Not all selected photos have reliable overlap. Could not connect: {string.Join(", ", missing)}. " +
                    "No photos were omitted. Select a connected set with more shared detail and less parallax.");
            }
            var total = distances.Sum();
            if (total < bestDistance)
            {
                best = source;
                bestDistance = total;
            }
        }
        return best;
    }

    private static Placement[] PlaceImages(List<AlignmentEdge> edges, int count, int anchor)
    {
        var result = new Placement[count];
        var placed = new bool[count];
        result[anchor] = new([1, 0, 0, 0, 1, 0, 0, 0, 1], [1, 1, 1]);
        placed[anchor] = true;
        for (var remaining = count - 1; remaining > 0; remaining--)
        {
            var edge = edges.Where(edge => placed[edge.First] != placed[edge.Second])
                .MaxBy(edge => edge.Inliers.Count)
                ?? throw new InvalidDataException("The overlap graph is disconnected.");
            var parent = placed[edge.First] ? edge.First : edge.Second;
            var child = placed[edge.First] ? edge.Second : edge.First;
            var matrix = parent == edge.First ? edge.Matrix :
                Invert(edge.Matrix) ?? throw new InvalidDataException("An overlap transform is singular.");
            var gains = new double[3];
            for (var channel = 0; channel < 3; channel++)
            {
                var relativeGain = parent == edge.First ? edge.Gains[channel] : 1 / edge.Gains[channel];
                gains[channel] = result[parent].Gains[channel] * relativeGain;
                if (gains[channel] < 0.25 || gains[channel] > 4)
                    throw new InvalidDataException("The exposure difference across this set is too large to blend reliably.");
            }
            result[child] = new(Multiply(result[parent].Matrix, matrix), gains);
            placed[child] = true;
        }
        return result;
    }

    private static void VerifyGlobalAlignment(
        List<AlignmentEdge> edges, Placement[] placements, FeatureSet[] features,
        CancellationToken cancellationToken)
    {
        foreach (var edge in edges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var errors = new List<double>();
            foreach (var index in edge.Inliers)
            {
                var match = edge.Matches[index];
                var a = features[edge.First].Features[match.FirstIndex];
                var b = features[edge.Second].Features[match.SecondIndex];
                if (!TryProject(placements[edge.First].Matrix,
                        a.X / (features[edge.First].Width - 1d),
                        a.Y / (features[edge.First].Height - 1d), out var ax, out var ay) ||
                    !TryProject(placements[edge.Second].Matrix,
                        b.X / (features[edge.Second].Width - 1d),
                        b.Y / (features[edge.Second].Height - 1d), out var bx, out var by))
                    throw new InvalidDataException("The global alignment is unstable.");
                errors.Add(Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by)));
            }
            errors.Sort();
            if (errors[(int)(errors.Count * 0.8)] * FeatureMaximumDimension > 8)
                throw new InvalidDataException(
                    "Overlapping photos disagree in the full panorama (alignment drift or parallax). No partial panorama was produced.");
        }
    }

    private static double[] Multiply(double[] left, double[] right)
    {
        var result = new double[9];
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
                for (var item = 0; item < 3; item++)
                    result[row * 3 + column] += left[row * 3 + item] * right[item * 3 + column];
        return result;
    }

    private static List<Match> MatchFeatures(
        IReadOnlyList<Feature> first,
        IReadOnlyList<Feature> second,
        CancellationToken cancellationToken)
    {
        var firstToSecond = FindNearest(first, second, cancellationToken);
        var secondToFirst = FindNearest(second, first, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var nearest = new int[query.Count];
        Array.Fill(nearest, -1);
        for (var queryIndex = 0; queryIndex < query.Count; queryIndex++)
        {
            if ((queryIndex & 31) == 0)
                cancellationToken.ThrowIfCancellationRequested();
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
                (secondBest == int.MaxValue || best < secondBest * 0.78))
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
        if (remaining.Length < MinimumInliers)
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

    private static List<Point> ProjectCorners(double[] matrix)
    {
        if (matrix.Any(value => !double.IsFinite(value)))
            throw new InvalidDataException("The photo alignment contains invalid coordinates.");
        var corners = new[]
        {
            new Point(0, 0),
            new Point(1, 0),
            new Point(1, 1),
            new Point(0, 1)
        };
        var projected = new List<Point>(corners.Length);
        var sign = Math.Sign(matrix[8]);
        foreach (var corner in corners)
        {
            var denominator = matrix[6] * corner.X + matrix[7] * corner.Y + matrix[8];
            if (Math.Sign(denominator) != sign ||
                !TryProject(matrix, corner.X, corner.Y, out var x, out var y))
                throw new InvalidDataException("The photo alignment projects image edges to infinity.");
            projected.Add(new Point(x, y));
        }
        var area = 0d;
        for (var index = 0; index < 4; index++)
        {
            var a = projected[index];
            var b = projected[(index + 1) % 4];
            area += a.X * b.Y - a.Y * b.X;
        }
        if (!double.IsFinite(area) || area <= 0.05 || area > 40)
            throw new InvalidDataException("The photo alignment is reflected, collapsed, or excessively stretched.");
        return projected;
    }

    private static CanvasLayout CreateLayout(
        Placement[] placements, IReadOnlyList<WindowsImagingCodec.DecodedImage> images, int anchor)
    {
        var corners = placements.Select(placement => ProjectCorners(placement.Matrix)).ToArray();
        var minX = corners.Min(points => points.Min(point => point.X)) * (images[anchor].Width - 1);
        var minY = corners.Min(points => points.Min(point => point.Y)) * (images[anchor].Height - 1);
        var maxX = corners.Max(points => points.Max(point => point.X)) * (images[anchor].Width - 1);
        var maxY = corners.Max(points => points.Max(point => point.Y)) * (images[anchor].Height - 1);
        var rawWidth = maxX - minX + 1;
        var rawHeight = maxY - minY + 1;
        var scale = Math.Min(1, Math.Min(
            (PanoramaStitchingService.MaximumOutputDimension - 1) / Math.Max(rawWidth, rawHeight),
            Math.Sqrt((PanoramaStitchingService.MaximumOutputPixels - 100_000d) / (rawWidth * rawHeight))));
        if (!double.IsFinite(scale) || scale < 0.02)
            throw new InvalidDataException("This set is too stretched for a reliable projective panorama.");
        var width = checked((int)Math.Ceiling(rawWidth * scale));
        var height = checked((int)Math.Ceiling(rawHeight * scale));
        var canvasTransform = new double[]
        {
            (images[anchor].Width - 1) * scale, 0, -minX * scale,
            0, (images[anchor].Height - 1) * scale, -minY * scale,
            0, 0, 1
        };
        var sources = new CanvasSource[placements.Length];
        for (var index = 0; index < sources.Length; index++)
        {
            var matrix = Multiply(canvasTransform, placements[index].Matrix);
            // These corners are already in pixels; the projective shape was checked above.
            var points = new List<Point>();
            foreach (var point in new[] { new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(0, 1) })
            {
                if (!TryProject(matrix, point.X, point.Y, out var x, out var y))
                    throw new InvalidDataException("The full panorama alignment is unstable.");
                points.Add(new(x, y));
            }
            sources[index] = new(
                matrix,
                Invert(matrix) ?? throw new InvalidDataException("The full panorama transform is singular."),
                Math.Clamp((int)Math.Floor(points.Min(point => point.X)), 0, width - 1),
                Math.Clamp((int)Math.Floor(points.Min(point => point.Y)), 0, height - 1),
                Math.Clamp((int)Math.Ceiling(points.Max(point => point.X)), 0, width - 1),
                Math.Clamp((int)Math.Ceiling(points.Max(point => point.Y)), 0, height - 1));
        }
        return new(width, height, scale, sources);
    }

    private static void EnsureEverySourceRetained(
        Placement[] placements, CanvasLayout layout, CropRectangle crop, IReadOnlyList<string> paths)
    {
        for (var index = 0; index < placements.Length; index++)
        {
            var retained = 0;
            for (var y = 1; y <= 5; y++)
            {
                for (var x = 1; x <= 5; x++)
                {
                    if (TryProject(layout.Sources[index].Matrix, x / 6d, y / 6d, out var px, out var py) &&
                        px >= crop.Left && px < crop.Left + crop.Width &&
                        py >= crop.Top && py < crop.Top + crop.Height)
                        retained++;
                }
            }
            if (retained < 3)
                throw new InvalidDataException(
                    $"The clean crop would discard '{Path.GetFileName(paths[index])}'. No photo was silently omitted. " +
                    "Choose a more rectangular set with greater overlap.");
        }
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
        IReadOnlyList<WindowsImagingCodec.DecodedImage> images,
        Placement[] placements,
        CanvasLayout layout,
        IProgress<PanoramaStitchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var width = layout.Width;
        var height = layout.Height;
        var output = new byte[checked(width * height * 4)];
        var mask = new byte[checked(width * height)];
        const int stripHeight = 32;
        var accumulated = new float[checked(width * Math.Min(stripHeight, height) * 4)];
        var linearSamples = Enumerable.Range(0, 256).Select(value => SrgbToLinear(value / 255d)).ToArray();
        var srgbSamples = Enumerable.Range(0, 65536)
            .Select(value => (byte)Math.Clamp(Math.Round(LinearToSrgb(value / 65535d) * 255), 0, 255)).ToArray();

        // Each original is warped once into bounded strips, never into another already blended image.
        for (var top = 0; top < height; top += stripHeight)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("Blending all photos", 67 + top * 28 / height));
            Array.Clear(accumulated);
            var bottom = Math.Min(height, top + stripHeight);
            for (var index = 0; index < images.Count; index++)
            {
                var source = layout.Sources[index];
                var image = images[index];
                for (var y = Math.Max(top, source.Top); y < Math.Min(bottom, source.Bottom + 1); y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var x = source.Left; x <= source.Right; x++)
                    {
                        if (!TryProject(source.Inverse, x, y, out var nx, out var ny) ||
                            nx < 0 || ny < 0 || nx > 1 || ny > 1)
                            continue;
                        var sx = nx * (image.Width - 1);
                        var sy = ny * (image.Height - 1);
                        var weight = (float)Math.Clamp(
                            EdgeDistance(sx, sy, (int)image.Width, (int)image.Height) / 72, 0.02, 1);
                        var offset = ((y - top) * width + x) * 4;
                        for (var channel = 0; channel < 3; channel++)
                        {
                            var value = SampleFlattened(image, sx, sy, channel);
                            var low = (int)value;
                            var high = Math.Min(255, low + 1);
                            var linear = linearSamples[low] +
                                (linearSamples[high] - linearSamples[low]) * (value - low);
                            accumulated[offset + channel] +=
                                (float)Math.Min(1, linear * placements[index].Gains[channel]) * weight;
                        }
                        accumulated[offset + 3] += weight;
                    }
                }
            }
            for (var y = top; y < bottom; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var sourceOffset = ((y - top) * width + x) * 4;
                    var weight = accumulated[sourceOffset + 3];
                    if (weight <= 0)
                        continue;
                    var targetOffset = (y * width + x) * 4;
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var linear = accumulated[sourceOffset + channel] / weight;
                        output[targetOffset + channel] =
                            srgbSamples[Math.Clamp((int)Math.Round(linear * 65535), 0, 65535)];
                    }
                    output[targetOffset + 3] = 255;
                    mask[y * width + x] = 1;
                }
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
    private sealed record AlignmentEdge(
        int First, int Second, double[] Matrix, List<Match> Matches, List<int> Inliers, double[] Gains);
    private sealed record Placement(double[] Matrix, double[] Gains);
    private sealed record CanvasSource(double[] Matrix, double[] Inverse, int Left, int Top, int Right, int Bottom);
    private sealed record CanvasLayout(int Width, int Height, double Scale, CanvasSource[] Sources);

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
            descriptor.Clear();
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
