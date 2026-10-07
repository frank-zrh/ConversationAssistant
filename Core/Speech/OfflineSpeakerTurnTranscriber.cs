using System.Runtime.CompilerServices;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Speech;

public sealed record OfflineSpeakerTurn(int StartSample, int EndSample, string? SpeakerId);

public interface IOfflineSpeakerDiarizer : IDisposable
{
    IReadOnlyList<OfflineSpeakerTurn> Analyze(float[] samples, CancellationToken cancellationToken);
}

public static class OfflineSpeakerTurnTranscriber
{
    public const int MaximumWindowSamples = SpeechWindowSegmenter.SampleRate * 15;

    public static async IAsyncEnumerable<SpeechText> TranscribeAsync(
        SpeechAudioWindow window,
        IOfflineSpeakerDiarizer diarizer,
        Func<float[], CancellationToken, Task<string>> transcribe,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(diarizer);
        ArgumentNullException.ThrowIfNull(transcribe);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAudio(window.Samples);
        if (window.Samples.Length == 0) yield break;

        var turns = diarizer.Analyze(window.Samples, cancellationToken)
            .OrderBy(turn => turn.StartSample).ThenBy(turn => turn.EndSample).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var turn in turns)
        {
            if (turn.StartSample < 0 || turn.EndSample <= turn.StartSample ||
                turn.EndSample > window.Samples.Length ||
                turn.SpeakerId is not null && string.IsNullOrWhiteSpace(turn.SpeakerId))
                throw new InvalidDataException(
                    "Offline speaker separation returned invalid turn boundaries. Pause and resume; reinstall the speaker models if this repeats.");
        }

        foreach (var turn in turns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = window.Samples[turn.StartSample..turn.EndSample];
            string text;
            try
            {
                text = await transcribe(samples, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Array.Clear(samples);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) continue;
            yield return new SpeechText(text,
                window.Start.AddSeconds((double)turn.StartSample / SpeechWindowSegmenter.SampleRate),
                window.Start.AddSeconds((double)turn.EndSample / SpeechWindowSegmenter.SampleRate),
                turn.SpeakerId);
        }
    }

    public static void ValidateAudio(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length > MaximumWindowSamples)
            throw new InvalidDataException(
                "Offline speaker separation requires short 16 kHz mono audio windows (at most 15 seconds). Pause and resume after checking the audio source.");
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample) || sample is < -1 or > 1)
                throw new InvalidDataException(
                    "Offline speaker separation requires normalized 16 kHz mono PCM audio. Select a supported microphone or loopback source.");
        }
    }
}
