using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.ML.Services;

public sealed class FaceRecognitionService
{
    public float SimilarityThreshold { get; set; } = 0.45f;

    public PersonMatch? FindBestMatch(
        float[] embedding,
        IEnumerable<FaceRegion> confirmedFaces)
    {
        PersonMatch? best = null;
        foreach (var group in confirmedFaces
            .Where(face => face.PersonId.HasValue && face.Embedding is not null)
            .GroupBy(face => new { Id = face.PersonId!.Value, face.PersonName }))
        {
            var similarity = group.Max(
                face => FaceEmbeddingService.CosineSimilarity(embedding, face.Embedding));
            if (similarity >= SimilarityThreshold &&
                (best is null || similarity > best.Similarity))
            {
                best = new PersonMatch(group.Key.Id, group.Key.PersonName, similarity);
            }
        }

        return best;
    }
}

public sealed record PersonMatch(long PersonId, string? PersonName, float Similarity);
