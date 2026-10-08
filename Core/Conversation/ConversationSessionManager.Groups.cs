using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Transcript;

namespace ConversationAssistant.Core.Conversation;

public sealed partial class ConversationSessionManager
{
    public TranscriptGroup CreateTranscriptGroup()
    {
        TranscriptGroup group;
        lock (_gate)
        {
            var session = RequireWorkspaceLocked();
            var assigned = session.Groups.SelectMany(item => item.TranscriptIds).ToHashSet();
            var ids = session.TranscriptSegments.Where(item => !assigned.Contains(item.Id))
                .Select(item => item.Id).ToArray();
            if (ids.Length == 0)
                throw new InvalidOperationException("There are no ungrouped transcript entries.");
            group = new TranscriptGroup
            {
                Number = session.NextGroupNumber++,
                Timestamp = _timeProvider.GetUtcNow(),
                TranscriptIds = ids
            };
            session.Groups.Add(group);
            PersistLocked(session);
        }
        GroupsUpdated?.Invoke();
        return group;
    }

    public void RenameTranscriptGroup(Guid groupId, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120)
            throw new InvalidOperationException("Enter a group name between 1 and 120 characters.");
        lock (_gate)
        {
            var session = RequireWorkspaceLocked();
            FindGroup(session, groupId).Name = name.Trim();
            PersistLocked(session);
        }
        GroupsUpdated?.Invoke();
    }

    public void SetGroupExpanded(Guid groupId, bool expanded)
    {
        lock (_gate)
        {
            var session = RequireWorkspaceLocked();
            var group = FindGroup(session, groupId);
            if (group.IsExpanded == expanded) return;
            group.IsExpanded = expanded;
            PersistLocked(session);
        }
        GroupsUpdated?.Invoke();
    }

    public ConversationAnalysisRequest AnalyzeGroup(Guid groupId)
    {
        ConversationAnalysisRequest request;
        lock (_gate)
        {
            var session = RequireWorkspaceLocked();
            var group = FindGroup(session, groupId);
            if (session.Analyses.LastOrDefault(item => item.GroupId == groupId &&
                item.Status is QuestionStatus.Pending or QuestionStatus.Processing) is { } existing)
            {
                existing.RequestExplicitly();
                return existing;
            }
            request = QueueAnalysisLocked(session, ConversationAnalysisTrigger.Group, group);
        }
        PublishRequest(request);
        return request;
    }

    public void UpdateWorkspacePreferences(ConversationSettings settings)
    {
        lock (_gate)
        {
            if (CurrentConversation is not { } session || _closing) return;
            session.Settings.Language = settings.Language;
            session.Settings.AnswerLanguage = settings.AnswerLanguage;
            session.Settings.AnswerStyle = settings.AnswerStyle;
            session.Settings.ContextWindowDuration = settings.ContextWindowDuration;
            session.Settings.MaxContextCharacters = settings.MaxContextCharacters;
            session.Settings.AutomaticAnalysis = settings.AutomaticAnalysis;
            PersistLocked(session);
        }
    }

    public void ResetWorkIqContext()
    {
        lock (_gate)
            if (CurrentConversation is { } session) session.WorkIqConversationId = null;
    }

    private static TranscriptGroup FindGroup(ConversationSession session, Guid groupId) =>
        session.Groups.FirstOrDefault(group => group.Id == groupId) ??
        throw new InvalidOperationException("This group has been cleared or belongs to another conversation.");

    private ConversationSession RequireWorkspaceLocked()
    {
        if (_closing) throw new InvalidOperationException("Wait for the conversation operation to finish.");
        return CurrentConversation ??
            throw new InvalidOperationException("Start or import a conversation first.");
    }

    private static ConversationBuffer CreateBuffer(ConversationSession session)
    {
        var buffer = new ConversationBuffer(session.Settings.ContextWindowDuration, session.Settings.MaxContextCharacters);
        foreach (var segment in session.TranscriptSegments) buffer.Add(segment);
        return buffer;
    }

    private void EnsureQueueLocked(ConversationSession session)
    {
        if (_queue is not null) return;
        var queue = _queue = new WorkIqRequestQueue();
        var cancellation = _conversationCancellation = new CancellationTokenSource();
        _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _resumed.TrySetResult();
        _paused = Session is null;
        _worker = Task.Run(() => ProcessQueueAsync(session, queue, cancellation.Token));
    }
}
