using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.WorkIQ;

public static class RecommendedQuestionBacklog
{
    public const int MaximumUnansweredQuestions = 10;

    public static int Priority(RecommendationKind kind) => kind switch
    {
        RecommendationKind.Unanswered => 0,
        RecommendationKind.FollowUp => 1,
        RecommendationKind.ConversationControl => 2,
        _ => 3
    };

    public static bool MatchesManualRequest(SuggestedQuestion question, QuestionRequest answer)
    {
        static string Normalize(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return question.Kind != RecommendationKind.Unspecified && answer.SuggestedQuestionId is null &&
            ConversationQuestionParser.QuestionKey(question.Question) == ConversationQuestionParser.QuestionKey(answer.Question) &&
            !string.IsNullOrWhiteSpace(question.Evidence) &&
            Normalize(answer.ContextUsed).Contains(Normalize(question.Evidence), StringComparison.OrdinalIgnoreCase);
    }

    // Candidates are newest-batch-first, preserving ranking within each batch.
    public static IReadOnlyList<SuggestedQuestion> Select(IEnumerable<SuggestedQuestion> candidates,
        IEnumerable<QuestionRequest> answers)
    {
        var all = candidates.Where(question => !question.ResolvedInConversation).DistinctBy(question => question.Id).ToArray();
        var savedAnswers = answers.ToArray();
        var completed = savedAnswers.Where(answer => answer.Status == QuestionStatus.Completed &&
                answer.SuggestedQuestionId is not null)
            .Select(answer => answer.SuggestedQuestionId!.Value).ToHashSet();
        completed.UnionWith(all.Where(question => savedAnswers.Any(answer => answer.Status == QuestionStatus.Completed &&
            MatchesManualRequest(question, answer))).Select(question => question.Id));
        var questions = all
            .GroupBy(question => ConversationQuestionParser.QuestionKey(question.Question))
            .Select(group => group.FirstOrDefault(question => completed.Contains(question.Id)) ?? group.First())
            .ToArray();
        return questions.Where(question => !completed.Contains(question.Id)).OrderBy(question => Priority(question.Kind))
            .Take(MaximumUnansweredQuestions)
            .Concat(questions.Where(question => completed.Contains(question.Id))).ToArray();
    }
}
