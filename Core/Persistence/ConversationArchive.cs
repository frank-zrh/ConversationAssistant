using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Persistence;

/// <summary>
/// An owned, detached snapshot. Call Capture while holding the session's mutation lock.
/// Archive content is plaintext; credentials, cloud sessions and capture configuration are excluded.
/// </summary>
public sealed class ConversationArchive
{
    public const int CurrentVersion = 1;
    public const string InterruptedRequestDiagnostic =
        "This request was interrupted before the conversation was saved. It was not resumed on import. Retry explicitly to submit it again.";

    private readonly ArchiveDocument _document;

    private ConversationArchive(ArchiveDocument document) => _document = document;

    public Guid SessionId => _document.SessionId;
    public Guid Id => SessionId;
    public int Version => _document.Version;

    internal ArchiveDocument Document => _document;

    public static ConversationArchive Capture(ConversationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var analyses = session.Analyses.ToList();
        if (session.Analysis is { } current && analyses.All(item => item.Id != current.Id))
            analyses.Add(current);

        var suggestions = session.SuggestedQuestions.Select(CaptureSuggestion).ToList();
        var knownSuggestions = new Dictionary<Guid, ArchiveSuggestion>();
        foreach (var suggestion in suggestions)
            knownSuggestions.TryAdd(suggestion.Id, suggestion);
        foreach (var question in analyses.SelectMany(item => item.Questions))
        {
            var snapshot = CaptureSuggestion(question);
            if (knownSuggestions.TryGetValue(snapshot.Id, out var existing))
            {
                if (existing != snapshot)
                    throw Invalid("A suggested question ID has conflicting snapshots.");
            }
            else
            {
                suggestions.Add(snapshot);
                knownSuggestions.Add(snapshot.Id, snapshot);
            }
        }

        var settings = session.Settings;
        return FromDocument(new ArchiveDocument
        {
            Version = CurrentVersion,
            SessionId = session.SessionId,
            StartTime = session.StartTime,
            EndTime = session.EndTime,
            Settings = new ArchiveSettings
            {
                Language = settings.Language,
                AnswerLanguage = settings.AnswerLanguage,
                AnswerStyle = settings.AnswerStyle,
                ContextWindowDuration = settings.ContextWindowDuration,
                MaxContextCharacters = settings.MaxContextCharacters,
                AutomaticAnalysis = settings.AutomaticAnalysis
            },
            TranscriptSegments = session.TranscriptSegments.Select(CaptureTranscript).ToArray(),
            Groups = session.Groups.Select(item => new ArchiveGroup
            {
                Id = item.Id,
                Number = item.Number,
                Name = item.Name,
                Timestamp = item.Timestamp,
                TranscriptIds = item.TranscriptIds.ToArray(),
                IsExpanded = item.IsExpanded
            }).ToArray(),
            NextGroupNumber = session.NextGroupNumber,
            AnalysisId = session.Analysis?.Id,
            Analyses = analyses.Select(item => new ArchiveAnalysis
            {
                Id = item.Id,
                Timestamp = item.Timestamp,
                IsManual = item.IsManual,
                Status = item.Status,
                Error = item.Error,
                Trigger = item.Trigger,
                GuidanceVersion = item.GuidanceVersion,
                Transcript = item.Transcript.Select(CaptureTranscript).ToArray(),
                ExistingQuestions = item.ExistingQuestions.ToArray(),
                GroupId = item.GroupId,
                GroupNumber = item.GroupNumber,
                GroupName = item.GroupName,
                QuestionIds = item.Questions.Select(question => question.Id).ToArray(),
                AddedQuestionCount = item.AddedQuestionCount,
                Response = item.Response,
                Sources = item.Sources.ToArray()
            }).ToArray(),
            SuggestedQuestions = suggestions.ToArray(),
            Answers = session.Answers.Select(item => new ArchiveAnswer
            {
                Id = item.Id,
                Timestamp = item.Timestamp,
                IsManual = item.IsManual,
                Status = item.Status,
                Error = item.Error,
                Question = item.Question,
                ContextUsed = item.ContextUsed,
                TranscriptSegmentId = item.TranscriptSegmentId,
                SuggestedQuestionId = item.SuggestedQuestionId,
                Answer = item.Answer,
                Sources = item.Sources.ToArray()
            }).ToArray()
        });
    }

