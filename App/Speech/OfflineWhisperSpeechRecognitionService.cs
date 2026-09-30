using System.Runtime.InteropServices;
using System.Threading.Channels;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Speech;
using Microsoft.Extensions.Logging;
using Whisper.net;

namespace ConversationAssistant_App.Speech;

public sealed class OfflineWhisperSpeechRecognitionService(
    OfflineWhisperModelManager models, ILogger<OfflineWhisperSpeechRecognitionService> logger)
    : ISpeechRecognitionService, IDisposable
{
    private readonly object _gate = new();
    private WhisperFactory? _factory;
    private Channel<CapturedAudio>? _audio;
    private SpeechInferenceQueue? _windows;
    private CancellationTokenSource? _stop;
    private Task? _worker;

    public event Action<SpeechText>? PartialTranscriptReceived;
    public event Action<SpeechText>? FinalTranscriptReceived;
    public event Action? SpeechStarted;
    public event Action? SpeechEnded;
    public event Action<Exception>? RecognitionError;

    public IReadOnlyList<ConversationLanguage> AvailableLanguages() => models.AvailableLanguages();

    public void Start(ConversationLanguage language)
    {
        if (language is not (ConversationLanguage.Chinese or ConversationLanguage.English))
            throw new ArgumentOutOfRangeException(nameof(language), "Select Chinese or English for offline transcription.");

        lock (_gate)
        {
            if (_audio is not null) throw new InvalidOperationException("Speech recognition already active.");
            var model = models.Validate();
            if (_factory is null)
            {
                var factory = WhisperFactory.FromPath(model);
                try
                {
                    using var warmUp = factory.CreateBuilder()
                        .WithLanguage("zh")
                        .WithNoContext()
                        .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8))
                        .Build();
                    WarmUpAsync(warmUp).GetAwaiter().GetResult();
                    _factory = factory;
                }
                catch
                {
                    factory.Dispose();
                    throw;
                }
            }
            var builder = _factory.CreateBuilder()
                .WithLanguage(language == ConversationLanguage.Chinese ? "zh" : "en")
                .WithNoContext()
                .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8));
            var processor = builder.Build();
            var audio = Channel.CreateBounded<CapturedAudio>(new BoundedChannelOptions(600)
            {
                SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
            });
            var stop = new CancellationTokenSource();
            var windows = new SpeechInferenceQueue();
            _audio = audio;
            _windows = windows;
            _stop = stop;
            _worker = Task.Run(() => RunSessionAsync(audio.Reader, windows, processor, language, stop));
        }
        SpeechStarted?.Invoke();
    }

    public void AcceptAudio(byte[] pcm16Mono16000)
    {
        ArgumentNullException.ThrowIfNull(pcm16Mono16000);
        if (pcm16Mono16000.Length % sizeof(short) != 0)
            throw new InvalidOperationException("Microphone returned an incomplete 16-bit PCM frame.");
        Channel<CapturedAudio>? audio;
        lock (_gate) audio = _audio;
        if (audio is not null && !audio.Writer.TryWrite(new CapturedAudio(pcm16Mono16000, DateTimeOffset.Now)))
            throw new InvalidOperationException("Offline speech inference is falling behind microphone audio. Pause and resume after checking CPU load.");
    }

    public void Stop()
    {
        Channel<CapturedAudio>? audio;
        SpeechInferenceQueue? windows;
        CancellationTokenSource? stop;
        Task? worker;
        lock (_gate)
        {
            audio = _audio;
            windows = _windows;
            stop = _stop;
            worker = _worker;
            _audio = null;
            _windows = null;
            _stop = null;
            _worker = null;
        }
        if (audio is null) return;
        stop!.Cancel();
        audio.Writer.TryComplete();
        windows!.Complete();
        try { worker!.GetAwaiter().GetResult(); }
        finally
        {
            windows.Dispose();
            stop.Dispose();
            SpeechEnded?.Invoke();
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _factory?.Dispose();
            _factory = null;
        }
    }

    private static async Task WarmUpAsync(WhisperProcessor processor)
    {
        await foreach (var _ in processor.ProcessAsync(new float[SpeechWindowSegmenter.SampleRate])
            .ConfigureAwait(false)) { }
    }

    private async Task RunSessionAsync(ChannelReader<CapturedAudio> input, SpeechInferenceQueue windows,
        WhisperProcessor processor, ConversationLanguage language, CancellationTokenSource stop)
    {
        var capture = CaptureWindowsAsync(input, windows, stop);
        var inference = InferWindowsAsync(windows, processor, language, stop);
        try
        {
            await Task.WhenAll(capture, inference).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError("Offline speech recognition stopped: {ErrorType}", ex.GetType().Name);
            RecognitionError?.Invoke(new InvalidOperationException(
                "Offline speech recognition stopped. Pause and resume to restart.", ex));
        }
        finally
        {
            stop.Cancel();
            windows.Complete();
        }
    }

    private static async Task CaptureWindowsAsync(ChannelReader<CapturedAudio> input,
        SpeechInferenceQueue windows, CancellationTokenSource stop)
    {
        var segmenter = new SpeechWindowSegmenter();
        try
        {
            while (true)
            {
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                tick.CancelAfter(TimeSpan.FromMilliseconds(250));
                CapturedAudio chunk;
                try { chunk = await input.ReadAsync(tick.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    if (segmenter.FinalizeIfIdle(DateTimeOffset.Now) is { } overdue)
                        windows.Publish(overdue);
                    continue;
                }
                catch (ChannelClosedException) { break; }
                var pcm = MemoryMarshal.Cast<byte, short>(chunk.Bytes.AsSpan());
                var samples = new float[pcm.Length];
                for (var i = 0; i < pcm.Length; i++) samples[i] = pcm[i] / 32768f;
                foreach (var window in segmenter.Add(samples, chunk.End))
                    windows.Publish(window);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception)
        {
            stop.Cancel();
            throw;
        }
        finally
        {
            segmenter.Clear();
            windows.Complete();
        }
    }

    private async Task InferWindowsAsync(SpeechInferenceQueue windows,
        WhisperProcessor processor, ConversationLanguage language, CancellationTokenSource stop)
    {
        try
        {
            using (processor)
            {
                while (await windows.ReadAsync(stop.Token).ConfigureAwait(false) is { } window)
                {
                    if (window.IsFinal)
                    {
                        var text = await TranscribeWindowAsync(processor, window.Samples, language, stop.Token)
                            .ConfigureAwait(false);
                        if (!stop.IsCancellationRequested && !string.IsNullOrWhiteSpace(text))
                            FinalTranscriptReceived?.Invoke(new SpeechText(text, window.Start, window.End));
                        continue;
                    }

                    var preview = windows.BeginPreview(stop.Token);
                    try
                    {
                        var text = await TranscribeWindowAsync(processor, window.Samples, language, preview.Token)
                            .ConfigureAwait(false);
                        if (!preview.IsCancellationRequested && !string.IsNullOrWhiteSpace(text))
                            PartialTranscriptReceived?.Invoke(new SpeechText(text, window.Start, window.End));
                    }
                    catch (OperationCanceledException) when (preview.IsCancellationRequested) { }
                    finally { windows.EndPreview(preview); }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception)
        {
            stop.Cancel();
            throw;
        }
    }

    private static async Task<string> TranscribeWindowAsync(
        WhisperProcessor processor, float[] samples, ConversationLanguage language, CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(segment.Text)) parts.Add(segment.Text.Trim());
        }
        var raw = string.Join(language == ConversationLanguage.Chinese ? "" : " ", parts);
        return SpeechTextNormalizer.Normalize(language == ConversationLanguage.Chinese
            ? ChineseScriptConverter.ToSimplified(raw) : raw, language);
    }

    private sealed record CapturedAudio(byte[] Bytes, DateTimeOffset End);
}
