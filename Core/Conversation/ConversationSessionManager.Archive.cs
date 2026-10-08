using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Persistence;

namespace ConversationAssistant.Core.Conversation;

public sealed partial class ConversationSessionManager
{
    private IConversationArchiveStore? _archiveStore;
    private ConversationArchiveWriter? _archiveWriter;
    private InvalidDataException? _snapshotFailure;
    public string? RecordPath { get; private set; }
    public DateTimeOffset? LastSavedAt { get; private set; }
    public bool HasSaveError { get; private set; }
    public event Action? ArchiveUpdated;

    private void InitializeArchive(IConversationArchiveStore? store)
    {
        _archiveStore = store;
        if (store is null) return;
        _archiveWriter = new ConversationArchiveWriter(store);
        _archiveWriter.Saved += path =>
        {
            lock (_gate)
            {
                if (!string.Equals(path, RecordPath, StringComparison.OrdinalIgnoreCase)) return;
                LastSavedAt = _timeProvider.GetUtcNow();
                HasSaveError = _snapshotFailure is not null;
            }
            ArchiveUpdated?.Invoke();
        };
        _archiveWriter.Failed += error =>
        {
            lock (_gate) HasSaveError = true;
            ArchiveUpdated?.Invoke();
            ErrorOccurred?.Invoke(error is InvalidDataException ? error.Message :
                "The conversation JSON could not be saved. Records remain in memory. Check disk space and folder access, then choose Save Now before starting or importing another conversation.");
        };
    }

    private void PersistLocked(ConversationSession session)
    {
        if (_archiveStore is null || _archiveWriter is null) return;
        var path = _archiveStore.GetPath(session.SessionId);
        if (!string.Equals(path, RecordPath, StringComparison.OrdinalIgnoreCase))
        {
            RecordPath = path;
            LastSavedAt = null;
            HasSaveError = false;
        }
        try
        {
            var snapshot = ConversationArchive.Capture(session);
            _snapshotFailure = null;
            _archiveWriter.Queue(snapshot);
        }
        catch (InvalidDataException error)
        {
            _snapshotFailure = error;
            HasSaveError = true;
            ArchiveUpdated?.Invoke();
            ErrorOccurred?.Invoke(error.Message);
        }
    }

    public async Task FlushArchiveAsync()
    {
        if (_archiveWriter is not null) await _archiveWriter.FlushAsync().ConfigureAwait(false);
        lock (_gate)
            if (_snapshotFailure is { } failure)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public async Task SaveConversationAsync()
    {
        lock (_gate) PersistLocked(RequireWorkspaceLocked());
        ArchiveUpdated?.Invoke();
        await FlushArchiveAsync().ConfigureAwait(false);
    }

    public async Task ImportConversationAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var store = _archiveStore ??
            throw new InvalidOperationException("Conversation record storage is not configured.");
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
                if (Session is not null)
                    throw new InvalidOperationException("End the current conversation before importing a record.");
            var archive = await store.LoadAsync(path).ConfigureAwait(false);
            var restored = archive.Fork().Restore();
            await CloseConversationCoreAsync().ConfigureAwait(false);
            var recordPath = await store.SaveAsync(ConversationArchive.Capture(restored)).ConfigureAwait(false);
            lock (_gate)
            {
                CurrentConversation = restored;
                RecordPath = recordPath;
                LastSavedAt = _timeProvider.GetUtcNow();
                HasSaveError = false;
                _snapshotFailure = null;
                _paused = false;
                _capturing = false;
                _workIqBusy = false;
                _analysisSchedule.Reset();
            }
            _transcript.Clear();
            ConversationChanged?.Invoke();
            GroupsUpdated?.Invoke();
            AnalysisUpdated?.Invoke();
            ArchiveUpdated?.Invoke();
            SetState(ConversationUiState.Idle);
        }
        finally { _lifecycle.Release(); }
    }
}
