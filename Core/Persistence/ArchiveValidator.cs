using ConversationAssistant.Core.Models;
using static ConversationAssistant.Core.Persistence.ConversationArchiveLimits;

namespace ConversationAssistant.Core.Persistence;

internal sealed class ArchiveValidator
{
    private int _totalItems;
    private readonly Dictionary<Guid, string> _ids = [];

    public static void Validate(ArchiveDocument document) => new ArchiveValidator().ValidateDocument(document);

    private void ValidateDocument(ArchiveDocument data)
    {
        Require(data is not null, "The document is required.");
        Require(data.Version == ConversationArchive.CurrentVersion, "The archive version is not supported.");
        AddId(data.SessionId, "session");
        Timestamp(data.StartTime);
        if (data.EndTime is { } end)
        {
            Timestamp(end);
            Require(end >= data.StartTime, "The end time precedes the start time.");
        }
        Require(data.Settings is not null, "Settings are required.");
        EnumValue(data.Settings.Language);
        Require(data.Settings.Language is ConversationLanguage.Chinese or ConversationLanguage.English,
            "The listening language must be Chinese or English.");
        EnumValue(data.Settings.AnswerLanguage);
        EnumValue(data.Settings.AnswerStyle);
        Require(data.Settings.ContextWindowDuration > TimeSpan.Zero &&
            data.Settings.ContextWindowDuration <= TimeSpan.FromDays(365), "The context window is out of range.");
        Require(data.Settings.MaxContextCharacters is > 0 and <= MaximumTextLength,
            "The context character limit is out of range.");
        Transcripts(data.TranscriptSegments);
        var currentTranscriptIds = data.TranscriptSegments.Select(item => item.Id).ToHashSet();
        List(data.Analyses);
        foreach (var analysis in data.Analyses)
        {
            AddId(analysis.Id, "analysis");
            Request(analysis.Timestamp, analysis.Status, analysis.Error);
            EnumValue(analysis.Trigger);
            Require(analysis.GuidanceVersion >= 0, "The analysis guidance version is invalid.");
            Transcripts(analysis.Transcript);
            Texts(analysis.ExistingQuestions);
            OptionalId(analysis.GroupId);
            GroupName(analysis.GroupName);
            Require(analysis.GroupId.HasValue == analysis.GroupNumber.HasValue,
                "A group analysis must include both a group ID and number.");
            Require(analysis.GroupNumber is null or > 0, "Group numbers must be positive.");
            Require((analysis.Trigger == ConversationAnalysisTrigger.Group) == analysis.GroupId.HasValue,
                "Group analysis metadata does not match its trigger.");
            Require(analysis.GroupId.HasValue || analysis.GroupName is null,
                "A group name requires a group ID.");
            IdList(analysis.QuestionIds);
            Require(analysis.AddedQuestionCount >= 0 && analysis.AddedQuestionCount <= analysis.QuestionIds.Length,
                "The added question count is out of range.");
            Text(analysis.Response);
            Texts(analysis.Sources, MaximumSourceLength);
        }
        OptionalId(data.AnalysisId);
        Require(data.AnalysisId is null || data.Analyses.Any(item => item.Id == data.AnalysisId),
            "The current analysis ID does not refer to a saved analysis.");

        List(data.Groups);
        var groupNumbers = new HashSet<int>();
        var groupMembership = new HashSet<Guid>();
        foreach (var group in data.Groups)
        {
            AddId(group.Id, "group");
            Require(group.Number > 0 && groupNumbers.Add(group.Number), "Group numbers must be positive and unique.");
            GroupName(group.Name);
            Timestamp(group.Timestamp);
            IdList(group.TranscriptIds);
            Require(group.TranscriptIds.Length > 0, "A group must contain at least one transcript.");
            foreach (var id in group.TranscriptIds)
            {
                Require(currentTranscriptIds.Contains(id), "A group refers to an unknown transcript.");
                Require(groupMembership.Add(id), "A transcript cannot belong to more than one group.");
            }
        }
        Require(data.NextGroupNumber > 0 && data.Groups.All(item => item.Number < data.NextGroupNumber),
            "The next group number must follow all saved groups.");
        var groupsById = data.Groups.ToDictionary(item => item.Id);
        var historicalGroupNumbers = new Dictionary<Guid, int>();
        foreach (var analysis in data.Analyses)
        {
            if (analysis.GroupId is not { } id) continue;
            if (groupsById.TryGetValue(id, out var group))
            {
                Require(group.Number == analysis.GroupNumber, "A group analysis has the wrong group number.");
                var members = group.TranscriptIds.ToHashSet();
                Require(analysis.Transcript.All(item => members.Contains(item.Id)),
                    "A group analysis contains a transcript outside its group.");
            }
            else
            {
                // Clear can remove the visible groups; their analysis snapshots remain standalone history.
                Require(!_ids.TryGetValue(id, out var kind) || kind == "historical group",
                    "A historical group ID conflicts with another record.");
                Require(!historicalGroupNumbers.TryGetValue(id, out var number) || number == analysis.GroupNumber,
                    "A historical group ID has conflicting group numbers.");
                _ids[id] = "historical group";
                historicalGroupNumbers[id] = analysis.GroupNumber!.Value;
            }
        }

        List(data.SuggestedQuestions);
        var suggestionIds = new HashSet<Guid>();
        foreach (var suggestion in data.SuggestedQuestions)
        {
            AddId(suggestion.Id, "suggestion");
            suggestionIds.Add(suggestion.Id);
            Timestamp(suggestion.Timestamp);
            Text(suggestion.Question);
            Require(!string.IsNullOrWhiteSpace(suggestion.Question), "A suggested question cannot be empty.");
            Text(suggestion.Reason);
            Text(suggestion.ContextUsed);
        }
        foreach (var analysis in data.Analyses)
            foreach (var id in analysis.QuestionIds)
                Require(suggestionIds.Contains(id), "An analysis refers to an unknown suggested question.");
        List(data.Answers);
        foreach (var answer in data.Answers)
        {
            AddId(answer.Id, "answer");
            Request(answer.Timestamp, answer.Status, answer.Error);
            Text(answer.Question);
            Require(!string.IsNullOrWhiteSpace(answer.Question), "An answer's question cannot be empty.");
            Text(answer.ContextUsed);
            OptionalId(answer.TranscriptSegmentId);
            OptionalId(answer.SuggestedQuestionId);
            // Transcript links may outlive Clear even when no analysis captured that transcript.
            if (answer.TranscriptSegmentId is { } transcriptId)
            {
                Require(!_ids.TryGetValue(transcriptId, out var kind) || kind == "transcript",
                    "An answer's transcript ID refers to a different kind of record.");
                _ids.TryAdd(transcriptId, "transcript");
            }
            Require(answer.SuggestedQuestionId is null || suggestionIds.Contains(answer.SuggestedQuestionId.Value),
                "An answer refers to an unknown suggested question.");
            Text(answer.Answer);
            Texts(answer.Sources, MaximumSourceLength);
        }
    }