    /// <summary>Creates local models only; no request, authentication or recording is resumed.</summary>
    public ConversationSession Restore()
    {
        var data = _document;
        var settings = data.Settings;
        var session = new ConversationSession
        {
            SessionId = data.SessionId,
            StartTime = data.StartTime,
            EndTime = data.EndTime ?? LatestTimestamp(data),
            Settings = new ConversationSettings
            {
                Language = settings.Language,
                AnswerLanguage = settings.AnswerLanguage,
                AnswerStyle = settings.AnswerStyle,
                ContextWindowDuration = settings.ContextWindowDuration,
                MaxContextCharacters = settings.MaxContextCharacters,
                AutomaticAnalysis = false
            },
            NextGroupNumber = data.NextGroupNumber
        };
        session.TranscriptSegments.AddRange(data.TranscriptSegments.Select(RestoreTranscript));
        session.Groups.AddRange(data.Groups.Select(item => new TranscriptGroup
        {
            Id = item.Id,
            Number = item.Number,
            Name = item.Name,
            Timestamp = item.Timestamp,
            TranscriptIds = item.TranscriptIds.ToArray(),
            IsExpanded = item.IsExpanded
        }));
        session.SuggestedQuestions.AddRange(data.SuggestedQuestions.Select(item => new SuggestedQuestion
        {
            Id = item.Id,
            Question = item.Question,
            Reason = item.Reason,
            ContextUsed = item.ContextUsed,
            Timestamp = item.Timestamp
        }));
        var suggestions = session.SuggestedQuestions.ToDictionary(item => item.Id);
        session.Analyses.AddRange(data.Analyses.Select(item => new ConversationAnalysisRequest
        {
            Id = item.Id,
            Timestamp = item.Timestamp,
            IsManual = item.IsManual,
            Status = RestoredStatus(item.Status),
            Error = RestoredError(item.Status, item.Error),
            Trigger = item.Trigger,
            GuidanceVersion = item.GuidanceVersion,
            Transcript = item.Transcript.Select(RestoreTranscript).ToArray(),
            ExistingQuestions = item.ExistingQuestions.ToArray(),
            GroupId = item.GroupId,
            GroupNumber = item.GroupNumber,
            GroupName = item.GroupName,
            Questions = item.QuestionIds.Select(id => suggestions[id]).ToArray(),
            AddedQuestionCount = item.AddedQuestionCount,
            Response = item.Response,
            Sources = item.Sources.ToArray()
        }));
        session.Analysis = data.AnalysisId is { } analysisId
            ? session.Analyses.Single(item => item.Id == analysisId)
            : null;
        session.Answers.AddRange(data.Answers.Select(item => new QuestionRequest
        {
            Id = item.Id,
            Timestamp = item.Timestamp,
            IsManual = item.IsManual,
            Status = RestoredStatus(item.Status),
            Error = RestoredError(item.Status, item.Error),
            Question = item.Question,
            ContextUsed = item.ContextUsed,
            TranscriptSegmentId = item.TranscriptSegmentId,
            SuggestedQuestionId = item.SuggestedQuestionId,
            Answer = item.Answer,
            Sources = item.Sources.ToArray()
        }));
        return session;
    }

    /// <summary>Deep-copies the history under a new local record ID, preserving any recorded end time.</summary>
    public ConversationArchive Fork() => new(_document with
    {
        SessionId = Guid.NewGuid(),
        Settings = _document.Settings with { },
        TranscriptSegments = _document.TranscriptSegments.Select(item => item with { }).ToArray(),
        Groups = _document.Groups.Select(item => item with
        {
            TranscriptIds = item.TranscriptIds.ToArray()
        }).ToArray(),
        Analyses = _document.Analyses.Select(item => item with
        {
            Transcript = item.Transcript.Select(transcript => transcript with { }).ToArray(),
            ExistingQuestions = item.ExistingQuestions.ToArray(),
            QuestionIds = item.QuestionIds.ToArray(),
            Sources = item.Sources.ToArray()
        }).ToArray(),
        SuggestedQuestions = _document.SuggestedQuestions.Select(item => item with { }).ToArray(),
        Answers = _document.Answers.Select(item => item with
        {
            Sources = item.Sources.ToArray()
        }).ToArray()
    });

    internal static ConversationArchive FromDocument(ArchiveDocument document)
    {
        ArchiveValidator.Validate(document);
        return new ConversationArchive(document);
    }

    private static ArchiveTranscript CaptureTranscript(TranscriptSegment item) => new()
    {
        Id = item.Id,
        TimestampStart = item.TimestampStart,
        TimestampEnd = item.TimestampEnd,
        Text = item.Text,
        Source = item.Source,
        IsFinal = item.IsFinal,
        SpeakerNumber = item.SpeakerNumber
    };

    private static TranscriptSegment RestoreTranscript(ArchiveTranscript item) =>
        new(item.Id, item.TimestampStart, item.TimestampEnd, item.Text, item.Source, item.IsFinal, item.SpeakerNumber);

