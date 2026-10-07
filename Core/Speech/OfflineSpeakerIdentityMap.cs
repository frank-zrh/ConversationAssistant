namespace ConversationAssistant.Core.Speech;

public sealed class OfflineSpeakerIdentityMap : IDisposable
{
    // Use the same conservative cosine-distance cutoff for local clustering and session matching.
    public const float ClusteringDistanceThreshold = 0.35f;
    private const int MaximumSpeakers = 64;
    private const float MinimumSimilarity = 1 - ClusteringDistanceThreshold;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly List<Speaker> _speakers = [];
    private int _nextId;
    private bool _disposed;

    public IReadOnlyDictionary<int, string?> Identify(IReadOnlyDictionary<int, float[]?> embeddings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(embeddings);
        var normalized = new Dictionary<int, float[]?>();
        try
        {
            foreach (var (local, embedding) in embeddings)
                normalized.Add(local, embedding is null ? null : Normalize(embedding));
            var candidates = new List<(int Local, Speaker Speaker, float Similarity)>();
            foreach (var (local, embedding) in normalized)
            {
                if (embedding is null) continue;
                foreach (var speaker in _speakers)
                {
                    var similarity = Cosine(embedding, speaker.Embedding);
                    if (similarity >= MinimumSimilarity)
                        candidates.Add((local, speaker, similarity));
                }
            }

            var assigned = new Dictionary<int, string?>();
            var used = new HashSet<Speaker>();
            // Different acoustic clusters in one window must never collapse into one speaker.
            foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Similarity))
            {
                if (assigned.ContainsKey(candidate.Local) || !used.Add(candidate.Speaker)) continue;
                assigned.Add(candidate.Local, candidate.Speaker.Id);
                candidate.Speaker.Update(normalized[candidate.Local]!);
            }

            foreach (var (local, embedding) in normalized.OrderBy(pair => pair.Key))
            {
                if (assigned.ContainsKey(local)) continue;
                if (embedding is null)
                {
                    assigned.Add(local, null);
                    continue;
                }
                if (_speakers.Count == MaximumSpeakers)
                    throw new InvalidOperationException(
                        "Offline speaker separation reached its session limit. Pause and resume to start a new speaker session.");
                var id = $"offline:{_sessionId}:{++_nextId}";
                _speakers.Add(new Speaker(id, (float[])embedding.Clone()));
                assigned.Add(local, id);
            }
            return assigned;
        }
        finally
        {
            foreach (var embedding in normalized.Values)
                if (embedding is not null) Array.Clear(embedding);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var speaker in _speakers) Array.Clear(speaker.Embedding);
        _speakers.Clear();
        _disposed = true;
    }

    private static float[] Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value)) throw InvalidEmbedding();
            sum += (double)value * value;
        }
        if (sum <= 1e-12) throw InvalidEmbedding();
        var scale = (float)(1 / Math.Sqrt(sum));
        return vector.Select(value => value * scale).ToArray();
    }

    private static float Cosine(float[] left, float[] right)
    {
        if (left.Length != right.Length) throw InvalidEmbedding();
        float sum = 0;
        for (var i = 0; i < left.Length; i++) sum += left[i] * right[i];
        return sum;
    }

    private static InvalidDataException InvalidEmbedding() => new(
        "Offline speaker separation could not extract a valid voice embedding. Check the audio source and reinstall the speaker models if this repeats.");

    private sealed class Speaker(string id, float[] embedding)
    {
        public string Id { get; } = id;
        public float[] Embedding { get; } = embedding;

        public void Update(float[] observation)
        {
            for (var i = 0; i < Embedding.Length; i++)
                Embedding[i] = .75f * Embedding[i] + .25f * observation[i];
            var normalized = Normalize(Embedding);
            normalized.CopyTo(Embedding, 0);
            Array.Clear(normalized);
        }
    }
}
