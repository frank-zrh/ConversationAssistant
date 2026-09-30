using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Settings;

namespace ConversationAssistant.Core.Speech;

public sealed class SelectableSpeechRecognitionService : ISpeechRecognitionService
{
    private readonly ISpeechSettingsStore _settings;
    private readonly ISpeechRecognitionService _azure;
    private readonly ISpeechRecognitionService _offline;
    private ISpeechRecognitionService? _active;
    public event Action<SpeechText>? PartialTranscriptReceived;
    public event Action<SpeechText>? FinalTranscriptReceived;
    public event Action? SpeechStarted;
    public event Action? SpeechEnded;
    public event Action<Exception>? RecognitionError;

    public SelectableSpeechRecognitionService(ISpeechSettingsStore settings,
        ISpeechRecognitionService azure, ISpeechRecognitionService offline)
    {
        _settings = settings;
        _azure = azure;
        _offline = offline;
        foreach (var service in new[] { azure, offline })
        {
            service.PartialTranscriptReceived += speech =>
            {
                if (ReferenceEquals(_active, service)) PartialTranscriptReceived?.Invoke(speech);
            };
            service.FinalTranscriptReceived += speech =>
            {
                if (ReferenceEquals(_active, service)) FinalTranscriptReceived?.Invoke(speech);
            };
            service.RecognitionError += error =>
            {
                if (ReferenceEquals(_active, service)) RecognitionError?.Invoke(error);
            };
        }
    }

    public IReadOnlyList<ConversationLanguage> AvailableLanguages() =>
        Selected().AvailableLanguages();

    public void Start(ConversationLanguage language)
    {
        if (_active is not null) throw new InvalidOperationException("Speech recognition already active.");
        var service = Selected();
        _active = service;
        try { service.Start(language); }
        catch { _active = null; throw; }
        SpeechStarted?.Invoke();
    }

    public void Stop()
    {
        var service = Interlocked.Exchange(ref _active, null);
        if (service is null) return;
        try { service.Stop(); }
        finally { SpeechEnded?.Invoke(); }
    }

    public void AcceptAudio(byte[] pcm16Mono16000) => _active?.AcceptAudio(pcm16Mono16000);

    private ISpeechRecognitionService Selected() => _settings.Load().Provider switch
    {
        SpeechProvider.AzureSpeech => _azure,
        SpeechProvider.OfflineWhisper => _offline,
        _ => throw new InvalidOperationException("Unknown speech provider.")
    };
}
