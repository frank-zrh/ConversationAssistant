namespace ConversationAssistant.Core.Models;

public enum ConversationAnalysisTrigger { Manual, Interval, MessageCount, Group }

public sealed record UserQuestionInteraction(string Question, string Action, string Status, bool Truncated = false);

public sealed class ConversationAnalysisRequest : WorkIqRequest
{
    public required IReadOnlyList<TranscriptSegment> Transcript { get; init; }
    public required IReadOnlyList<string> ExistingQuestions { get; init; }
    public IReadOnlyList<string> ExistingProductNames { get; internal set; } = [];
    public IReadOnlyList<UserQuestionInteraction> UserQuestionInteractions { get; init; } = [];
    public required ConversationAnalysisTrigger Trigger { get; init; }
    public string OverallContext { get; init; } = "";
    public string? StageContext { get; init; }
    public string PreviousStageSummaries { get; init; } = "";
    public string IntentSummary { get; internal set; } = "";
    public string StageSummary { get; internal set; } = "";
    public ConversationAssistance? Assistance { get; internal set; }
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

public sealed record ConversationAssistance
{
    public const int MaximumConclusionLength = 240;
    public const int MaximumWordingLength = 400;
    public const int MaximumPointLength = 200;
    public const int MaximumUncertaintyLength = 240;
    public const int MaximumKeyPoints = 2;
    public const int MaximumSources = 3;
    public const int MaximumSourceLength = 2048;

    public required string Conclusion { get; init; }
    public string SuggestedWording { get; init; } = "";
    public IReadOnlyList<string> KeyPoints { get; init; } = [];
    public string Uncertainty { get; init; } = "";
    public IReadOnlyList<string> Sources { get; init; } = [];

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(Conclusion) && Conclusion.Length <= MaximumConclusionLength &&
        SuggestedWording is not null && SuggestedWording.Length <= MaximumWordingLength &&
        Uncertainty is not null && Uncertainty.Length <= MaximumUncertaintyLength &&
        ValidItems(KeyPoints, MaximumKeyPoints, MaximumPointLength) &&
        ValidItems(Sources, MaximumSources, MaximumSourceLength);

    public ConversationAssistance Snapshot() => this with { KeyPoints = KeyPoints.ToArray(), Sources = Sources.ToArray() };

    private static bool ValidItems(IReadOnlyList<string>? items, int maximumCount, int maximumLength) =>
        items is not null && items.Count <= maximumCount &&
        items.All(item => !string.IsNullOrWhiteSpace(item) && item.Length <= maximumLength);
}

public sealed class SuggestedQuestion
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Question { get; init; }
    public required string Reason { get; init; }
    public required string ContextUsed { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
