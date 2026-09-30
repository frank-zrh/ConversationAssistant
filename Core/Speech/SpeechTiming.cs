using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Speech;

public static class SpeechTiming
{
    public static SpeechText Create(string text, TimeSpan? audioDuration, DateTimeOffset receivedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var duration = audioDuration is { } value && value > TimeSpan.Zero ? value : TimeSpan.Zero;
        return new SpeechText(text, receivedAt - duration, receivedAt);
    }
}
