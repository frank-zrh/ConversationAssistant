namespace ConversationAssistant.Core.WorkIQ;

public sealed record AnswerDocument(long Revision, string Html);

public sealed class AnswerRenderQueue
{
    private AnswerDocument? _pending;
    public AnswerDocument? Active { get; private set; }

    public void Enqueue(long revision, string html)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(html);
        _pending = new AnswerDocument(revision, html);
    }

    public AnswerDocument? StartNext()
    {
        if (Active is not null) return null;
        Active = _pending;
        _pending = null;
        return Active;
    }

    public bool Complete(long revision)
    {
        if (Active?.Revision != revision) return false;
        Active = null;
        return true;
    }

    public void Clear() => Active = _pending = null;
}
