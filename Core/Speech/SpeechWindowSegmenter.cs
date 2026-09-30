namespace ConversationAssistant.Core.Speech;

public sealed record SpeechAudioWindow(
    float[] Samples, DateTimeOffset Start, DateTimeOffset End, bool IsFinal);

public sealed class SpeechWindowSegmenter
{
    public const int SampleRate = 16000;
    private const int PreRollSamples = SampleRate * 3 / 10;
    private const int SilenceSamples = SampleRate * 8 / 10;
    private const int PreviewSamples = SampleRate * 3;
    private const int MaximumSamples = SampleRate * 12;
    private const int MinimumVoicedSamples = SampleRate / 5;
    private const double MinimumRms = 0.006;
    private static readonly TimeSpan MaximumIdle = TimeSpan.FromSeconds(5);

    private readonly List<float> _utterance = [];
    private readonly Queue<float[]> _preRoll = new();
    private int _preRollSamples;
    private int _voicedSamples;
    private int _silentSamples;
    private int _lastPreviewSamples;
    private DateTimeOffset _start;
    private DateTimeOffset _lastSpeechEnd;

    public IReadOnlyList<SpeechAudioWindow> Add(ReadOnlySpan<float> samples, DateTimeOffset end)
    {
        if (samples.IsEmpty) return [];
        var voiced = IsSpeech(samples);
        if (_utterance.Count == 0 && !voiced)
        {
            StorePreRoll(samples);
            return [];
        }

        if (_utterance.Count == 0)
        {
            _start = end - TimeSpan.FromSeconds((double)(samples.Length + _preRollSamples) / SampleRate);
            foreach (var chunk in _preRoll) _utterance.AddRange(chunk);
            _preRoll.Clear();
            _preRollSamples = 0;
        }

        for (var i = 0; i < samples.Length; i++) _utterance.Add(samples[i]);
        if (voiced)
        {
            _voicedSamples += samples.Length;
            _silentSamples = 0;
            _lastSpeechEnd = end;
        }
        else
        {
            _silentSamples += samples.Length;
        }

        if (_silentSamples >= SilenceSamples || _utterance.Count >= MaximumSamples)
        {
            var window = _voicedSamples >= MinimumVoicedSamples
                ? new SpeechAudioWindow(_utterance.ToArray(), _start, end, true)
                : null;
            Clear();
            StorePreRoll(samples);
            return window is null ? [] : [window];
        }

        if (_utterance.Count >= PreviewSamples &&
            _utterance.Count - _lastPreviewSamples >= PreviewSamples)
        {
            _lastPreviewSamples = _utterance.Count;
            return [new SpeechAudioWindow(_utterance.ToArray(), _start, end, false)];
        }
        return [];
    }

    public SpeechAudioWindow? FinalizeIfIdle(DateTimeOffset now)
    {
        if (_utterance.Count == 0 || now - _lastSpeechEnd < MaximumIdle) return null;
        var window = _voicedSamples >= MinimumVoicedSamples
            ? new SpeechAudioWindow(_utterance.ToArray(), _start, _lastSpeechEnd, true)
            : null;
        Clear();
        return window;
    }

    public void Clear()
    {
        _utterance.Clear();
        _preRoll.Clear();
        _preRollSamples = 0;
        _voicedSamples = 0;
        _silentSamples = 0;
        _lastPreviewSamples = 0;
        _start = default;
        _lastSpeechEnd = default;
    }

    private void StorePreRoll(ReadOnlySpan<float> samples)
    {
        var chunk = samples.ToArray();
        _preRoll.Enqueue(chunk);
        _preRollSamples += chunk.Length;
        while (_preRollSamples > PreRollSamples && _preRoll.Count > 1)
            _preRollSamples -= _preRoll.Dequeue().Length;
    }

    private static bool IsSpeech(ReadOnlySpan<float> samples)
    {
        double energy = 0;
        foreach (var sample in samples) energy += sample * sample;
        return energy / samples.Length >= MinimumRms * MinimumRms;
    }
}
