namespace ConversationAssistant.Core.Speech;

public sealed class SpeechConnectionException(string message) : InvalidOperationException(message);