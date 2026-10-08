using System.Collections.ObjectModel;
using ConversationAssistant.Core.Models;
using Microsoft.Extensions.Logging;

namespace ConversationAssistant_App.UI;

public sealed partial class MainViewModel
{
    private readonly Dictionary<Guid, TranscriptGroupRow> _groupRows = [];
    private AnalysisRow? _selectedAnalysis;
    private Guid? _latestAnalysisId;
    private bool _ending;
    private bool _importing;
    private bool _savingRecord;
    private bool _closingApp;
    private bool WorkspaceBusy => _starting || _ending || _importing || _closingApp;
    public ObservableCollection<ITranscriptListItem> TranscriptListItems { get; } = [];
    public ObservableCollection<AnalysisRow> AnalysisHistory { get; } = [];
    public bool CanCreateGroup => !WorkspaceBusy && TranscriptItems.Count >
        _conversation.Groups.Sum(group => group.TranscriptIds.Count);
    public bool CanImportConversation => !WorkspaceBusy && _conversation.Session is null;
    public bool CanSaveConversation => !WorkspaceBusy && !_savingRecord &&
        _conversation.CurrentConversation is not null;
    public string RecordPath => _conversation.RecordPath ?? "";
    public string RecordStatus => _conversation.CurrentConversation is null ? Texts["RecordNone"] :
        _conversation.HasSaveError ? Texts["RecordSaveFailed"] :
        _savingRecord || _conversation.LastSavedAt is null ? Texts["RecordSaving"] :
        Texts.Format("RecordSavedAt", _conversation.LastSavedAt.Value.ToLocalTime().ToString("HH:mm:ss"));
    public AnalysisRow? SelectedAnalysis
    {
        get => _selectedAnalysis;
        set
        {
            if (Set(ref _selectedAnalysis, value)) RefreshSelectedAnalysis();
        }
    }

    public void CreateGroup()
    {
        try { _conversation.CreateTranscriptGroup(); }
        catch (InvalidOperationException error) { LastError = error.Message; }
    }

    public bool RenameGroup(Guid groupId, string name)
    {
        LastError = "";
        try
        {
            _conversation.RenameTranscriptGroup(groupId, name);
            return true;
        }
        catch (InvalidOperationException error) { LastError = error.Message; return false; }
    }

    private void SetGroupExpanded(Guid groupId, bool expanded)
    {
        try { _conversation.SetGroupExpanded(groupId, expanded); }
        catch (InvalidOperationException error) { LastError = error.Message; }
    }

    public void AnalyzeGroup(Guid groupId)
    {
        try
        {
            var request = _conversation.AnalyzeGroup(groupId);
            RefreshAnalysis();
            SelectedAnalysis = AnalysisHistory.First(row => row.Id == request.Id);
        }
        catch (InvalidOperationException error) { LastError = error.Message; }
    }

    public async Task<bool> ImportAsync(string path)
    {
        if (!CanImportConversation)
        {
            LastError = Texts["ErrorImportEndFirst"];
            return false;
        }
        _importing = true;
        NotifyWorkspaceControls();
        try
        {
            await _conversation.ImportConversationAsync(path);
            LastError = "";
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            LastError = error.Message;
            _logger.LogWarning("Conversation import failed: {ErrorType}", error.GetType().Name);
            return false;
        }
        finally
        {
            _importing = false;
            NotifyWorkspaceControls();
        }
    }

    public async Task SaveRecordAsync()
    {
        if (!CanSaveConversation) return;
        _savingRecord = true;
        RefreshRecordStatus();
        try
        {
            await _conversation.SaveConversationAsync();
            LastError = "";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            LastError = error.Message;
            _logger.LogWarning("Conversation save failed: {ErrorType}", error.GetType().Name);
        }
        finally
        {
            _savingRecord = false;
            RefreshRecordStatus();
        }
    }

    public async Task<bool> PrepareCloseAsync()
    {
        _closingApp = true;
        NotifyWorkspaceControls();
        try
        {
            await _conversation.EndConversationAsync();
            return !_conversation.HasSaveError;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
            System.Runtime.InteropServices.COMException)
        {
            LastError = error.Message;
            _logger.LogWarning("Conversation close/save failed: {ErrorType}", error.GetType().Name);
            return false;
        }
        finally
        {
            _closingApp = false;
            NotifyWorkspaceControls();
        }
    }

