using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Transcript;

public interface ITranscriptEngine
{
    event Action<TranscriptSegment?>? Updated;
    event Action<TranscriptSegment>? Finalized;
    IReadOnlyList<TranscriptSegment> Segments { get; }
    void ReceivePartial(SpeechText speech);
    TranscriptSegment CommitFinal(SpeechText speech);
    void Clear();
}

public sealed class TranscriptEngine : ITranscriptEngine
{
    private readonly object _gate = new();
    private readonly List<TranscriptSegment> _segments = [];
    private readonly Dictionary<string, int> _speakers = new(StringComparer.Ordinal);
    private TranscriptSegment? _partial;

    public event Action<TranscriptSegment?>? Updated;
    public event Action<TranscriptSegment>? Finalized;

    public IReadOnlyList<TranscriptSegment> Segments
    {
        get { lock (_gate) return _segments.ToArray(); }
    }

    public void ReceivePartial(SpeechText speech)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speech.Text);
        TranscriptSegment segment;
        lock (_gate)
        {
            segment = CreateSegment(speech, false);
            _partial = segment;
        }
        Updated?.Invoke(segment);
    }

    public TranscriptSegment CommitFinal(SpeechText speech)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speech.Text);
        TranscriptSegment segment;
        lock (_gate)
        {
            segment = CreateSegment(speech, true);
            _partial = null;
            _segments.Add(segment);
        }
        Updated?.Invoke(null);
        Finalized?.Invoke(segment);
        return segment;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _segments.Clear();
            _speakers.Clear();
            _partial = null;
        }
        Updated?.Invoke(null);
    }

    private TranscriptSegment CreateSegment(SpeechText speech, bool final)
    {
        int? number = null;
        if (!string.IsNullOrWhiteSpace(speech.SpeakerId) &&
            !speech.SpeakerId.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            if (!_speakers.TryGetValue(speech.SpeakerId, out var known))
                _speakers.Add(speech.SpeakerId, known = _speakers.Count + 1);
            number = known;
        }
        return new TranscriptSegment(Guid.NewGuid(), speech.Start, speech.End, speech.Text.Trim(),
            "Microphone", final, number);
    }
}
