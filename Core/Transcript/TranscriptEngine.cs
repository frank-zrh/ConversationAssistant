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
        var segment = new TranscriptSegment(Guid.NewGuid(), speech.Start, speech.End,
            speech.Text.Trim(), "Microphone", false);
        lock (_gate) _partial = segment;
        Updated?.Invoke(segment);
    }

    public TranscriptSegment CommitFinal(SpeechText speech)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speech.Text);
        var segment = new TranscriptSegment(Guid.NewGuid(), speech.Start, speech.End,
            speech.Text.Trim(), "Microphone", true);
        lock (_gate)
        {
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
            _partial = null;
        }
        Updated?.Invoke(null);
    }
}
