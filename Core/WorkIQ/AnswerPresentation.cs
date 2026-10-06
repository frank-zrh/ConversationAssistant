using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Localization;

namespace ConversationAssistant.Core.WorkIQ;

public static class AnswerPresentation
{
    public static string Compose(string? answer, QuestionStatus? status, IReadOnlyList<string> sources,
        ConversationLanguage language = ConversationLanguage.English)
    {
        var texts = UiText.For(language);
        if (string.IsNullOrWhiteSpace(answer))
            return texts[status switch
            {
                null => "AnswerSelect",
                QuestionStatus.Pending => "AnswerPending",
                QuestionStatus.Processing => "AnswerProcessing",
                QuestionStatus.Failed => "AnswerFailed",
                QuestionStatus.Cancelled => "AnswerCancelled",
                QuestionStatus.Ignored => "AnswerIgnored",
                _ => "AnswerEmpty"
            }];
        var additional = sources.Where(source => !string.IsNullOrWhiteSpace(source) &&
            !answer.Contains(source, StringComparison.OrdinalIgnoreCase)).ToArray();
        return additional.Length == 0 ? answer :
            answer + "\n\n## " + texts["AdditionalSources"] + "\n" +
            string.Join("\n", additional.Select(source => "- " + source));
    }
}
