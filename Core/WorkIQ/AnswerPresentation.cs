using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.WorkIQ;

public static class AnswerPresentation
{
    public static string Compose(string? answer, QuestionStatus? status, IReadOnlyList<string> sources)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return status switch
            {
                null => "Select a question to view its answer.",
                QuestionStatus.Pending => "Question queued for Work IQ.",
                QuestionStatus.Processing => "Thinking with Work IQ...",
                QuestionStatus.Failed => "Work IQ could not answer. See the error below, then retry.",
                QuestionStatus.Cancelled => "This question was cancelled.",
                QuestionStatus.Ignored => "This question was ignored.",
                _ => "Work IQ returned an empty answer. Retry this question."
            };
        var additional = sources.Where(source => !string.IsNullOrWhiteSpace(source) &&
            !answer.Contains(source, StringComparison.OrdinalIgnoreCase)).ToArray();
        return additional.Length == 0 ? answer :
            answer + "\n\n## ADDITIONAL SOURCES\n" +
            string.Join("\n", additional.Select(source => "- " + source));
    }
}