    private static ArchiveSuggestion CaptureSuggestion(SuggestedQuestion item) => new()
    {
        Id = item.Id,
        Timestamp = item.Timestamp,
        Question = item.Question,
        Reason = item.Reason,
        ContextUsed = item.ContextUsed
    };

    private static QuestionStatus RestoredStatus(QuestionStatus status) =>
        IsInterrupted(status) ? QuestionStatus.Cancelled : status;

    private static string? RestoredError(QuestionStatus status, string? error) =>
        IsInterrupted(status)
            ? string.IsNullOrEmpty(error) ? InterruptedRequestDiagnostic : $"{InterruptedRequestDiagnostic}\n{error}"
            : error;

    private static bool IsInterrupted(QuestionStatus status) =>
        status is QuestionStatus.Pending or QuestionStatus.Processing;

    private static DateTimeOffset LatestTimestamp(ArchiveDocument data)
    {
        var latest = data.StartTime;
        void Include(DateTimeOffset timestamp)
        {
            if (timestamp > latest) latest = timestamp;
        }
        foreach (var item in data.TranscriptSegments) Include(item.TimestampEnd);
        foreach (var item in data.Groups) Include(item.Timestamp);
        foreach (var item in data.Analyses)
        {
            Include(item.Timestamp);
            foreach (var transcript in item.Transcript) Include(transcript.TimestampEnd);
        }
        foreach (var item in data.SuggestedQuestions) Include(item.Timestamp);
        foreach (var item in data.Answers) Include(item.Timestamp);
        return latest;
    }

    internal static InvalidDataException Invalid(string message) => new($"Invalid conversation archive: {message}");
}

internal sealed record ArchiveDocument
{
    public required int Version { get; init; }
    public required Guid SessionId { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset? EndTime { get; init; }
    public required ArchiveSettings Settings { get; init; }
    public required ArchiveTranscript[] TranscriptSegments { get; init; }
    public required ArchiveGroup[] Groups { get; init; }
    public required int NextGroupNumber { get; init; }
    public required Guid? AnalysisId { get; init; }
    public required ArchiveAnalysis[] Analyses { get; init; }
    public required ArchiveSuggestion[] SuggestedQuestions { get; init; }
    public required ArchiveAnswer[] Answers { get; init; }
}

internal sealed record ArchiveSettings
{
    public required ConversationLanguage Language { get; init; }
    public required ConversationLanguage AnswerLanguage { get; init; }
    public required AnswerStyle AnswerStyle { get; init; }
    public required TimeSpan ContextWindowDuration { get; init; }
    public required int MaxContextCharacters { get; init; }
    public required bool AutomaticAnalysis { get; init; }
}

internal sealed record ArchiveTranscript
{
    public required Guid Id { get; init; }
    public required DateTimeOffset TimestampStart { get; init; }
    public required DateTimeOffset TimestampEnd { get; init; }
    public required string Text { get; init; }
    public required string Source { get; init; }
    public required bool IsFinal { get; init; }
    public required int? SpeakerNumber { get; init; }
}

internal sealed record ArchiveGroup
{
    public required Guid Id { get; init; }
    public required int Number { get; init; }
    public required string? Name { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required Guid[] TranscriptIds { get; init; }
    public required bool IsExpanded { get; init; }
}

internal sealed record ArchiveAnalysis
{
    public required Guid Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required bool IsManual { get; init; }
    public required QuestionStatus Status { get; init; }
    public required string? Error { get; init; }
    public required ConversationAnalysisTrigger Trigger { get; init; }
    public int GuidanceVersion { get; init; }
    public required ArchiveTranscript[] Transcript { get; init; }
    public required string[] ExistingQuestions { get; init; }
    public required Guid? GroupId { get; init; }
    public required int? GroupNumber { get; init; }
    public required string? GroupName { get; init; }
    public required Guid[] QuestionIds { get; init; }
    public required int AddedQuestionCount { get; init; }
    public required string Response { get; init; }
    public required string[] Sources { get; init; }
}

internal sealed record ArchiveSuggestion
{
    public required Guid Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Question { get; init; }
    public required string Reason { get; init; }
    public required string ContextUsed { get; init; }
}

internal sealed record ArchiveAnswer
{
    public required Guid Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required bool IsManual { get; init; }
    public required QuestionStatus Status { get; init; }
    public required string? Error { get; init; }
    public required string Question { get; init; }
    public required string ContextUsed { get; init; }
    public required Guid? TranscriptSegmentId { get; init; }
    public required Guid? SuggestedQuestionId { get; init; }
    public required string Answer { get; init; }
    public required string[] Sources { get; init; }
}
