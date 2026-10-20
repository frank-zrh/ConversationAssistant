using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.WorkIQ;

public static class RecommendedQuestionBacklog
{
    public const int MaximumUnansweredQuestions = 10;

    // Candidates are newest-batch-first, preserving ranking within each batch.
    public static IReadOnlyList<SuggestedQuestion> Select(IEnumerable<SuggestedQuestion> candidates,
        IEnumerable<QuestionRequest> answers)
    {
        var completed = answers.Where(answer => answer.Status == QuestionStatus.Completed &&
                answer.SuggestedQuestionId is not null)
            .Select(answer => answer.SuggestedQuestionId!.Value).ToHashSet();
        var questions = candidates.DistinctBy(question => question.Id)
            .GroupBy(question => ConversationQuestionParser.QuestionKey(question.Question))
            .Select(group => group.FirstOrDefault(question => completed.Contains(question.Id)) ?? group.First())
            .ToArray();
        return questions.Where(question => !completed.Contains(question.Id)).Take(MaximumUnansweredQuestions)
            .Concat(questions.Where(question => completed.Contains(question.Id))).ToArray();
    }
}
