namespace ConversationAssistant.Core.Models;

public enum ConversationAnalysisTrigger { Manual, Interval, MessageCount }

public sealed class ConversationAnalysisRequest : WorkIqRequest
{
    public required IReadOnlyList<TranscriptSegment> Transcript { get; init; }
    public required IReadOnlyList<string> ExistingQuestions { get; init; }
    public required ConversationAnalysisTrigger Trigger { get; init; }
    public int AddedQuestionCount { get; internal set; }
}

public sealed class SuggestedQuestion
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Question { get; init; }
    public required string Reason { get; init; }
    public required string ContextUsed { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
