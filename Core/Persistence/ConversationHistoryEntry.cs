namespace ConversationAssistant.Core.Persistence;

public sealed record ConversationHistoryEntry(
    Guid SessionId, string Path, DateTimeOffset StartTime, DateTimeOffset? EndTime, string IntentSummary)
{
    public string Title { get; init; } = "";
}

public sealed record ConversationHistoryResult(
    IReadOnlyList<ConversationHistoryEntry> Entries, int UnavailableCount);
