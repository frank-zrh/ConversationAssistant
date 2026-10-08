namespace ConversationAssistant.Core.Models;

public enum ConversationUiState
{
    Idle, Starting, Listening, SpeechDetected, QuestionDetected, Thinking, Answering,
    Paused, Offline, Error
}

public enum ConversationLanguage { Auto, English, Chinese }
public enum AnswerStyle { Concise, Balanced, Detailed }
public enum QuestionSensitivity { Low, Medium, High }
public enum QuestionStatus { Pending, Processing, Completed, Ignored, Failed, Cancelled }

public sealed class ConversationSettings
{
    public ConversationLanguage Language { get; set; } = ConversationLanguage.Chinese;
    public ConversationLanguage AnswerLanguage { get; set; } = ConversationLanguage.Auto;
    public AnswerStyle AnswerStyle { get; set; } = AnswerStyle.Concise;
    public string? InputDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public AudioCaptureMode AudioMode { get; set; } = AudioCaptureMode.Microphone;
    public AudioCaptureOptions AudioCapture => new(AudioMode, InputDeviceId, OutputDeviceId);
    public TimeSpan ContextWindowDuration { get; set; } = TimeSpan.FromMinutes(3);
    public int MaxContextCharacters { get; set; } = 4000;
    public bool AutomaticAnalysis { get; set; } = true;

    public bool StoreAudio => false;
    public bool StoreTranscript => true;
    public bool ClearAfterConversation => false;
    public bool WebGrounding => false;
}

public sealed record TranscriptSegment(
    Guid Id, DateTimeOffset TimestampStart, DateTimeOffset TimestampEnd,
    string Text, string Source, bool IsFinal, int? SpeakerNumber = null);

public sealed record SpeechText(string Text, DateTimeOffset Start, DateTimeOffset End, string? SpeakerId = null);
public sealed record AudioDevice(string Id, string Name, string? EndpointId = null)
{
    public string EffectiveId => EndpointId ?? Id;
}
public sealed record QuestionDetectionResult(
    bool IsQuestion, double Confidence, string QuestionText,
    string TriggerReason, DateTimeOffset Timestamp, string ContextPrefix = "");
public sealed record WorkIqAnswer(string Text, IReadOnlyList<string> Sources, string? ConversationId);

public abstract class WorkIqRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required DateTimeOffset Timestamp { get; init; }
    public bool IsManual { get; init; }
    private readonly TaskCompletionSource _explicitRequest =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task ExplicitRequest => _explicitRequest.Task;
    internal void RequestExplicitly() => _explicitRequest.TrySetResult();
    public QuestionStatus Status { get; internal set; } = QuestionStatus.Pending;
    public string? Error { get; internal set; }
}

public sealed class QuestionRequest : WorkIqRequest
{
    public required string Question { get; init; }
    public required string ContextUsed { get; init; }
    public Guid? TranscriptSegmentId { get; init; }
    public Guid? SuggestedQuestionId { get; init; }
    public string Answer { get; internal set; } = "";
    public IReadOnlyList<string> Sources { get; internal set; } = [];
}

public sealed class ConversationSession
{
    public Guid SessionId { get; init; } = Guid.NewGuid();
    public DateTimeOffset StartTime { get; internal set; } = DateTimeOffset.Now;
    public DateTimeOffset? EndTime { get; internal set; }
    public string? WorkIqConversationId { get; internal set; }
    public required ConversationSettings Settings { get; init; }
    public List<TranscriptSegment> TranscriptSegments { get; } = [];
    public List<TranscriptGroup> Groups { get; } = [];
    public int NextGroupNumber { get; internal set; } = 1;
    public ConversationAnalysisRequest? Analysis { get; internal set; }
    public List<ConversationAnalysisRequest> Analyses { get; } = [];
    public List<SuggestedQuestion> SuggestedQuestions { get; } = [];
    public List<QuestionRequest> Answers { get; } = [];
}

public sealed class TranscriptGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required int Number { get; init; }
    public string? Name { get; internal set; }
    public required DateTimeOffset Timestamp { get; init; }
    public required IReadOnlyList<Guid> TranscriptIds { get; init; }
    public bool IsExpanded { get; internal set; } = true;
}
