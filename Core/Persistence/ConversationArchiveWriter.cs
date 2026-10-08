using System.Text.Json;

namespace ConversationAssistant.Core.Persistence;

/// <summary>
/// Keeps at most one active and one pending snapshot. Queue does no file I/O.
/// A failed drain retains its newest snapshot without retrying automatically. FlushAsync continues
/// reporting that failure until an explicit Queue retries with a snapshot.
/// Event handlers run on the worker and must not block on FlushAsync or throw.
/// </summary>
public sealed class ConversationArchiveWriter
{
    private readonly IConversationArchiveStore _store;
    private readonly object _gate = new();
    private ConversationArchive? _pending;
    private Task<Exception?>? _worker;
    private Guid? _sessionId;
    private long _generation;
    private Exception? _lastFailure;

    public ConversationArchiveWriter(IConversationArchiveStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public event Action<string>? Saved;
    public event Action<Exception>? Failed;

    public void Queue(ConversationArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        lock (_gate)
        {
            if (_sessionId is { } id && id != archive.SessionId && (_worker is not null || _pending is not null))
                throw new InvalidOperationException("Flush the current conversation archive before queuing another conversation.");
            _sessionId = archive.SessionId;
            _pending = archive;
            if (_worker is null) StartWorker();
        }
    }

    public Task FlushAsync()
    {
        lock (_gate)
        {
            if (_worker is not null) return ObserveAsync(_worker);
            if (_lastFailure is not null) return Task.FromException(_lastFailure);
            return _pending is not null ? ObserveAsync(StartWorker()) : Task.CompletedTask;
        }
    }

    private Task<Exception?> StartWorker()
    {
        _lastFailure = null;
        var generation = ++_generation;
        return _worker = Task.Run(() => DrainAsync(generation));
    }

    private async Task<Exception?> DrainAsync(long generation)
    {
        ConversationArchive? active = null;
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_pending is null)
                    {
                        _sessionId = null;
                        _worker = null;
                        return null;
                    }
                    active = _pending;
                    _pending = null;
                }

                string path;
                try
                {
                    path = await _store.SaveAsync(active).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException
                    or UnauthorizedAccessException or OperationCanceledException or JsonException)
                {
                    var failure = NormalizeFailure(exception);
                    lock (_gate)
                    {
                        _pending ??= active;
                        active = null;
                        _lastFailure = failure;
                        _worker = null;
                    }
                    Failed?.Invoke(failure);
                    return failure;
                }
                active = null;
                Saved?.Invoke(path);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (generation == _generation && _worker is not null)
                {
                    _pending ??= active;
                    _worker = null;
                    if (_pending is null) _sessionId = null;
                }
            }
        }
    }

    private static async Task ObserveAsync(Task<Exception?> worker)
    {
        var failure = await worker.ConfigureAwait(false);
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Exception NormalizeFailure(Exception exception) => exception switch
    {
        IOException or InvalidDataException => exception,
        JsonException => new InvalidDataException("The conversation archive could not be serialized.", exception),
        OperationCanceledException => new IOException("The conversation archive save was cancelled. Queue a snapshot again to retry.", exception),
        _ => new IOException("The conversation archive could not be saved. Queue a snapshot again to retry.", exception)
    };
}
