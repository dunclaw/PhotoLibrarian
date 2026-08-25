namespace PhotoLibrarian.Core.Services;

public readonly record struct StraightenAnalysisResult(
    bool HasResult,
    double CorrectionDegrees,
    double Confidence,
    int EdgeCount)
{
    public static StraightenAnalysisResult NoResult(int edgeCount = 0) =>
        new(false, 0, 0, edgeCount);
}

/// <summary>
/// Detects a dominant axis using thinned Sobel edges, gradient-constrained Hough voting, and
/// consensus across parallel horizontal and vertical lines. Ambiguous scenes are rejected rather
/// than returning an arbitrary correction.
/// </summary>
public static class StraightenAnalyzer
{
    private const int ThetaBinCount = 360;
    private const double ThetaStepDegrees = 180.0 / ThetaBinCount;
    private const int MaximumVotedEdges = 60_000;
    private const int GradientVoteRadiusBins = 20;
    private const int MaximumPeaksPerTheta = 8;
    private const double MaximumAxisDeviation = 30;
    private const int CorrectionBinCount = 121;

    public static StraightenAnalysisResult AnalyzeBgra(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride = 0)
    {
        if (width < 3)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 3)
            throw new ArgumentOutOfRangeException(nameof(height));

        stride = stride == 0 ? checked(width * 4) : stride;
        if (stride < width * 4)
            throw new ArgumentOutOfRangeException(nameof(stride));
        if (pixels.Length < checked(stride * height))
            throw new ArgumentException("Pixel buffer is smaller than the supplied dimensions.", nameof(pixels));

        var grayscale = new byte[checked(width * height)];
        for (var y = 0; y < height; y++)
        {
            var sourceRow = y * stride;
            var destinationRow = y * width;
            for (var x = 0; x < width; x++)
            {
                var pixel = sourceRow + (x * 4);
                var blue = pixels[pixel];
                var green = pixels[pixel + 1];
                var red = pixels[pixel + 2];
                grayscale[destinationRow + x] =
                    (byte)((red * 77 + green * 150 + blue * 29) >> 8);
            }
        }