    private void RefreshWorkspace()
    {
        SetPartial(null);
        TranscriptItems.Clear();
        TranscriptListItems.Clear();
        _groupRows.Clear();
        SuggestedQuestions.Clear();
        AnalysisHistory.Clear();
        Answers.Clear();
        _latestAnalysisId = null;
        _selectedAnalysis = null;
        SelectAnswer(null);
        _followNewestAnswers = true;
        if (_conversation.CurrentConversation is { } session)
        {
            Settings.Language = session.Settings.Language;
            Settings.AnswerLanguage = session.Settings.AnswerLanguage;
            Settings.AnswerStyle = session.Settings.AnswerStyle;
            Settings.ContextWindowDuration = session.Settings.ContextWindowDuration;
            Settings.MaxContextCharacters = session.Settings.MaxContextCharacters;
            Settings.AutomaticAnalysis = session.Settings.AutomaticAnalysis;
            RefreshLanguage();
            foreach (var answer in session.Answers) UpsertAnswer(answer);
        }
        OnTranscriptUpdate(null);
        RefreshAnalysis();
        Tick();
        RefreshRecordStatus();
        Notify(nameof(SelectedAnalysis), nameof(ContextWindowIndex), nameof(AnswerStyleIndex),
            nameof(AnswerLanguageIndex));
        NotifyWorkspaceControls();
    }

    private void RefreshTranscriptLayout()
    {
        var groups = _conversation.Groups;
        var cards = TranscriptItems.ToDictionary(card => card.Id);
        var membership = new Dictionary<Guid, TranscriptGroupRow>();
        var groupIds = groups.Select(group => group.Id).ToHashSet();
        foreach (var obsolete in _groupRows.Keys.Where(id => !groupIds.Contains(id)).ToArray())
            _groupRows.Remove(obsolete);
        foreach (var group in groups)
        {
            if (!_groupRows.TryGetValue(group.Id, out var row))
            {
                row = new TranscriptGroupRow(group, Texts, SetGroupExpanded);
                _groupRows.Add(group.Id, row);
            }
            row.Refresh(Texts);
            Synchronize(row.Items, group.TranscriptIds.Where(cards.ContainsKey).Select(id => cards[id]).ToArray());
            foreach (var id in group.TranscriptIds) membership.Add(id, row);
        }
        var layout = new List<ITranscriptListItem>();
        var shownGroups = new HashSet<Guid>();
        foreach (var card in TranscriptItems)
        {
            if (!membership.TryGetValue(card.Id, out var group)) layout.Add(card);
            else if (shownGroups.Add(group.Id)) layout.Add(group);
        }
        Synchronize(TranscriptListItems, layout);
        Notify(nameof(CanCreateGroup), nameof(CanSaveConversation), nameof(CanAnalyzeConversation));
    }

    private static void Synchronize<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        for (var index = 0; index < source.Count; index++)
        {
            if (index < target.Count && EqualityComparer<T>.Default.Equals(target[index], source[index])) continue;
            var existing = target.IndexOf(source[index]);
            if (existing >= 0) target.Move(existing, index);
            else target.Insert(index, source[index]);
        }
        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
    }

    private void RefreshRecordStatus() => Notify(nameof(RecordStatus), nameof(RecordPath),
        nameof(CanSaveConversation));

    private void NotifyWorkspaceControls() => Notify(nameof(CanStart), nameof(CanEnd), nameof(CanPause),
        nameof(CanResume), nameof(CanSignIn), nameof(CanEditSpeechSettings), nameof(CanChangeSpeechLanguage),
        nameof(CanChangeAudioSettings), nameof(CanChooseInputDevice), nameof(CanChooseOutputDevice),
        nameof(CanAnalyzeConversation), nameof(CanCreateGroup), nameof(CanImportConversation),
        nameof(CanSaveConversation), nameof(ListeningText), nameof(RecordStatus), nameof(RecordPath));
}
