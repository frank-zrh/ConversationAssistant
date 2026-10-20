using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.WorkIQ;

namespace ConversationAssistant.Core.Conversation;

public sealed partial class ConversationSessionManager
{
    private const int MaximumQuestionInteractions = 6;
    private const int MaximumInteractionCharacters = 240;
    private readonly List<QuestionRequest> _userQuestionHistory = [];

    public IReadOnlyList<SuggestedQuestion> GetSuggestedQuestionsForAnalysis(Guid analysisId)
    {
        lock (_gate)
        {
            if (CurrentConversation is not { } session) return [];
            var analysis = session.Analyses.FirstOrDefault(item => item.Id == analysisId);
            return analysis is null ? [] : GetRecommendationBacklogLocked(session, analysis);
        }
    }

    private static IReadOnlyList<SuggestedQuestion> GetRecommendationBacklogLocked(
        ConversationSession session, ConversationAnalysisRequest analysis)
    {
        IEnumerable<SuggestedQuestion> candidates = analysis.Questions;
        if (session.Analysis?.Id == analysis.Id)
        {
            var currentIds = session.TranscriptSegments.Select(segment => segment.Id).ToHashSet();
            var selectedIndex = session.Analyses.IndexOf(analysis);
            candidates = session.Analyses.Take(selectedIndex + 1)
                .Where(item => item.Status == QuestionStatus.Completed && item.GroupId == analysis.GroupId &&
                    item.Transcript.All(segment => currentIds.Contains(segment.Id)))
                .Reverse().SelectMany(item => item.Questions);
        }
        return RecommendedQuestionBacklog.Select(candidates, session.Answers);
    }

    private static QuestionRequest? FindSuggestionAnswer(ConversationSession session, Guid questionId) =>
        session.Answers.LastOrDefault(answer => answer.SuggestedQuestionId == questionId &&
            answer.Status == QuestionStatus.Completed) ??
        session.Answers.FirstOrDefault(answer => answer.SuggestedQuestionId == questionId);

    private void RecordUserQuestionLocked(QuestionRequest request)
    {
        _userQuestionHistory.RemoveAll(item => item.Id == request.Id);
        _userQuestionHistory.Add(request);
        if (_userQuestionHistory.Count > MaximumQuestionInteractions)
            _userQuestionHistory.RemoveAt(0);
    }

    private IReadOnlyList<UserQuestionInteraction> SnapshotUserQuestionsLocked() => _userQuestionHistory
        .Where(item => item.Status is not (QuestionStatus.Cancelled or QuestionStatus.Ignored))
        .Select(item =>
        {
            var question = item.Question;
            var truncated = question.Length > MaximumInteractionCharacters;
            if (truncated)
            {
                var length = MaximumInteractionCharacters;
                if (char.IsHighSurrogate(question[length - 1])) length--;
                question = question[..length];
            }
            return new UserQuestionInteraction(question,
                item.SuggestedQuestionId is not null ? "SelectedRecommendation" :
                item.TranscriptSegmentId is not null ? "SelectedTranscript" : "TypedQuestion",
                item.Status.ToString(), truncated);
        }).ToArray();
}
