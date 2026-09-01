namespace PhotoLibrarian.ML.Services;

internal static class YuNetPostProcessor
{
    private static readonly int[] Strides = [8, 16, 32];

    public static void ValidateModelOutputs(IEnumerable<string> outputNames)
    {
        var names = outputNames.ToHashSet(StringComparer.Ordinal);
        var missing = Strides
            .SelectMany(stride => new[]
            {
                $"cls_{stride}",
                $"obj_{stride}",
                $"bbox_{stride}",
                $"kps_{stride}"
            })
            .Where(name => !names.Contains(name))
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"YuNet model is missing required outputs: {string.Join(", ", missing)}.");
        }
    }

    public static List<DetectedFace> Decode(
        IReadOnlyDictionary<string, float[]> outputs,
        int inputWidth,
        int inputHeight,
        float imageScale,
        int originalWidth,
        int originalHeight,
        float confidenceThreshold,
        float nmsThreshold,
        int maximumFaces)
    {
        ValidateModelOutputs(outputs.Keys);
        var candidates = new List<DetectedFace>();

        foreach (var stride in Strides)
        {
            var columns = inputWidth / stride;
            var rows = inputHeight / stride;
            var cellCount = checked(columns * rows);
            var classifications = RequireLength(outputs, $"cls_{stride}", cellCount);
            var objects = RequireLength(outputs, $"obj_{stride}", cellCount);
            var boxes = RequireLength(outputs, $"bbox_{stride}", cellCount * 4);
            var landmarks = RequireLength(outputs, $"kps_{stride}", cellCount * 10);

            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    var index = row * columns + column;
                    var score = MathF.Sqrt(
                        Math.Clamp(classifications[index], 0, 1) *
                        Math.Clamp(objects[index], 0, 1));
                    if (score < confidenceThreshold)
                    {
                        continue;
                    }

                    var boxIndex = index * 4;
                    var centerX = (column + boxes[boxIndex]) * stride;
                    var centerY = (row + boxes[boxIndex + 1]) * stride;
                    var width = MathF.Exp(boxes[boxIndex + 2]) * stride;
                    var height = MathF.Exp(boxes[boxIndex + 3]) * stride;
                    var x = centerX - width / 2;
                    var y = centerY - height / 2;

                    var normalized = NormalizeBox(
                        x / imageScale,
                        y / imageScale,
                        width / imageScale,
                        height / imageScale,
                        originalWidth,
                        originalHeight);
                    if (normalized.Width <= 0 || normalized.Height <= 0)
                    {
                        continue;
                    }

                    var points = new FaceLandmark[5];
                    var landmarkIndex = index * 10;
                    for (var point = 0; point < points.Length; point++)
                    {
                        var pointX = (landmarks[landmarkIndex + point * 2] + column) *
                            stride / imageScale / originalWidth;
                        var pointY = (landmarks[landmarkIndex + point * 2 + 1] + row) *
                            stride / imageScale / originalHeight;
                        points[point] = new FaceLandmark(pointX, pointY);
                    }

                    candidates.Add(new DetectedFace
                    {
                        X = normalized.X,
                        Y = normalized.Y,
                        Width = normalized.Width,
                        Height = normalized.Height,
                        Confidence = score,
                        Landmarks = points
                    });
                }
            }
        }

        return ApplyNonMaximumSuppression(candidates, nmsThreshold, maximumFaces);
    }

    private static float[] RequireLength(
        IReadOnlyDictionary<string, float[]> outputs,
        string name,
        int minimumLength)
    {
        if (!outputs.TryGetValue(name, out var values) || values.Length < minimumLength)
        {
            throw new InvalidDataException(
                $"YuNet output '{name}' has an unexpected size.");
        }

        return values;
    }

    private static (float X, float Y, float Width, float Height) NormalizeBox(
        float x,
        float y,
        float width,
        float height,
        int imageWidth,
        int imageHeight)
    {
        var left = Math.Clamp(x, 0, imageWidth);
        var top = Math.Clamp(y, 0, imageHeight);
        var right = Math.Clamp(x + width, 0, imageWidth);
        var bottom = Math.Clamp(y + height, 0, imageHeight);
        return (
            left / imageWidth,
            top / imageHeight,
            Math.Max(0, right - left) / imageWidth,
            Math.Max(0, bottom - top) / imageHeight);
    }

    private static List<DetectedFace> ApplyNonMaximumSuppression(
        IEnumerable<DetectedFace> candidates,
        float threshold,
        int maximumFaces)
    {
        var remaining = candidates
            .OrderByDescending(candidate => candidate.Confidence)
            .ToList();
        var kept = new List<DetectedFace>();

        while (remaining.Count > 0 && kept.Count < maximumFaces)
        {
            var best = remaining[0];
            remaining.RemoveAt(0);
            kept.Add(best);
            remaining.RemoveAll(candidate => IntersectionOverUnion(best, candidate) >= threshold);
        }

        return kept;
    }

    private static float IntersectionOverUnion(DetectedFace first, DetectedFace second)
    {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union = first.Width * first.Height + second.Width * second.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}
