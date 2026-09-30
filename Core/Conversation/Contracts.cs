using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Conversation;

public interface IAudioCaptureService
{
    event Action<byte[]>? AudioDataAvailable;
    event Action<Exception>? AudioError;
    event Action? AudioStarted;
    event Action? AudioStopped;
    event Action? AudioDeviceChanged;
    IReadOnlyList<AudioDevice> ListDevices();
    void Start(string? deviceId);
    void Stop();
}

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

public interface IAuthenticationService
{
    bool IsSignedIn { get; }
    Task SignInAsync(CancellationToken cancellationToken = default);
}

public interface INetworkStatus
{
    bool IsAvailable { get; }
}

public interface IWorkIqClient
{
    // The conversation ID is null on the first question of a conversation.
    Task<WorkIqAnswer> AskAsync(string prompt, string? conversationId, CancellationToken cancellationToken);
}

public sealed class WorkIqException(string message, Exception? innerException = null)
    : Exception(message, innerException);
