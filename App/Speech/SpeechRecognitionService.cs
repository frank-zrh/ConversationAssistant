using System.Speech.AudioFormat;
using System.Speech.Recognition;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Speech;

namespace ConversationAssistant_App.Speech;

public sealed class SpeechRecognitionService(SpeechModelManager models) : ISpeechRecognitionService
{
    private readonly object _gate = new();
    private SpeechRecognitionEngine? _engine;
    private PcmAudioStream? _stream;
    private ConversationLanguage _activeLanguage = ConversationLanguage.Chinese;

    public event Action<SpeechText>? PartialTranscriptReceived;
    public event Action<SpeechText>? FinalTranscriptReceived;
    public event Action? SpeechStarted;
    public event Action? SpeechEnded;
    public event Action<Exception>? RecognitionError;

    public IReadOnlyList<ConversationLanguage> AvailableLanguages() => models.AvailableLanguages();

    public void Start(ConversationLanguage language)
    {
        SpeechRecognitionEngine engine;
        PcmAudioStream stream;
        lock (_gate)
        {
            if (_engine is not null) throw new InvalidOperationException("Speech recognition already active.");
            var recognizer = models.Resolve(language);
            engine = new SpeechRecognitionEngine(recognizer);
            stream = new PcmAudioStream();
            try
            {
                engine.LoadGrammar(new DictationGrammar());
                engine.SpeechHypothesized += OnHypothesized;
                engine.SpeechRecognized += OnRecognized;
                engine.SpeechRecognitionRejected += OnRejected;
                engine.RecognizeCompleted += OnCompleted;
                // Publish input before SAPI touches the stream: microphone callbacks can now feed it.
                _engine = engine;
                _stream = stream;
                _activeLanguage = language;
            }
            catch
            {
                stream.Complete();
                engine.Dispose();
                stream.Dispose();
                throw;
            }
        }
        try
        {
            // Some recognizers read synchronously during initialization. Never hold _gate here.
            engine.SetInputToAudioStream(stream,
                new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            engine.RecognizeAsync(RecognizeMode.Multiple);
        }
        catch
        {
            Stop();
            throw;
        }
        SpeechStarted?.Invoke();
    }

    public void AcceptAudio(byte[] pcm16Mono16000)
    {
        lock (_gate)
        {
            if (_stream is not null) _stream.WriteChunk(pcm16Mono16000);
        }
    }

    public void Stop()
    {
        SpeechRecognitionEngine? engine;
        PcmAudioStream? stream;
        lock (_gate)
        {
            engine = _engine;
            stream = _stream;
            _engine = null;
            _stream = null;
        }
        if (engine is null) return;
        stream!.Complete();
        engine.SpeechHypothesized -= OnHypothesized;
        engine.SpeechRecognized -= OnRecognized;
        engine.SpeechRecognitionRejected -= OnRejected;
        engine.RecognizeCompleted -= OnCompleted;
        try { engine.RecognizeAsyncCancel(); }
        finally
        {
            engine.Dispose();
            stream.Dispose();
            SpeechEnded?.Invoke();
        }
    }

    private void OnHypothesized(object? sender, SpeechHypothesizedEventArgs e)
    {
        if (ToSpeechText(sender, e.Result) is { } speech)
            PartialTranscriptReceived?.Invoke(speech);
    }

    private void OnRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (ToSpeechText(sender, e.Result) is { } speech)
            FinalTranscriptReceived?.Invoke(speech);
    }

    private SpeechText? ToSpeechText(object? sender, RecognitionResult? result)
    {
        ConversationLanguage language;
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _engine)) return null;
            language = _activeLanguage;
        }
        if (result is null || string.IsNullOrWhiteSpace(result.Text)) return null;
        var text = SpeechTextNormalizer.Normalize(result.Text, language);
        return string.IsNullOrWhiteSpace(text) ? null :
            SpeechTiming.Create(text, result.Audio?.Duration, DateTimeOffset.Now);
    }

    private void OnRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
    {
        // Rejected low-confidence audio is not committed as a final transcript.
    }

    private void OnCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        if (e.Error is not null) RecognitionError?.Invoke(e.Error);
    }
}
