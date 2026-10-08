namespace ConversationAssistant.Core.Models;

public enum ConversationAnalysisTrigger { Manual, Interval, MessageCount, Group }

public sealed class ConversationAnalysisRequest : WorkIqRequest
{
    public required IReadOnlyList<TranscriptSegment> Transcript { get; init; }
    public required IReadOnlyList<string> ExistingQuestions { get; init; }
    public required ConversationAnalysisTrigger Trigger { get; init; }
    // Older archives default to zero; their questions are not reused under newer guidance.
    public int GuidanceVersion { get; init; }
    public Guid? GroupId { get; init; }
    public int? GroupNumber { get; init; }
    public string? GroupName { get; init; }
    public IReadOnlyList<SuggestedQuestion> Questions { get; internal set; } = [];
    public string Response { get; internal set; } = "";
    public IReadOnlyList<string> Sources { get; internal set; } = [];
    public int AddedQuestionCount { get; internal set; }
}

public sealed class SuggestedQuestion
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Question { get; init; }
    public required string Reason { get; init; }
    public required string ContextUsed { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
