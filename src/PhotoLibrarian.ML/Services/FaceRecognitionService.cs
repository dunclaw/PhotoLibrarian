using PhotoLibrarian.Core.Models;

namespace PhotoLibrarian.ML.Services;

public sealed class FaceRecognitionService
{
    private const int MaxPrototypesPerPerson = 8;
    private const float PrototypeMergeThreshold = 0.65f;

    public float SimilarityThreshold { get; set; } = 0.45f;

    public IReadOnlyList<PersonFaceProfile> BuildProfiles(
        IEnumerable<FaceRegion> confirmedFaces)
    {
        return confirmedFaces
            .Where(face => face.PersonId.HasValue && face.Embedding is not null)
            .GroupBy(face => face.PersonId!.Value)
            .Select(group => BuildProfile(group.Key, group))
            .ToList();
    }

    public PersonMatch? FindBestMatch(
        float[] embedding,
        IEnumerable<FaceRegion> confirmedFaces)
    {
        return FindBestMatch(embedding, BuildProfiles(confirmedFaces));
    }

    public PersonMatch? FindBestMatch(
        float[] embedding,
        IEnumerable<PersonFaceProfile> profiles,
        IReadOnlySet<long>? excludedPersonIds = null)
    {
        return FindMatches(embedding, profiles, excludedPersonIds).FirstOrDefault();
    }

    public IReadOnlyList<PersonMatch> FindMatches(
        float[] embedding,
        IEnumerable<PersonFaceProfile> profiles,
        IReadOnlySet<long>? excludedPersonIds = null)
    {
        return profiles
            .Where(profile => excludedPersonIds?.Contains(profile.PersonId) != true)
            .Select(profile => new PersonMatch(
                profile.PersonId,
                profile.PersonName,
                profile.Prototypes.Max(
                    prototype => FaceEmbeddingService.CosineSimilarity(
                        embedding,
                        prototype))))
            .Where(match => match.Similarity >= SimilarityThreshold)
            .OrderByDescending(match => match.Similarity)
            .ThenBy(match => match.PersonId)
            .ToList();
    }

    private static PersonFaceProfile BuildProfile(
        long personId,
        IEnumerable<FaceRegion> faces)
    {
        var faceList = faces.ToList();
        var builders = new List<PrototypeBuilder>();
        foreach (var face in faceList)
        {
            var embedding = face.Embedding!;
            var nearest = builders
                .Select(builder => new
                {
                    Builder = builder,
                    Similarity = FaceEmbeddingService.CosineSimilarity(
                        embedding,
                        builder.Centroid)
                })
                .MaxBy(candidate => candidate.Similarity);

            if (nearest is null ||
                (nearest.Similarity < PrototypeMergeThreshold &&
                 builders.Count < MaxPrototypesPerPerson))
            {
                builders.Add(new PrototypeBuilder(embedding));
            }
            else
            {
                nearest.Builder.Add(embedding);
            }
        }

        return new PersonFaceProfile(
            personId,
            faceList.Select(face => face.PersonName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
            builders.Select(builder => builder.Centroid).ToList(),
            faceList.Count);
    }

    private sealed class PrototypeBuilder
    {
        private readonly float[] _sum;
        private float[]? _centroid;

        public PrototypeBuilder(float[] embedding)
        {
            _sum = embedding.ToArray();
        }

        public float[] Centroid => _centroid ??= Normalize(_sum);

        public void Add(float[] embedding)
        {
            if (embedding.Length != _sum.Length)
            {
                return;
            }

            for (var index = 0; index < _sum.Length; index++)
            {
                _sum[index] += embedding[index];
            }
            _centroid = null;
        }

        private static float[] Normalize(float[] values)
        {
            var normalized = values.ToArray();
            float squaredNorm = 0;
            foreach (var value in normalized)
            {
                squaredNorm += value * value;
            }

            var norm = MathF.Sqrt(squaredNorm);
            if (norm <= 0)
            {
                return normalized;
            }

            for (var index = 0; index < normalized.Length; index++)
            {
                normalized[index] /= norm;
            }
            return normalized;
        }
    }
}

public sealed record PersonFaceProfile(
    long PersonId,
    string? PersonName,
    IReadOnlyList<float[]> Prototypes,
    int FaceCount);

public sealed record PersonMatch(long PersonId, string? PersonName, float Similarity);
