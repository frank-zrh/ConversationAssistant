using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Transcript;

public interface IConversationBuffer
{
    void Add(TranscriptSegment segment);
    string GetRecentContext(DateTimeOffset now);
    string GetContextBefore(DateTimeOffset timestamp);
    void Clear();
}

public sealed class ConversationBuffer(TimeSpan duration, int maxCharacters) : IConversationBuffer
{
    private readonly object _gate = new();
    private readonly Queue<TranscriptSegment> _items = new();

    public void Add(TranscriptSegment segment)
    {
        if (!segment.IsFinal) throw new ArgumentException("Only final speech enters context.", nameof(segment));
        if (duration <= TimeSpan.Zero || maxCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCharacters), "Context limits must be positive.");
        lock (_gate)
        {
            _items.Enqueue(segment);
            Prune(segment.TimestampEnd);
        }
    }

    public string GetRecentContext(DateTimeOffset now) => GetContextBefore(now);

    public string GetContextBefore(DateTimeOffset timestamp)
    {
        lock (_gate)
        {
            Prune(timestamp);
            var text = string.Join(Environment.NewLine, _items
                .Where(item => item.TimestampEnd <= timestamp &&
                    item.TimestampEnd >= timestamp - duration)
                .Select(item => item.Text));
            return text.Length > maxCharacters ? text[^maxCharacters..] : text;
        }
    }

    public void Clear()
    {
        lock (_gate) _items.Clear();
    }

    private void Prune(DateTimeOffset now)
    {
        while (_items.Count > 0 && _items.Peek().TimestampEnd < now - duration)
            _items.Dequeue();
        var length = _items.Sum(s => s.Text.Length + Environment.NewLine.Length);
        while (_items.Count > 1 && length > maxCharacters)
            length -= _items.Dequeue().Text.Length + Environment.NewLine.Length;
    }
}
