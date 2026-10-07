using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Transcript;

public static class TranscriptSpeaker
{
    public static string Label(int? number, UiText texts) =>
        number is { } value ? texts.Format("SpeakerNumber", value) : texts["SpeakerUnknown"];

    public static string ContextText(TranscriptSegment segment) => segment.SpeakerNumber is { } number
        ? $"[{Label(number, UiText.For(ConversationLanguage.English))}] {segment.Text}"
        : segment.Text;
}
