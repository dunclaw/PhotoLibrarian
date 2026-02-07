namespace PhotoLibrarian.ML.Services;

/// <summary>
/// Clusters face embeddings to group faces by identity.
/// Uses a simplified Chinese Whispers-like approach.
/// </summary>
public sealed class FaceClusteringService
{
    /// <summary>
    /// Minimum cosine similarity to consider two faces the same person.
    /// </summary>
    public float SimilarityThreshold { get; set; } = 0.55f;

    /// <summary>
    /// Clusters face embeddings into groups. Returns cluster assignments
    /// where each index corresponds to the input face, and the value is the cluster ID.
    /// </summary>
    public int[] ClusterFaces(List<FaceWithEmbedding> faces)
    {
        if (faces.Count == 0) return [];

        int n = faces.Count;
        var labels = new int[n];
        for (int i = 0; i < n; i++) labels[i] = i; // Each face starts as its own cluster

        // Build adjacency based on similarity threshold
        var adjacency = new List<(int a, int b, float sim)>();
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                var sim = FaceEmbeddingService.CosineSimilarity(faces[i].Embedding, faces[j].Embedding);
                if (sim >= SimilarityThreshold)
                    adjacency.Add((i, j, sim));
            }
        }

        // Iterative label propagation (Chinese Whispers)
        var rng = new Random(42);
        for (int iter = 0; iter < 20; iter++)
        {
            bool changed = false;
            var order = Enumerable.Range(0, n).OrderBy(_ => rng.Next()).ToList();

            foreach (var i in order)
            {
                // Count neighbor label votes weighted by similarity
                var votes = new Dictionary<int, float>();
                foreach (var (a, b, sim) in adjacency)
                {
                    int neighbor = -1;
                    if (a == i) neighbor = b;
                    else if (b == i) neighbor = a;
                    if (neighbor < 0) continue;

                    var label = labels[neighbor];
                    if (!votes.ContainsKey(label)) votes[label] = 0;
                    votes[label] += sim;
                }

                if (votes.Count > 0)
                {
                    var bestLabel = votes.MaxBy(kv => kv.Value).Key;
                    if (labels[i] != bestLabel)
                    {
                        labels[i] = bestLabel;
                        changed = true;
                    }
                }
            }

            if (!changed) break;
        }

        // Remap labels to 0-based sequential IDs
        var labelMap = new Dictionary<int, int>();
        int nextId = 0;
        for (int i = 0; i < n; i++)
        {
            if (!labelMap.ContainsKey(labels[i]))
                labelMap[labels[i]] = nextId++;
            labels[i] = labelMap[labels[i]];
        }

        return labels;
    }
}

public sealed class FaceWithEmbedding
{
    public long FaceRegionId { get; set; }
    public long ImageId { get; set; }
    public required float[] Embedding { get; set; }
}
