using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Settings;

namespace ConversationAssistant.Core.Speech;

public sealed class SpeechConnectionException(string message) : InvalidOperationException(message);

public interface IAzureSpeechSession : IDisposable
{
    event Action<SpeechText>? Partial;
    event Action<SpeechText>? Final;
    event Action<SpeechConnectionException>? Failed;
    Task StartAsync(CancellationToken cancellationToken);
    void Write(byte[] pcm);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IAzureSpeechSessionFactory
{
    IAzureSpeechSession Create(SpeechServiceConfiguration configuration, ConversationLanguage language);
}

public sealed class AzureSpeechRecognitionService(
    ISpeechSettingsStore settings, IAzureSpeechSessionFactory factory) : ISpeechRecognitionService, IDisposable
{
    private readonly object _gate = new();
    private IAzureSpeechSession? _session;
    private bool _active;
    private SpeechConnectionException? _connectionError;
    public event Action<SpeechText>? PartialTranscriptReceived;
    public event Action<SpeechText>? FinalTranscriptReceived;
    public event Action? SpeechStarted;
    public event Action? SpeechEnded;
    public event Action<Exception>? RecognitionError;

    public IReadOnlyList<ConversationLanguage> AvailableLanguages() =>
        [ConversationLanguage.Chinese, ConversationLanguage.English];

    public void Start(ConversationLanguage language)
    {
        if (language is not (ConversationLanguage.Chinese or ConversationLanguage.English))
            throw new ArgumentOutOfRangeException(nameof(language));
        var configuration = settings.Load();
        configuration.ValidateForAzure();
        IAzureSpeechSession session;
        lock (_gate)
        {
            if (_session is not null) throw new InvalidOperationException("Speech recognition already active.");
            session = factory.Create(configuration, language);
            _session = session;
            _connectionError = null;
        }
        session.Partial += speech =>
        {
            if (IsActive(session)) PartialTranscriptReceived?.Invoke(speech);
        };
        session.Final += speech =>
        {
            if (IsActive(session)) FinalTranscriptReceived?.Invoke(speech);
        };
        session.Failed += error =>
        {
            lock (_gate)
            {
                if (!ReferenceEquals(session, _session)) return;
                _connectionError = error;
                if (!_active) return;
                _active = false;
            }
            RecognitionError?.Invoke(error);
        };
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            session.StartAsync(deadline.Token).GetAwaiter().GetResult();
            lock (_gate)
            {
                if (_connectionError is not null) throw _connectionError;
                _active = true;
            }
        }
        catch (OperationCanceledException)
        {
            ReleaseFailedStart(session);
            throw new SpeechConnectionException("连接 Azure Speech 超时。请检查 Endpoint、Entra 登录、资源角色和网络，然后重试。");
        }
        catch
        {
            ReleaseFailedStart(session);
            throw;
        }
        SpeechStarted?.Invoke();
    }

    public void AcceptAudio(byte[] pcm16Mono16000)
    {
        ArgumentNullException.ThrowIfNull(pcm16Mono16000);
        if (pcm16Mono16000.Length % 2 != 0)
            throw new InvalidOperationException("Microphone returned an incomplete 16-bit PCM sample.");
        lock (_gate)
        {
            if (_active) _session!.Write(pcm16Mono16000);
        }
    }

    public void Stop()
    {
        IAzureSpeechSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
            _active = false;
            _connectionError = null;
        }
        if (session is null) return;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            session.StopAsync(deadline.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new SpeechConnectionException("停止 Azure Speech 超时，连接已释放。");
        }
        finally
        {
            session.Dispose();
            SpeechEnded?.Invoke();
        }
    }

    public void Dispose() => Stop();

    private bool IsActive(IAzureSpeechSession session)
    {
        lock (_gate) return _active && ReferenceEquals(_session, session);
    }

    private void ReleaseFailedStart(IAzureSpeechSession session)
    {
        lock (_gate) { _session = null; _active = false; }
        session.Dispose();
    }
}
