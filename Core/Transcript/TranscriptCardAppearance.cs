using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Localization;

namespace ConversationAssistant.Core.Transcript;

public sealed record TranscriptCardAppearance(uint BackgroundArgb, string StatusText)
{
    public static TranscriptCardAppearance For(QuestionStatus? status,
        ConversationLanguage language = ConversationLanguage.Chinese)
    {
        var (color, key) = status switch
        {
            QuestionStatus.Completed => (0xFFE4F4E7u, "TranscriptCompleted"),
            QuestionStatus.Pending => (0xFFFFF4CCu, "TranscriptPending"),
            QuestionStatus.Processing => (0xFFFFF4CCu, "TranscriptProcessing"),
            QuestionStatus.Failed => (0xFFEADDD3u, "TranscriptFailed"),
            QuestionStatus.Cancelled => (0xFFFFFFFFu, "TranscriptCancelled"),
            _ => (0xFFFFFFFFu, "TranscriptUnanswered")
        };
        return new TranscriptCardAppearance(color, UiText.For(language)[key]);
    }
}
