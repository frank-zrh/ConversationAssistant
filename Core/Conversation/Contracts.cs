using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Conversation;

public interface IAudioCaptureService
{
    event Action<byte[]>? AudioDataAvailable;
    event Action<Exception>? AudioError;
    event Action? AudioStarted;
    event Action? AudioStopped;
    event Action? AudioDeviceChanged;
    IReadOnlyList<AudioDevice> ListDevices(AudioDeviceKind kind = AudioDeviceKind.Input);
    void Start(AudioCaptureOptions options);
    void Stop();
}

public sealed class AudioCaptureException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public interface ISpeechRecognitionService
{
    event Action<SpeechText>? PartialTranscriptReceived;
    event Action<SpeechText>? FinalTranscriptReceived;
    event Action? SpeechStarted;
    event Action? SpeechEnded;
    event Action<Exception>? RecognitionError;
    IReadOnlyList<ConversationLanguage> AvailableLanguages();
    void Start(ConversationLanguage language);
    void AcceptAudio(byte[] pcm16Mono16000);
    void Stop();
}

public interface INetworkStatus
{
    bool IsAvailable { get; }
}

public interface IWorkIqClient
{
    bool SupportsFullPrompt => false;
    // The conversation ID is null on the first question of a conversation.
    Task<WorkIqAnswer> AskAsync(string prompt, string? conversationId, CancellationToken cancellationToken);
    Task<WorkIqAnswer> AnalyzeAsync(string prompt, string? conversationId, CancellationToken cancellationToken) =>
        AskAsync(prompt, conversationId, cancellationToken);
}

public sealed class WorkIqException(string message, Exception? innerException = null)
    : Exception(message, innerException);
