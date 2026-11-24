using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Speech;

namespace ConversationAssistant.Core.Qwen;

public sealed class QwenSpeechRecognitionService : ISpeechRecognitionService, IDisposable
{
    private const int BytesPerSecond = 32000; // 16 kHz, mono, 16-bit PCM.
    private const int WireChunkBytes = BytesPerSecond / 10;
    private const int QueueCapacity = 32;
    private const int MaxEventBytes = 256 * 1024;
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(1);
    private const string SetupFailure = "Could not start Qwen speech recognition. Check the pay-as-you-go API key, speech model, and network, then retry.";
    private const string StreamFailure = "Qwen speech recognition was interrupted. Check the network and retry.";
    private const string FinishFailure = "Qwen speech recognition did not finish in time. The last words may not have been saved.";
    private const string Rejected = "Qwen rejected the speech request. Check the pay-as-you-go API key, model access, and account quota, then retry.";
    private const string AudioOverload = "Qwen speech recognition could not keep up with microphone audio. Check the network and retry.";
    private const string InvalidAudio = "Qwen speech recognition received invalid microphone audio. Check the audio device and retry.";

    private readonly IQwenSettingsStore _settings;
    private readonly Func<IQwenSpeechSocket> _createSocket;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private volatile Session? _session;

    public QwenSpeechRecognitionService(IQwenSettingsStore settings)
        : this(settings, static () => new QwenSpeechSocket(), TimeProvider.System) { }

    internal QwenSpeechRecognitionService(IQwenSettingsStore settings, Func<IQwenSpeechSocket> createSocket, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _createSocket = createSocket;
        _clock = clock;
    }

    public event Action<SpeechText>? PartialTranscriptReceived;
    public event Action<SpeechText>? FinalTranscriptReceived;
    public event Action? SpeechStarted;
    public event Action? SpeechEnded;
    public event Action<Exception>? RecognitionError;

    public IReadOnlyList<ConversationLanguage> AvailableLanguages() =>
        [ConversationLanguage.Auto, ConversationLanguage.Chinese, ConversationLanguage.English];

    public void Start(ConversationLanguage language)
    {
        Session session;
        lock (_gate)
        {
            if (_session is not null) throw new InvalidOperationException("Speech recognition already active.");
            var (key, protocol) = LoadSettings(language);
            _session = session = new Session(protocol);
            _ = Task.Run(() => RunAsync(session, key));
        }

        try { session.Ready.Task.GetAwaiter().GetResult(); }
        catch
        {
            lock (_gate) { if (ReferenceEquals(_session, session)) _session = null; }
            throw;
        }
        if (ReferenceEquals(_session, session) && !session.Stopping) SpeechStarted?.Invoke();
    }

    public void AcceptAudio(byte[] pcm16Mono16000)
    {
        var session = _session;
        if (session is null || session.Stopping || session.Abort.Task.IsCompleted || session.Completed.Task.IsCompleted) return;
        if (pcm16Mono16000 is null || pcm16Mono16000.Length % 2 != 0 || pcm16Mono16000.Length > BytesPerSecond)
        {
            session.Abort.TrySetResult(InvalidAudio);
            return;
        }
        if (pcm16Mono16000.Length == 0) return;

        var frame = new AudioFrame((byte[])pcm16Mono16000.Clone(), _clock.GetUtcNow());
        if (!session.Audio.Writer.TryWrite(frame) && !session.Stopping)
            session.Abort.TrySetResult(AudioOverload);
    }

