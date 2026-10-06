using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Conversation;

internal sealed class ConversationAnalysisSchedule
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    public const int BatchSize = 7;
    private int _totalCount;
    private int _completedCount;
    private int _attemptedCount;
    private DateTimeOffset? _pendingSince;
    private DateTimeOffset? _retryAfter;

    public int PendingCount => _totalCount - _completedCount;

    public void RecordFinal(DateTimeOffset now)
    {
        _totalCount++;
        _pendingSince ??= now;
    }

    public ConversationAnalysisTrigger? DueTrigger(DateTimeOffset now)
    {
        if (PendingCount == 0) return null;
        if (_totalCount - _attemptedCount >= BatchSize)
            return ConversationAnalysisTrigger.MessageCount;
        var deadline = _retryAfter ?? _pendingSince?.Add(Interval);
        return deadline is not null && now >= deadline
            ? ConversationAnalysisTrigger.Interval : null;
    }

    public void BeginAttempt()
    {
        _attemptedCount = _totalCount;
        _pendingSince = null;
        _retryAfter = null;
    }

    public void Complete(int coveredCount) => _completedCount = coveredCount;

    public void Fail(DateTimeOffset now) => _retryAfter = now.Add(Interval);

    public void Reset()
    {
        _totalCount = _completedCount = _attemptedCount = 0;
        _pendingSince = _retryAfter = null;
    }
}