        return AnalyzeGrayscale(grayscale, width, height);
    }

    public static StraightenAnalysisResult AnalyzeGrayscale(
        ReadOnlySpan<byte> grayscale,
        int width,
        int height)
    {
        if (width < 3)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 3)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (grayscale.Length < checked(width * height))
            throw new ArgumentException("Pixel buffer is smaller than the supplied dimensions.", nameof(grayscale));

        var fineResult = AnalyzeScale(grayscale, width, height, coarse: false);
        var coarseResult = AnalyzeScale(grayscale, width, height, coarse: true);

        if (fineResult.HasResult && coarseResult.HasResult)
        {
            if (Math.Abs(fineResult.CorrectionDegrees - coarseResult.CorrectionDegrees) <= 3)
            {
                var totalConfidence = fineResult.Confidence + coarseResult.Confidence;
                return new StraightenAnalysisResult(
                    true,
                    ((fineResult.CorrectionDegrees * fineResult.Confidence)
                        + (coarseResult.CorrectionDegrees * coarseResult.Confidence))
                        / totalConfidence,
                    Math.Clamp(Math.Max(fineResult.Confidence, coarseResult.Confidence) + 0.1, 0, 1),
                    Math.Max(fineResult.EdgeCount, coarseResult.EdgeCount));
            }

            return fineResult.Confidence >= coarseResult.Confidence + 0.12
                ? fineResult
                : StraightenAnalysisResult.NoResult(
                    Math.Max(fineResult.EdgeCount, coarseResult.EdgeCount));
        }

        if (fineResult.HasResult)
            return fineResult;

        // A coarse-only result is useful for shorelines and horizons, but very large inferred
        // rolls are usually texture or a diagonal subject surviving the blur.
        return coarseResult.HasResult && Math.Abs(coarseResult.CorrectionDegrees) <= 15
            ? coarseResult
            : StraightenAnalysisResult.NoResult(coarseResult.EdgeCount);
    }

    private static StraightenAnalysisResult AnalyzeScale(
        ReadOnlySpan<byte> grayscale,
        int width,
        int height,
        bool coarse)
    {
        var analysisGrayscale = coarse
            ? GaussianBlur5(GaussianBlur5(grayscale, width, height), width, height)
            : grayscale.ToArray();
        var pixelCount = checked(width * height);
        var magnitudes = new ushort[pixelCount];
        var gradientXs = new short[pixelCount];
        var gradientYs = new short[pixelCount];
        long magnitudeTotal = 0;
        var maximumMagnitude = 0;

        for (var y = 1; y < height - 1; y++)
        {
            var row = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var index = row + x;
                var topLeft = analysisGrayscale[index - width - 1];
                var top = analysisGrayscale[index - width];
                var topRight = analysisGrayscale[index - width + 1];
                var left = analysisGrayscale[index - 1];
                var right = analysisGrayscale[index + 1];
                var bottomLeft = analysisGrayscale[index + width - 1];
                var bottom = analysisGrayscale[index + width];
                var bottomRight = analysisGrayscale[index + width + 1];

                var gradientX = -topLeft - (2 * left) - bottomLeft
                    + topRight + (2 * right) + bottomRight;
                var gradientY = -topLeft - (2 * top) - topRight
                    + bottomLeft + (2 * bottom) + bottomRight;
                var magnitude = Math.Abs(gradientX) + Math.Abs(gradientY);

                magnitudes[index] = (ushort)magnitude;
                gradientXs[index] = (short)gradientX;
                gradientYs[index] = (short)gradientY;
                magnitudeTotal += magnitude;
                maximumMagnitude = Math.Max(maximumMagnitude, magnitude);
            }
        }

        if (maximumMagnitude == 0)
            return StraightenAnalysisResult.NoResult();

        var interiorPixelCount = (width - 2) * (height - 2);
        var meanMagnitude = magnitudeTotal / (double)interiorPixelCount;
        var threshold = coarse
            ? Math.Max(24, Math.Max(meanMagnitude * 1.5, maximumMagnitude * 0.08))
            : Math.Max(80, Math.Max(meanMagnitude * 2.5, maximumMagnitude * 0.18));

        var edges = new List<EdgePoint>();
        for (var y = 1; y < height - 1; y++)
        {
            var row = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var index = row + x;
                var magnitude = magnitudes[index];
                if (magnitude < threshold
                    || !IsLocalGradientMaximum(
                        magnitudes,
                        width,
                        index,
                        gradientXs[index],
                        gradientYs[index],
                        magnitude))
                {
                    continue;
                }

                var normalDegrees = Math.Atan2(gradientYs[index], gradientXs[index]) * 180 / Math.PI;
                edges.Add(new EdgePoint(x, y, GetThetaIndex(normalDegrees), magnitude));
            }
        }

        if (edges.Count == 0)
            return StraightenAnalysisResult.NoResult();

        var edgeStep = Math.Max(1, (int)Math.Ceiling(edges.Count / (double)MaximumVotedEdges));
        var diagonal = (int)Math.Ceiling(Math.Sqrt((width * (double)width) + (height * (double)height)));
        var rhoBinCount = (2 * diagonal) + 1;
        var accumulator = new int[checked(ThetaBinCount * rhoBinCount)];
        var cosines = new double[ThetaBinCount];
        var sines = new double[ThetaBinCount];

        for (var thetaIndex = 0; thetaIndex < ThetaBinCount; thetaIndex++)
        {
            var theta = ((thetaIndex * ThetaStepDegrees) - 90) * Math.PI / 180;
            cosines[thetaIndex] = Math.Cos(theta);
            sines[thetaIndex] = Math.Sin(theta);
        }

        for (var edgeIndex = 0; edgeIndex < edges.Count; edgeIndex += edgeStep)
        {
            var edge = edges[edgeIndex];
            for (var delta = -GradientVoteRadiusBins; delta <= GradientVoteRadiusBins; delta++)
            {
                var thetaIndex = WrapThetaIndex(edge.NormalThetaIndex + delta);
                var rho = (int)Math.Round(
                    edge.X * cosines[thetaIndex] + edge.Y * sines[thetaIndex]) + diagonal;
                var magnitudeWeight = Math.Clamp(
                    (int)Math.Round(edge.Magnitude / threshold),
                    1,
                    8);
                accumulator[(thetaIndex * rhoBinCount) + rho] += magnitudeWeight;
            }
        }

        var minimumPeakVotes = Math.Max(12, (int)Math.Ceiling(Math.Max(width, height) * 0.08));
        var scoresByTheta = new double[ThetaBinCount];
        var bestVotesByTheta = new int[ThetaBinCount];
        var peakCountsByTheta = new int[ThetaBinCount];
        for (var thetaIndex = 0; thetaIndex < ThetaBinCount; thetaIndex++)
        {
            var thetaOffset = thetaIndex * rhoBinCount;
            var peaks = new List<int>();
            for (var rhoIndex = 2; rhoIndex < rhoBinCount - 2; rhoIndex++)
            {
                var votes = accumulator[thetaOffset + rhoIndex];
                if (votes < minimumPeakVotes
                    || votes < accumulator[thetaOffset + rhoIndex - 1]
                    || votes < accumulator[thetaOffset + rhoIndex + 1]
                    || votes < accumulator[thetaOffset + rhoIndex - 2]
                    || votes < accumulator[thetaOffset + rhoIndex + 2])
                {
                    continue;
                }

                peaks.Add(votes);
            }

            if (peaks.Count == 0)
                continue;

            peaks.Sort(static (left, right) => right.CompareTo(left));
            var retainedPeakCount = Math.Min(MaximumPeaksPerTheta, peaks.Count);
            bestVotesByTheta[thetaIndex] = peaks[0];
            peakCountsByTheta[thetaIndex] = retainedPeakCount;
            for (var peakIndex = 0; peakIndex < retainedPeakCount; peakIndex++)
                scoresByTheta[thetaIndex] += peaks[peakIndex] * (double)peaks[peakIndex];
        }

        var correctionScores = new double[CorrectionBinCount];
        for (var thetaIndex = 0; thetaIndex < ThetaBinCount; thetaIndex++)
        {
            if (scoresByTheta[thetaIndex] == 0)
                continue;

            var lineAngle = NormalizeHalfTurn(GetThetaDegrees(thetaIndex) + 90);
            var deviation = GetAxisDeviation(lineAngle);
            if (Math.Abs(deviation) > MaximumAxisDeviation)
                continue;

            var correctionIndex = CorrectionToIndex(-deviation);
            correctionScores[correctionIndex] += scoresByTheta[thetaIndex];
        }

        var smoothedScores = SmoothCorrectionScores(correctionScores);
        var bestCorrectionIndex = GetBestIndex(smoothedScores);
        var bestScore = smoothedScores[bestCorrectionIndex];
        if (bestScore <= 0)
            return StraightenAnalysisResult.NoResult(edges.Count);

        const int competingPeakExclusionBins = 8;
        var secondBestScore = 0.0;
        for (var index = 0; index < smoothedScores.Length; index++)
        {
            if (Math.Abs(index - bestCorrectionIndex) > competingPeakExclusionBins)
                secondBestScore = Math.Max(secondBestScore, smoothedScores[index]);
        }

        var competingRatio = secondBestScore / bestScore;
        if (competingRatio > 0.82)
            return StraightenAnalysisResult.NoResult(edges.Count);

        var correction = GetWeightedCorrection(correctionScores, bestCorrectionIndex);
        var bestLineVotes = 0;
        var supportingPeakCount = 0;
        for (var thetaIndex = 0; thetaIndex < ThetaBinCount; thetaIndex++)
        {
            var lineAngle = NormalizeHalfTurn(GetThetaDegrees(thetaIndex) + 90);
            var candidateCorrection = -GetAxisDeviation(lineAngle);
            if (Math.Abs(candidateCorrection - correction) <= 1.5)
            {
                bestLineVotes = Math.Max(bestLineVotes, bestVotesByTheta[thetaIndex]);
                supportingPeakCount = Math.Max(supportingPeakCount, peakCountsByTheta[thetaIndex]);
            }
        }

        var strength = Math.Clamp(bestLineVotes / (Math.Max(width, height) * 2.4), 0, 1);
        var dominance = Math.Clamp(1 - competingRatio, 0, 1);
        var structuralSupport = Math.Clamp(supportingPeakCount / 4.0, 0, 1);
        var confidence = Math.Clamp(
            (strength * 0.50) + (dominance * 0.35) + (structuralSupport * 0.15),
            0,
            1);
        if (confidence < 0.3)
            return StraightenAnalysisResult.NoResult(edges.Count);

        return new StraightenAnalysisResult(
            true,
            Math.Clamp(correction, -45, 45),
            confidence,
            edges.Count);
    }

    private static bool IsLocalGradientMaximum(
        ushort[] magnitudes,
        int width,
        int index,
        int gradientX,
        int gradientY,
        int magnitude)
    {
        var absoluteX = Math.Abs(gradientX);
        var absoluteY = Math.Abs(gradientY);
        int previousIndex;
        int nextIndex;

        if (absoluteX > absoluteY * 2)
        {
            previousIndex = index - 1;
            nextIndex = index + 1;
        }
        else if (absoluteY > absoluteX * 2)
        {
            previousIndex = index - width;
            nextIndex = index + width;
        }
        else if ((gradientX >= 0) == (gradientY >= 0))
        {
            previousIndex = index - width - 1;
            nextIndex = index + width + 1;
        }
        else
        {
            previousIndex = index - width + 1;
            nextIndex = index + width - 1;
        }

        return magnitude >= magnitudes[previousIndex] && magnitude >= magnitudes[nextIndex];
    }

    private static byte[] GaussianBlur5(ReadOnlySpan<byte> pixels, int width, int height)
    {
        ReadOnlySpan<int> kernel = [1, 4, 6, 4, 1];
        var horizontal = new int[checked(width * height)];
        var blurred = new byte[horizontal.Length];

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                for (var offset = -2; offset <= 2; offset++)
                {
                    var sourceX = Math.Clamp(x + offset, 0, width - 1);
                    sum += pixels[row + sourceX] * kernel[offset + 2];
                }
                horizontal[row + x] = sum;
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                for (var offset = -2; offset <= 2; offset++)
                {
                    var sourceY = Math.Clamp(y + offset, 0, height - 1);
                    sum += horizontal[(sourceY * width) + x] * kernel[offset + 2];
                }
                blurred[(y * width) + x] = (byte)((sum + 128) / 256);
            }
        }

        return blurred;
    }

    private static double[] SmoothCorrectionScores(double[] scores)
    {
        var smoothed = new double[scores.Length];
        for (var index = 0; index < scores.Length; index++)
        {
            for (var offset = -2; offset <= 2; offset++)
            {
                var sourceIndex = index + offset;
                if (sourceIndex >= 0 && sourceIndex < scores.Length)
                    smoothed[index] += scores[sourceIndex] * (3 - Math.Abs(offset));
            }
        }

        return smoothed;
    }

    private static int GetBestIndex(double[] values)
    {
        var bestIndex = 0;
        for (var index = 1; index < values.Length; index++)
        {
            if (values[index] > values[bestIndex])
                bestIndex = index;
        }
        return bestIndex;
    }

    private static double GetWeightedCorrection(double[] scores, int centerIndex)
    {
        var weightedTotal = 0.0;
        var totalWeight = 0.0;
        for (var index = Math.Max(0, centerIndex - 3);
             index <= Math.Min(scores.Length - 1, centerIndex + 3);
             index++)
        {
            weightedTotal += IndexToCorrection(index) * scores[index];
            totalWeight += scores[index];
        }

        return totalWeight == 0 ? IndexToCorrection(centerIndex) : weightedTotal / totalWeight;
    }

    private static int GetThetaIndex(double normalDegrees)
    {
        var normalized = NormalizeHalfTurn(normalDegrees);
        return WrapThetaIndex((int)Math.Round((normalized + 90) / ThetaStepDegrees));
    }

    private static int WrapThetaIndex(int index)
    {
        index %= ThetaBinCount;
        return index < 0 ? index + ThetaBinCount : index;
    }

    private static double GetThetaDegrees(int thetaIndex) =>
        (thetaIndex * ThetaStepDegrees) - 90;

    private static int CorrectionToIndex(double correction) =>
        Math.Clamp(
            (int)Math.Round((correction + MaximumAxisDeviation) / ThetaStepDegrees),
            0,
            CorrectionBinCount - 1);

    private static double IndexToCorrection(int index) =>
        (index * ThetaStepDegrees) - MaximumAxisDeviation;

    private static double NormalizeHalfTurn(double angle)
    {
        while (angle >= 90)
            angle -= 180;
        while (angle < -90)
            angle += 180;
        return angle;
    }

    private static double GetAxisDeviation(double lineAngle) => lineAngle switch
    {
        > 45 => lineAngle - 90,
        < -45 => lineAngle + 90,
        _ => lineAngle
    };

    private readonly record struct EdgePoint(
        int X,
        int Y,
        int NormalThetaIndex,
        int Magnitude);
}
