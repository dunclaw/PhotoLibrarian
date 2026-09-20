using PhotoLibrarian.Core.Data;
using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.ML.Services;

/// <summary>
/// The face half of a single per-photo recognition work item. Shared by the
/// unified pipeline and the standalone face scan so both produce identical
/// face results.
/// </summary>
internal static class FaceScanWorkItem
{
    public static async Task<int> ScanAsync(
        ImageEntry image,
        DecodedImage decoded,
        IFaceScanStore store,
        IFaceDetector detector,
        IFaceEmbedder embedder,
        CancellationToken cancellationToken)
    {
        var metadataFaces = (await store.GetFacesForImageAsync(image.Id))
            .Where(face => face.IsMetadataManaged)
            .ToList();
        var detections = await detector.DetectFacesAsync(
            decoded,
            cancellationToken);
        var newDetections = detections
            .Where(detection => metadataFaces.All(
                face => IntersectionOverUnion(face, detection) < 0.5))
            .ToList();
        var embeddingInputs = metadataFaces
            .Select(ToDetectedFace)
            .Concat(newDetections)
            .ToList();
        var embeddings = await embedder.GenerateEmbeddingsAsync(
            decoded,
            embeddingInputs,
            cancellationToken);
        if (embeddingInputs.Count != embeddings.Count)
        {
            throw new InvalidDataException(
                $"Expected {embeddingInputs.Count} embeddings but received {embeddings.Count}.");
        }

        var regions = metadataFaces
            .Select((face, index) => new FaceRegion
            {
                ImageId = image.Id,
                X = face.X,
                Y = face.Y,
                Width = face.Width,
                Height = face.Height,
                Confidence = face.Confidence,
                Embedding = embeddings[index],
                PersonId = face.PersonId,
                PersonName = face.PersonName,
                IsMetadataManaged = true
            })
            .Concat(newDetections.Select((face, index) =>
                new FaceRegion
                {
                    ImageId = image.Id,
                    X = face.X,
                    Y = face.Y,
                    Width = face.Width,
                    Height = face.Height,
                    Confidence = face.Confidence,
                    Embedding = embeddings[metadataFaces.Count + index]
                }))
            .ToArray();
        var wasSaved = await store.TryReplaceFaceRegionsAsync(
            image.Id,
            image.FileSize,
            image.DateModified,
            regions,
            FaceModelCatalog.PipelineVersion,
            cancellationToken);
        if (!wasSaved)
        {
            throw new InvalidOperationException(
                "The photo changed during face detection and will be retried.");
        }

        return regions.Length;
    }

    private static DetectedFace ToDetectedFace(FaceRegion face)
    {
        var left = (float)face.X;
        var top = (float)face.Y;
        var width = (float)face.Width;
        var height = (float)face.Height;
        return new DetectedFace
        {
            X = left,
            Y = top,
            Width = width,
            Height = height,
            Confidence = face.Confidence,
            Landmarks =
            [
                new FaceLandmark(left + width * 0.30f, top + height * 0.38f),
                new FaceLandmark(left + width * 0.70f, top + height * 0.38f),
                new FaceLandmark(left + width * 0.50f, top + height * 0.58f),
                new FaceLandmark(left + width * 0.35f, top + height * 0.78f),
                new FaceLandmark(left + width * 0.65f, top + height * 0.78f)
            ]
        };
    }

    private static double IntersectionOverUnion(
        FaceRegion face,
        DetectedFace detection)
    {
        var left = Math.Max(face.X, detection.X);
        var top = Math.Max(face.Y, detection.Y);
        var right = Math.Min(face.X + face.Width, detection.X + detection.Width);
        var bottom = Math.Min(face.Y + face.Height, detection.Y + detection.Height);
        var intersection =
            Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union =
            face.Width * face.Height +
            detection.Width * detection.Height -
            intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}

/// <summary>
/// The tagging half of a single per-photo recognition work item.
/// </summary>
internal static class AutoTagWorkItem
{
    public static async Task<int> TagAsync(
        ImageEntry image,
        DecodedImage decoded,
        IAutoTagStore store,
        IAutoTagger tagger,
        AutoTaggingSettings settings,
        AutoTagModelDefinition definition,
        CancellationToken cancellationToken)
    {
        var predictions = await tagger.PredictTagsAsync(
            decoded,
            settings.ProfileId,
            settings.ModelDirectory,
            settings.MaximumTags,
            settings.ConfidenceThreshold,
            cancellationToken);
        var generatedTags = predictions
            .Select(prediction => new GeneratedImageTag(
                prediction.Tag,
                prediction.Confidence))
            .ToArray();
        if (!await store.TryReplaceAutoTagsAsync(
            image,
            generatedTags,
            definition.PipelineVersion,
            cancellationToken))
        {
            throw new InvalidOperationException(
                "The photo changed during automatic tagging and will be retried.");
        }

        return generatedTags.Length;
    }
}
