using System.Runtime.InteropServices;
using ConversationAssistant.Core.Speech;
using SherpaOnnx;

namespace ConversationAssistant_App.Speech;

public sealed class OfflineSpeakerDiarizer : IOfflineSpeakerDiarizer
{
    private readonly OfflineSpeakerDiarization _diarization;
    private readonly SpeakerEmbeddingExtractor _embeddings;
    private readonly OfflineSpeakerIdentityMap _identities = new();
    private bool _disposed;

    public OfflineSpeakerDiarizer(OfflineSpeakerModelPaths models)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException(
                "Offline speaker separation requires the Windows x64 build. Install that build or select Azure Speech explicitly.");

        var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = models.Segmentation;
        config.Segmentation.Pyannote.WindowShiftRatio = 0.1f;
        config.Segmentation.NumThreads = threads;
        config.Embedding.Model = models.Embedding;
        config.Embedding.NumThreads = threads;
        config.Clustering.NumClusters = -1;
        config.Clustering.Threshold = OfflineSpeakerIdentityMap.ClusteringDistanceThreshold;
        config.MinDurationOn = 0;
        config.MinDurationOff = 0;
        OfflineSpeakerDiarization? diarization = null;
        SpeakerEmbeddingExtractor? embeddings = null;
        try
        {
            diarization = new OfflineSpeakerDiarization(config);
            embeddings = new SpeakerEmbeddingExtractor(config.Embedding);
            if (diarization.SampleRate != SpeechWindowSegmenter.SampleRate || embeddings.Dim <= 0)
                throw new InvalidOperationException(
                    "Offline speaker models are incompatible. Run App\\Speech\\Install-OfflineSpeakerModels.ps1 and rebuild.");
            _diarization = diarization;
            _embeddings = embeddings;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            embeddings?.Dispose();
            diarization?.Dispose();
            _identities.Dispose();
            throw new InvalidOperationException(
                "Offline speaker separation could not load its local models or native runtime. Reinstall the Windows x64 app and Microsoft Visual C++ x64 runtime, and run App\\Speech\\Install-OfflineSpeakerModels.ps1 before rebuilding.", ex);
        }
    }

    public IReadOnlyList<OfflineSpeakerTurn> Analyze(float[] samples, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        OfflineSpeakerTurnTranscriber.ValidateAudio(samples);
        if (samples.Length == 0) return [];

        // sherpa's native call is synchronous; bound each call instead of disposing a running model.
        var segments = _diarization.Process(samples);
        cancellationToken.ThrowIfCancellationRequested();
        var spans = new List<(int Start, int End, int Speaker)>();
        foreach (var segment in segments)
        {
            if (!float.IsFinite(segment.Start) || !float.IsFinite(segment.End) ||
                segment.Start < 0 || segment.End <= segment.Start)
                throw new InvalidDataException(
                    "Offline speaker separation returned invalid timing. Pause and resume; reinstall the speaker models if this repeats.");
            var start = Math.Clamp((int)Math.Round(segment.Start * SpeechWindowSegmenter.SampleRate), 0, samples.Length);
            var end = Math.Clamp((int)Math.Round(segment.End * SpeechWindowSegmenter.SampleRate), 0, samples.Length);
            if (end > start) spans.Add((start, end, segment.Speaker));
        }

        var observations = new Dictionary<int, float[]?>();
        try
        {
            foreach (var group in spans.Where(span => span.Speaker >= 0).GroupBy(span => span.Speaker))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = _embeddings.CreateStream();
                foreach (var span in group)
                {
                    var audio = samples[span.Start..span.End];
                    try { stream.AcceptWaveform(SpeechWindowSegmenter.SampleRate, audio); }
                    finally { Array.Clear(audio); }
                }
                stream.InputFinished();
                observations.Add(group.Key, _embeddings.IsReady(stream) ? _embeddings.Compute(stream) : null);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var identities = _identities.Identify(observations);
            return spans.OrderBy(span => span.Start).ThenBy(span => span.End)
                .Select(span => new OfflineSpeakerTurn(span.Start, span.End,
                    span.Speaker >= 0 ? identities[span.Speaker] : null)).ToArray();
        }
        finally
        {
            foreach (var embedding in observations.Values)
                if (embedding is not null) Array.Clear(embedding);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _identities.Dispose();
        _embeddings.Dispose();
        _diarization.Dispose();
        _disposed = true;
    }
}
