namespace ConversationAssistant.Core.Speech;

public sealed class SpeechInferenceQueue : IDisposable
{
    private const int MaximumPendingFinals = 16;
    private readonly object _gate = new();
    private readonly Queue<SpeechAudioWindow> _finals = new();
    private readonly SemaphoreSlim _signal = new(0);
    private SpeechAudioWindow? _preview;
    private CancellationTokenSource? _activePreview;
    private bool _completed;

    public void Publish(SpeechAudioWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        lock (_gate)
        {
            if (_completed) throw new InvalidOperationException("Speech inference queue has stopped.");
            if (window.IsFinal)
            {
                if (_finals.Count >= MaximumPendingFinals)
                    throw new InvalidOperationException(
                        "Local speech inference cannot keep up with the conversation. Pause and resume after checking CPU load.");
                _finals.Enqueue(window);
                _preview = null;
                _activePreview?.Cancel();
            }
            else
            {
                if (_finals.Count > 0) return;
                _preview = window;
            }
            _signal.Release();
        }
    }

    public async ValueTask<SpeechAudioWindow?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_finals.TryDequeue(out var final)) return final;
                if (_preview is { } preview)
                {
                    _preview = null;
                    return preview;
                }
                if (_completed) return null;
            }
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public CancellationTokenSource BeginPreview(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _activePreview = source;
            if (_finals.Count > 0) source.Cancel();
        }
        return source;
    }

    public void EndPreview(CancellationTokenSource source)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activePreview, source)) _activePreview = null;
        }
        source.Dispose();
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            _signal.Release();
        }
    }

    public void Dispose()
    {
        Complete();
        _signal.Dispose();
    }
}