    private void Transcripts(ArchiveTranscript[] items)
    {
        List(items);
        var localIds = new HashSet<Guid>();
        foreach (var item in items)
        {
            Require(item.Id != Guid.Empty && localIds.Add(item.Id), "Transcript IDs must be nonempty and unique in each snapshot.");
            Require(!_ids.TryGetValue(item.Id, out var kind) || kind == "transcript",
                "A transcript ID conflicts with another record.");
            _ids[item.Id] = "transcript";
            Timestamp(item.TimestampStart);
            Timestamp(item.TimestampEnd);
            Require(item.TimestampEnd >= item.TimestampStart, "A transcript ends before it starts.");
            Text(item.Text);
            Text(item.Source, MaximumSourceLength);
            Require(item.SpeakerNumber is null or > 0, "Speaker numbers must be positive.");
        }
    }

    private void Request(DateTimeOffset timestamp, QuestionStatus status, string? error)
    {
        Timestamp(timestamp);
        EnumValue(status);
        if (error is not null)
        {
            var diagnosticPrefix = ConversationArchive.InterruptedRequestDiagnostic + "\n";
            var maximum = status == QuestionStatus.Cancelled && error.StartsWith(diagnosticPrefix, StringComparison.Ordinal)
                ? MaximumTextLength + diagnosticPrefix.Length
                : MaximumTextLength;
            Text(error, maximum);
        }
    }

    private void List<T>(T[] items)
    {
        Require(items is not null, "Lists cannot be null.");
        Require(items.Length <= MaximumListLength, "A list exceeds the archive item limit.");
        _totalItems += items.Length;
        Require(_totalItems <= MaximumTotalItems, "The archive exceeds the total item limit.");
        foreach (var item in items)
            Require(item is not null, "Lists cannot contain null items.");
    }

    private void IdList(Guid[] items)
    {
        List(items);
        var ids = new HashSet<Guid>();
        foreach (var id in items)
            Require(id != Guid.Empty && ids.Add(id), "Referenced IDs must be nonempty and unique.");
    }

    private void Texts(string[] items, int maximum = MaximumTextLength)
    {
        List(items);
        foreach (var item in items) Text(item, maximum);
    }

    private void AddId(Guid id, string kind) =>
        Require(id != Guid.Empty && _ids.TryAdd(id, kind), "Record IDs must be nonempty and unique.");

    private static void OptionalId(Guid? id) => Require(id != Guid.Empty, "Referenced IDs cannot be empty.");

    private static void Timestamp(DateTimeOffset timestamp) =>
        Require(timestamp > DateTimeOffset.MinValue && timestamp < DateTimeOffset.MaxValue, "A timestamp is invalid.");

    private static void EnumValue<T>(T value) where T : struct, Enum =>
        Require(Enum.IsDefined(value), "An enum value is not supported.");

    private static void GroupName(string? name)
    {
        if (name is null) return;
        Text(name, MaximumGroupNameLength);
        Require(!string.IsNullOrWhiteSpace(name), "A group name cannot be blank.");
    }

    private static void Text(string text, int maximum = MaximumTextLength) =>
        Require(text is not null && text.Length <= maximum, "A required text value is null or exceeds its length limit.");

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw ConversationArchive.Invalid(message);
    }
}
