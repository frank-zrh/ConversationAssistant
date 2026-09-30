using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Settings;
using ConversationAssistant.Core.Speech;
using ConversationAssistant_App.Authentication;
using Azure.Core;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace ConversationAssistant_App.Speech;

public sealed class AzureSpeechSessionFactory(ISpeechCredentialProvider identity) : IAzureSpeechSessionFactory
{
    internal static SpeechConfig CreateConfiguration(SpeechServiceConfiguration settings,
        ConversationLanguage language, TokenCredential credential)
    {
        settings.ValidateForAzure();
        if (language is not (ConversationLanguage.Chinese or ConversationLanguage.English))
            throw new ArgumentOutOfRangeException(nameof(language));
        var uri = AzureSpeechEndpoint.GetSdkUri(settings.ServiceUri);
        ArgumentNullException.ThrowIfNull(credential);
        var configuration = SpeechConfig.FromEndpoint(uri, credential);
        configuration.SpeechRecognitionLanguage = language == ConversationLanguage.Chinese ? "zh-CN" : "en-US";
        configuration.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, "800");
        return configuration;
    }

    public IAzureSpeechSession Create(SpeechServiceConfiguration configuration, ConversationLanguage language)
    {
        configuration.ValidateForAzure();
        var credential = identity.GetCredential(configuration);
        try { return new AzureSpeechSession(configuration, language, credential); }
        catch (Exception error) when (error is ApplicationException or
            ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            throw new SpeechConnectionException(AzureSpeechDiagnostics.Initialization(error));
        }
    }
}

internal sealed class AzureSpeechSession : IAzureSpeechSession
{
    private readonly SpeechConfig _configuration;
    private readonly AudioStreamFormat _format;
    private readonly PushAudioInputStream _input;
    private readonly AudioConfig _audio;
    private readonly SpeechRecognizer _recognizer;
    private readonly ConversationLanguage _language;
    private readonly Connection _connection;
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _audioOriginUtcTicks;
    private int _stopping;
    private int _disposed;
    private int _failed;
    private SpeechConnectionException? _failure;

    public event Action<SpeechText>? Partial;
    public event Action<SpeechText>? Final;
    public event Action<SpeechConnectionException>? Failed;

    public AzureSpeechSession(SpeechServiceConfiguration settings, ConversationLanguage language, TokenCredential credential)
    {
        settings.ValidateForAzure();
        _language = language;
        _configuration = AzureSpeechSessionFactory.CreateConfiguration(settings, language, credential);
        try
        {
            _format = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
            _input = AudioInputStream.CreatePushStream(_format);
            _audio = AudioConfig.FromStreamInput(_input);
            _recognizer = new SpeechRecognizer(_configuration, _audio);
            _connection = Connection.FromRecognizer(_recognizer);
        }
        catch
        {
            _connection?.Dispose();
            _recognizer?.Dispose();
            _audio?.Dispose();
            _input?.Dispose();
            _format?.Dispose();
            throw;
        }
        _connection.Connected += (_, _) => _connected.TrySetResult();
        _recognizer.Recognizing += (_, args) =>
        {
            if (Volatile.Read(ref _stopping) == 0 && ToSpeech(args.Result) is { } speech)
                Partial?.Invoke(speech);
        };
        _recognizer.Recognized += (_, args) =>
        {
            if (Volatile.Read(ref _stopping) == 0 && args.Result.Reason == ResultReason.RecognizedSpeech &&
                ToSpeech(args.Result) is { } speech)
                Final?.Invoke(speech);
        };
        _recognizer.Canceled += (_, args) =>
        {
            if (Volatile.Read(ref _stopping) != 0) return;
            SignalFailure(AzureSpeechDiagnostics.Cancellation(args.ErrorCode, args.ErrorDetails));
        };
        _recognizer.SessionStopped += (_, _) =>
        {
            if (Volatile.Read(ref _stopping) == 0)
                SignalFailure("Azure Speech 会话已结束。请恢复监听以重新连接。");
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
            await _recognizer.StartContinuousRecognitionAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_failure is { } failure) throw failure;
        }
        catch (Exception error) when (error is not SpeechConnectionException &&
            error is ApplicationException or InvalidOperationException or
            System.Runtime.InteropServices.COMException or System.Net.Http.HttpRequestException)
        {
            // SDK details can contain request headers or URI data; surface only a safe message.
            throw new SpeechConnectionException(AzureSpeechDiagnostics.Initialization(error));
        }
    }

    internal async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _connection.Open(forContinuousRecognition: true);
        await _connected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_failure is { } failure) throw failure;
    }

    public void Write(byte[] pcm)
    {
        Interlocked.CompareExchange(ref _audioOriginUtcTicks,
            (DateTimeOffset.UtcNow - TimeSpan.FromSeconds(pcm.Length / 32000d)).Ticks, 0);
        try { _input.Write(pcm); }
        catch (ApplicationException)
        {
            throw new SpeechConnectionException("Azure Speech 音频连接不可用。请恢复监听以重新连接。");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _stopping, 1);
        _input.Close();
        try { await _recognizer.StopContinuousRecognitionAsync().WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (ApplicationException)
        {
            throw new SpeechConnectionException("Azure Speech 停止时发生错误，连接将释放。");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _stopping, 1);
        _connection.Dispose();
        _recognizer.Dispose();
        _audio.Dispose();
        _input.Dispose();
        _format.Dispose();
    }

    private SpeechText? ToSpeech(SpeechRecognitionResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Text)) return null;
        var text = SpeechTextNormalizer.Normalize(_language == ConversationLanguage.Chinese
            ? ChineseScriptConverter.ToSimplified(result.Text) : result.Text, _language);
        if (string.IsNullOrWhiteSpace(text)) return null;
        var origin = Interlocked.Read(ref _audioOriginUtcTicks);
        var start = (origin == 0 ? DateTimeOffset.UtcNow : new DateTimeOffset(origin, TimeSpan.Zero)) +
            TimeSpan.FromTicks(checked((long)result.OffsetInTicks));
        return new SpeechText(text, start, start + result.Duration);
    }

    private void SignalFailure(string message)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0) return;
        var exception = new SpeechConnectionException(message);
        _failure = exception;
        _connected.TrySetException(exception);
        Failed?.Invoke(exception);
    }
}
