using System.Collections.ObjectModel;
using ConversationAssistant.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace ConversationAssistant_App.UI;

public sealed partial class MainViewModel
{
    private readonly JsonConversationArchiveStore _historyStore;
    private bool _historyLoading;
    private bool _historyLoadFailed;
    private int _unavailableHistoryCount;
    public ObservableCollection<ConversationHistoryRow> ConversationHistory { get; } = [];
    public bool IsHistoryLoading => _historyLoading;
    public bool CanRefreshHistory => !_historyLoading;
    public string HistoryStatus => _historyLoading ? Texts["HistoryLoading"] :
        _historyLoadFailed ? Texts["ErrorHistoryLoad"] :
        (ConversationHistory.Count == 0 ? Texts["HistoryEmpty"] : Texts.Format("HistoryCount", ConversationHistory.Count)) +
        (_unavailableHistoryCount == 0 ? "" : " " + Texts.Format("HistoryUnavailable", _unavailableHistoryCount));

    public async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (_historyLoading) return;
        _historyLoading = true;
        _historyLoadFailed = false;
        NotifyHistory();
        try
        {
            var result = await _historyStore.ListAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ConversationHistory.Clear();
            foreach (var entry in result.Entries) ConversationHistory.Add(new ConversationHistoryRow(entry, Texts));
            _unavailableHistoryCount = result.UnavailableCount;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _historyLoadFailed = true;
            _logger.LogWarning("Conversation history loading failed: {ErrorType}", error.GetType().Name);
        }
        finally
        {
            _historyLoading = false;
            NotifyHistory();
        }
    }

    private void NotifyHistory() => Notify(nameof(IsHistoryLoading), nameof(CanRefreshHistory), nameof(HistoryStatus));
}