    public void Stop()
    {
        Session session;
        lock (_gate)
        {
            if (_session is not { Stopping: false } active) return;
            session = active;
            session.Stopping = true;
            session.Audio.Writer.TryComplete();
        }

        try
        {
            var error = session.Completed.Task.WaitAsync(FinishTimeout, _clock).GetAwaiter().GetResult();
            if (error is not null && !session.ErrorReported) throw error;
        }
        catch (TimeoutException)
        {
            session.Abort.TrySetResult(FinishFailure);
            throw new SpeechConnectionException(FinishFailure);
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_session, session)) _session = null; }
            if (session.Ready.Task.IsCompletedSuccessfully) SpeechEnded?.Invoke();
        }
    }

    public void Dispose() => Stop();

    public async Task TestAsync(ConversationLanguage language, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (key, protocol) = LoadSettings(language);
        try
        {
            using var socket = _createSocket();
            await SetupAsync(socket, protocol, key, cancellationToken).ConfigureAwait(false);
            // This probe checks the server's setup acknowledgement without submitting audio.
            await CloseAsync(socket, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (error is OperationCanceledException or SessionFailure or WebSocketException or HttpRequestException or JsonException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new SpeechConnectionException(error is SessionFailure ? error.Message : SetupFailure);
        }
    }

    private (string Key, QwenSpeechProtocol Protocol) LoadSettings(ConversationLanguage language)
    {
        QwenSettings settings;
        try { settings = _settings.Load(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException)
        { throw new SpeechConnectionException("Qwen settings could not be read. Save the API key and models again."); }
        string key;
        try { key = settings.RequireApiKey(); }
        catch (InvalidOperationException error) { throw new SpeechConnectionException(error.Message); }
        return (key, new QwenSpeechProtocol(settings.SpeechModel, language));
    }

    private async Task RunAsync(Session session, string key)
    {
        using var lifetime = new CancellationTokenSource();
        var work = RunSocketAsync(session, key, lifetime.Token);
        SpeechConnectionException? error = null;
        try
        {
            if (await Task.WhenAny(work, session.Abort.Task).ConfigureAwait(false) == session.Abort.Task)
                throw new SessionFailure(await session.Abort.Task.ConfigureAwait(false));
            await work.ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            error = new SpeechConnectionException(failure is SessionFailure ? failure.Message :
                session.Ready.Task.IsCompletedSuccessfully ? StreamFailure : SetupFailure);
        }
        finally
        {
            session.Audio.Writer.TryComplete();
            if (!work.IsCompleted) lifetime.Cancel();
            await ObserveAsync(work).ConfigureAwait(false);
        }

        bool report;
        lock (_gate)
        {
            if (!session.Ready.Task.IsCompleted) session.Ready.TrySetException(error ?? new SpeechConnectionException(SetupFailure));
            report = error is not null && session.Ready.Task.IsCompletedSuccessfully && !session.Stopping;
            session.ErrorReported = report;
            session.Completed.TrySetResult(error);
        }
        // Complete the session first: a subscriber is allowed to call Stop from this callback.
        if (report) RecognitionError?.Invoke(error!);
    }

    private async Task RunSocketAsync(Session session, string key, CancellationToken token)
    {
        using var socket = _createSocket();
        await SetupAsync(socket, session.Protocol, key, token).ConfigureAwait(false);
        session.Ready.TrySetResult();

        using var streaming = CancellationTokenSource.CreateLinkedTokenSource(token);
        var sending = SendAsync(session, socket, streaming.Token);
        var receiving = ReceiveAsync(session, socket, streaming.Token);
        try
        {
            var first = await Task.WhenAny(sending, receiving).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            await sending.ConfigureAwait(false);
            await receiving.ConfigureAwait(false);
        }
        finally
        {
            streaming.Cancel();
            await ObserveAsync(sending).ConfigureAwait(false);
            await ObserveAsync(receiving).ConfigureAwait(false);
        }
        await CloseAsync(socket, token).ConfigureAwait(false);
    }

    private async Task SetupAsync(IQwenSpeechSocket socket, QwenSpeechProtocol protocol, string key, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(SetupTimeout, _clock);
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        await socket.ConnectAsync(protocol.Endpoint, key, protocol.IsRealtime, setup.Token).ConfigureAwait(false);
        await socket.SendAsync(protocol.SetupMessage(), WebSocketMessageType.Text, setup.Token).ConfigureAwait(false);
        while (true)
        {
            var response = await ReadEventAsync(socket, protocol, setup.Token).ConfigureAwait(false);
            if (response.Kind == QwenSpeechProtocol.EventKind.Ready) return;
            if (response.Kind == QwenSpeechProtocol.EventKind.Failed) throw new SessionFailure(Rejected);
            if (response.Kind == QwenSpeechProtocol.EventKind.Finished) throw new SessionFailure(SetupFailure);
        }
    }

    private static async Task SendAsync(Session session, IQwenSpeechSocket socket, CancellationToken token)
    {
        await foreach (var frame in session.Audio.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            if (session.AudioStartTicks < 0)
                Volatile.Write(ref session.AudioStartTicks, Math.Max(0, frame.CapturedAt.UtcTicks - AudioTicks(frame.Pcm.Length)));
            for (var offset = 0; offset < frame.Pcm.Length; offset += WireChunkBytes)
            {
                token.ThrowIfCancellationRequested();
                var part = frame.Pcm.AsMemory(offset, Math.Min(WireChunkBytes, frame.Pcm.Length - offset));
                Interlocked.Add(ref session.BytesSent, part.Length);
                await session.Protocol.SendAudioAsync(socket, part, token).ConfigureAwait(false);
            }
        }
        session.FinishRequested = true;
        await socket.SendAsync(session.Protocol.FinishMessage(), WebSocketMessageType.Text, token).ConfigureAwait(false);
    }

    private async Task ReceiveAsync(Session session, IQwenSpeechSocket socket, CancellationToken token)
    {
        while (true)
        {
            var response = await ReadEventAsync(socket, session.Protocol, token).ConfigureAwait(false);
            switch (response.Kind)
            {
                case QwenSpeechProtocol.EventKind.Finished:
                    if (!session.FinishRequested) throw new SessionFailure(StreamFailure);
                    return;
                case QwenSpeechProtocol.EventKind.Failed:
                    throw new SessionFailure(Rejected);
                case QwenSpeechProtocol.EventKind.Partial:
                case QwenSpeechProtocol.EventKind.Final:
                    if (!string.IsNullOrWhiteSpace(response.Text) && ReferenceEquals(_session, session) && !session.Abort.Task.IsCompleted)
                    {
                        var transcript = Timestamp(session, response);
                        if (response.Kind == QwenSpeechProtocol.EventKind.Final) FinalTranscriptReceived?.Invoke(transcript);
                        else PartialTranscriptReceived?.Invoke(transcript);
                    }
                    break;
            }
        }
    }

    private SpeechText Timestamp(Session session, QwenSpeechProtocol.Event response)
    {
        var origin = Volatile.Read(ref session.AudioStartTicks);
        var maxTicks = DateTimeOffset.MaxValue.UtcTicks;
        if (origin < 0)
        {
            var now = _clock.GetUtcNow();
            return new SpeechText(response.Text!, now, now);
        }

        var latestAudio = origin + Math.Min(maxTicks - origin, AudioTicks(Volatile.Read(ref session.BytesSent)));
        long FromOffset(long? offset, long fallback) => offset is long ms && ms <= (maxTicks - origin) / TimeSpan.TicksPerMillisecond
            ? origin + ms * TimeSpan.TicksPerMillisecond : fallback;
        var end = FromOffset(response.EndMs, latestAudio);
        var start = Math.Min(FromOffset(response.StartMs, end), end);
        return new SpeechText(response.Text!, new DateTimeOffset(start, TimeSpan.Zero), new DateTimeOffset(end, TimeSpan.Zero));
    }

    private static long AudioTicks(long bytes) => bytes / 2 * TimeSpan.TicksPerSecond / (BytesPerSecond / 2);

    private static async Task<QwenSpeechProtocol.Event> ReadEventAsync(IQwenSpeechSocket socket,
        QwenSpeechProtocol protocol, CancellationToken token)
    {
        var message = new ArrayBufferWriter<byte>();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var result = await socket.ReceiveAsync(message.GetMemory(4096), token).ConfigureAwait(false);
            if (result.MessageType != WebSocketMessageType.Text || result.Count > MaxEventBytes - message.WrittenCount)
                throw new JsonException();
            message.Advance(result.Count);
            if (!result.EndOfMessage) continue;
            using var json = JsonDocument.Parse(message.WrittenMemory);
            return protocol.Parse(json.RootElement);
        }
    }

    private async Task CloseAsync(IQwenSpeechSocket socket, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(CloseTimeout, _clock);
        using var close = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try { await socket.CloseOutputAsync(close.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException)
        { /* Acknowledgement was received; disposing still releases the socket. */ }
    }

    private static async Task ObserveAsync(Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch (Exception) { /* The owning task publishes the original failure as a safe error. */ }
    }

    private sealed class Session(QwenSpeechProtocol protocol)
    {
        internal QwenSpeechProtocol Protocol { get; } = protocol;
        internal Channel<AudioFrame> Audio { get; } = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<SpeechConnectionException?> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string> Abort { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile bool Stopping;
        internal volatile bool FinishRequested;
        internal bool ErrorReported;
        internal long AudioStartTicks = -1;
        internal long BytesSent;
    }

    private sealed class SessionFailure(string message) : Exception(message);
    private readonly record struct AudioFrame(byte[] Pcm, DateTimeOffset CapturedAt);
}