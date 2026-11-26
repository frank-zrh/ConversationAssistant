using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Persistence;
using ConversationAssistant.Core.Transcript;
using ConversationAssistant.Core.Qwen;
using ConversationAssistant.Core.WorkIQ;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ConversationAssistant_App.UI;

public sealed class AnswerRow(QuestionRequest request,
    ConversationLanguage language = ConversationLanguage.Chinese) : INotifyPropertyChanged
{
    private AnswerSections _sections = AnswerSections.Parse(request.Answer);
    public Guid Id => request.Id;
    public Guid? TranscriptSegmentId => request.TranscriptSegmentId;
    public Guid? SuggestedQuestionId => request.SuggestedQuestionId;
    public string Question => request.Question;
    public string Time => request.Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string Status => request.Status.ToString();
    public UiText Texts { get; private set; } = UiText.For(language);
    public string StatusText => Texts["QuestionStatus" + request.Status];
    public string Answer => request.Answer;
    public string SuggestedAnswer => _sections.SuggestedAnswer;
    public string KeyPoints => _sections.KeyPoints;
    public string Error => Texts.LocalizeDiagnostic(request.Error);
    public string Sources => string.Join(Environment.NewLine,
        new[] { _sections.Sources }.Concat(request.Sources)
            .Where(x => !string.IsNullOrWhiteSpace(x)));
    public IReadOnlyList<string> SourceReferences => request.Sources;
    public string DisplayMarkdown => AnswerPresentation.Compose(request.Answer, request.Status, request.Sources, Texts.Language);
    public event PropertyChangedEventHandler? PropertyChanged;
    public void UpdateLanguage(UiText texts)
    {
        Texts = texts;
        foreach (var property in new[] { nameof(Texts), nameof(StatusText), nameof(Error), nameof(DisplayMarkdown) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
    public void Refresh()
    {
        _sections = AnswerSections.Parse(request.Answer);
        foreach (var property in new[] { nameof(Status), nameof(StatusText), nameof(Answer), nameof(SuggestedAnswer),
            nameof(KeyPoints), nameof(Error), nameof(Sources), nameof(DisplayMarkdown) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}

public sealed partial class MainViewModel : INotifyPropertyChanged
{
    private readonly ConversationSessionManager _conversation;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private AnswerRow? _selectedAnswer;
    private bool _followNewestAnswers = true;
    private AudioDeviceRow? _selectedDevice;
    private AudioDeviceRow? _selectedOutputDevice;
    private bool _refreshingDevices;
    private ConversationUiState _state = ConversationUiState.Idle;
    private TranscriptSegment? _partialSegment;
    private UiMessage? _lastError;
    private string _timerText = "00:00";
    private UiMessage _analysisStatus = new("AnalysisIdle");
    private UiMessage? _analysisError;
    private int _pendingAnalysisCount;
    private bool _analysisBusy;
    private bool _scrollPaused;
    private bool _starting;
    public ConversationSettings Settings { get; } = new();
    public UiText Texts { get; private set; } = UiText.For(ConversationLanguage.Chinese);
    public IReadOnlyList<LocalizedOption> SpeechLanguageOptions { get; } = Options("ChineseSpeech", "EnglishSpeech");
    public IReadOnlyList<LocalizedOption> AnalysisLanguageOptions { get; } = Options("Chinese", "English");
    public IReadOnlyList<LocalizedOption> DisplayLanguageOptions { get; } = Options("Chinese", "English");
    public IReadOnlyList<LocalizedOption> ContextOptions { get; } = Options("ShortContext", "MediumContext", "LongContext");
    public IReadOnlyList<LocalizedOption> AnswerStyleOptions { get; } = Options("Concise", "Balanced", "Detailed");
    public IReadOnlyList<LocalizedOption> AnswerLanguageOptions { get; } = Options("AutomaticLanguage", "English", "Chinese");
    public IReadOnlyList<LocalizedOption> AudioModeOptions { get; } = Options("AudioMicrophone", "AudioSystem", "AudioBoth");
    public ObservableCollection<TranscriptRow> TranscriptItems { get; } = [];
    public ObservableCollection<AnswerRow> Answers { get; } = [];
    public ObservableCollection<SuggestedQuestionRow> SuggestedQuestions { get; } = [];
    public string IntentSummary => AnalysisHistory.LastOrDefault(row =>
        row.Request.Status == QuestionStatus.Completed && !string.IsNullOrWhiteSpace(row.Request.IntentSummary))
        ?.Request.IntentSummary ?? "";
    public IReadOnlyList<AnalysisRow> StageSummaries => AnalysisHistory.Where(row =>
        row.Request.Status == QuestionStatus.Completed && !string.IsNullOrWhiteSpace(row.Summary)).ToArray();
    public Visibility EmptyIntentVisibility => string.IsNullOrWhiteSpace(IntentSummary) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyStageSummariesVisibility => StageSummaries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public ObservableCollection<AudioDeviceRow> AudioDevices { get; } = [];
    public ObservableCollection<AudioDeviceRow> OutputDevices { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? TranscriptScrollRequested;

    public MainViewModel(ConversationSessionManager conversation, ILogger<MainViewModel> logger,
        IQwenSettingsStore qwenSettings, QwenSpeechRecognitionService qwenSpeech,
        QwenAnalysisClient qwenAnalysis, JsonConversationArchiveStore historyStore)
    {
        _historyStore = historyStore;
        _conversation = conversation;
        _logger = logger;
        _qwenSettings = qwenSettings;
        _qwenSpeech = qwenSpeech;
        _qwenAnalysis = qwenAnalysis;
        conversation.StateChanged += state => OnUi(() => State = state);
        conversation.TranscriptUpdated += segment => OnUi(() => OnTranscriptUpdate(segment));
        conversation.AnswerUpdated += request => OnUi(() => OnAnswerUpdate(request));
        conversation.AnalysisUpdated += () => OnUi(RefreshAnalysis);
        conversation.GroupsUpdated += () => OnUi(RefreshTranscriptLayout);
        conversation.ConversationChanged += () => OnUi(RefreshWorkspace);
        conversation.ArchiveUpdated += () => OnUi(RefreshRecordStatus);
        conversation.AudioDevicesChanged += () => OnUi(RefreshDevices);
        conversation.ErrorOccurred += message => OnUi(() =>
        {
            LastError = message;
            _logger.LogWarning("Conversation operation failed; state {State}", State);
        });
        conversation.ConversationEnded += () => OnUi(() =>
        {
            SetPartial(null);
            Tick();
            RefreshAnalysis();
            RefreshRecordStatus();
        });
        RefreshDevices();
        LoadQwenSettings();
    }

    public ConversationUiState State
    {
        get => _state;
        set
        {
            _state = value;
            Notify(nameof(State), nameof(StatusText), nameof(StatusBrush), nameof(ListeningText),
                nameof(CanStart), nameof(CanEnd), nameof(CanPause), nameof(CanResume),
                nameof(CanChangeSpeechLanguage), nameof(CanEditQwenSettings), nameof(QwenEditHintVisibility), nameof(CanTestQwen),
                nameof(CanAnalyzeConversation), nameof(CanChangeAudioSettings),
                nameof(CanChooseInputDevice), nameof(CanChooseOutputDevice),
                nameof(CanCreateGroup), nameof(CanImportConversation), nameof(CanSaveConversation), nameof(CanNewChat));
        }
    }

    public int SpeechLanguageIndex
    {
        get => Settings.Language == ConversationLanguage.Chinese ? 0 : 1;
        set
        {
            if (value is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(value));
            var language = value == 0 ? ConversationLanguage.Chinese : ConversationLanguage.English;
            if (Settings.Language == language) return;
            if (!CanChangeSpeechLanguage)
            {
                LastError = Texts["ErrorPauseLanguage"];
                Notify(nameof(SpeechLanguageIndex));
                return;
            }
            Settings.Language = language;
            _conversation.UpdateWorkspacePreferences(Settings);
            Notify(nameof(SpeechLanguageIndex));
        }
    }

    public int AnalysisLanguageIndex
    {
        get => Settings.AnalysisLanguage == ConversationLanguage.Chinese ? 0 : 1;
        set
        {
            if (value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
            Settings.AnalysisLanguage = value == 0 ? ConversationLanguage.Chinese : ConversationLanguage.English;
            _conversation.UpdateWorkspacePreferences(Settings);
            Notify(nameof(AnalysisLanguageIndex), nameof(AnswerLanguageIndex));
        }
    }

    public int DisplayLanguageIndex
    {
        get => Settings.DisplayLanguage == ConversationLanguage.Chinese ? 0 : 1;
        set
        {
            if (value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
            Settings.DisplayLanguage = value == 0 ? ConversationLanguage.Chinese : ConversationLanguage.English;
            RefreshLanguage();
            _conversation.UpdateWorkspacePreferences(Settings);
        }
    }

    private void RefreshLanguage()
    {
        var language = Settings.DisplayLanguage;
        Texts = UiText.For(language);
        foreach (var options in new[] { SpeechLanguageOptions, AnalysisLanguageOptions, DisplayLanguageOptions,
            ContextOptions, AnswerStyleOptions, AnswerLanguageOptions, AudioModeOptions, QwenBillingOptions, QwenTokenPlanSpeechOptions })
            foreach (var option in options) option.UpdateLanguage(Texts);
        foreach (var row in TranscriptItems) row.UpdateLanguage(Texts);
        foreach (var row in SuggestedQuestions) row.UpdateLanguage(Texts);
        foreach (var row in ProductGuides) row.UpdateLanguage(Texts);
        foreach (var row in Answers) row.UpdateLanguage(Texts);
        foreach (var row in _groupRows.Values) row.Refresh(Texts);
        foreach (var row in AnalysisHistory) row.Refresh(Texts);
        foreach (var row in ConversationHistory) row.UpdateLanguage(Texts);
        foreach (var row in AudioDevices) row.UpdateLanguage(Texts);
        foreach (var row in OutputDevices) row.UpdateLanguage(Texts);
        Notify(nameof(Texts), nameof(SpeechLanguageIndex), nameof(AnalysisLanguageIndex),
            nameof(DisplayLanguageIndex), nameof(StatusText), nameof(ListeningText),
            nameof(QwenStatus), nameof(QwenKeyHint), nameof(QwenTokenPlanKeyHint), nameof(QwenTestLabel),
            nameof(QwenModeHelp), nameof(StartConversationLabel), nameof(QwenSettingsStatus), nameof(PartialText),
            nameof(ScrollText), nameof(LastError), nameof(AnalysisStatus), nameof(AnalysisError),
            nameof(PendingAnalysisText), nameof(AnalysisScheduleText), nameof(RecordStatus),
            nameof(RecordPath), nameof(AutomaticAnalysis));
        NotifyAssistance();
        NotifyAnswerPanel();
        NotifyHistory();
        Notify(nameof(StageDocumentSections));
    }

    public string StatusText => Texts[State switch
    {
        ConversationUiState.Listening => "StatusListening",
        ConversationUiState.Starting => "StatusStartingQwen",
        ConversationUiState.SpeechDetected => "StatusSpeechDetected",
        ConversationUiState.QuestionDetected => "StatusQuestionDetected",
        ConversationUiState.Thinking => "StatusThinking",
        ConversationUiState.Answering => "StatusAnswering",
        ConversationUiState.Paused => "StatusPaused",
        ConversationUiState.Offline => "StatusOffline",
        ConversationUiState.Error => "StatusError",
        _ => "StatusIdle"
    }];

    public Brush StatusBrush => (Brush)Application.Current.Resources[State switch
    {
        ConversationUiState.Listening or ConversationUiState.SpeechDetected => "TeamsSuccessBrush",
        ConversationUiState.QuestionDetected or ConversationUiState.Thinking or ConversationUiState.Answering => "TeamsAccentBrush",
        ConversationUiState.Error or ConversationUiState.Offline => "TeamsDangerBrush",
        _ => "TeamsSecondaryTextBrush"
    }];

    public void RefreshTheme()
    {
        foreach (var row in TranscriptItems) row.RefreshAppearance();
        Notify(nameof(StatusBrush));
    }
    public string ListeningText
    {
        get
        {
            if (!_conversation.IsListening) return Texts["MicrophoneOff"];
            var source = Settings.AudioMode switch
            {
                AudioCaptureMode.Microphone => "Microphone",
                AudioCaptureMode.SystemAudio => "SystemAudio",
                _ => "CombinedAudio"
            };
            return Texts[source + "Qwen"];
        }
    }
    public bool CanStart => _conversation.Session is null && !WorkspaceBusy && !QwenSettingsBusy &&
        !_hasUnsavedQwenSettings && _savedQwen.HasAnalysisApiKey;
    public bool CanNewChat => !WorkspaceBusy && !_savingRecord && !QwenSettingsBusy &&
        !_hasUnsavedQwenSettings && _savedQwen.HasAnalysisApiKey;
    public bool CanEnd => _conversation.Session is not null && !WorkspaceBusy;
    public bool CanPause => _conversation.IsListening && !WorkspaceBusy;
    public bool CanResume => _savedQwen.SpeechEnabled && _conversation.Session is not null && !_conversation.IsListening && !WorkspaceBusy;
    public bool CanChangeSpeechLanguage => !_conversation.IsListening && !WorkspaceBusy;
    public bool CanChangeAudioSettings => !_conversation.IsListening && !WorkspaceBusy;
    public bool CanChooseInputDevice => CanChangeAudioSettings && Settings.AudioCapture.IncludesMicrophone;
    public bool CanChooseOutputDevice => CanChangeAudioSettings && Settings.AudioCapture.IncludesSystemAudio;
    public Visibility SystemAudioNoticeVisibility => Settings.AudioCapture.IncludesSystemAudio
        ? Visibility.Visible : Visibility.Collapsed;
    public int AudioModeIndex
    {
        get => (int)Settings.AudioMode;
        set
        {
            if (value is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(value));
            if ((int)Settings.AudioMode == value) return;
            if (!CanChangeAudioSettings)
            {
                LastError = Texts["ErrorPauseAudioSettings"];
                Notify(nameof(AudioModeIndex));
                return;
            }
            Settings.AudioMode = (AudioCaptureMode)value;
            Notify(nameof(AudioModeIndex), nameof(CanChooseInputDevice), nameof(CanChooseOutputDevice),
                nameof(SystemAudioNoticeVisibility), nameof(ListeningText));
        }
    }
    public int ContextWindowIndex
    {
        get => Settings.ContextWindowDuration.TotalMinutes switch { 1 => 0, 5 => 2, _ => 1 };
        set
        {
            if (value is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(value));
            Settings.ContextWindowDuration = TimeSpan.FromMinutes(value switch { 0 => 1, 2 => 5, _ => 3 });
            _conversation.UpdateWorkspacePreferences(Settings);
            Notify(nameof(ContextWindowIndex));
        }
    }
    public int AnswerStyleIndex
    {
        get => (int)Settings.AnswerStyle;
        set
        {
            if (value is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(value));
            Settings.AnswerStyle = (AnswerStyle)value;
            _conversation.UpdateWorkspacePreferences(Settings);
            Notify(nameof(AnswerStyleIndex));
        }
    }
    public int AnswerLanguageIndex
    {
        get => (int)Settings.AnswerLanguage;
        set
        {
            if (value is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(value));
            Settings.AnswerLanguage = (ConversationLanguage)value;
            _conversation.UpdateWorkspacePreferences(Settings);
            Notify(nameof(AnswerLanguageIndex));
        }
    }
    public string TimerText { get => _timerText; private set => Set(ref _timerText, value); }
    public string PartialText => _partialSegment is { } partial
        ? $"{TranscriptSpeaker.Label(partial.SpeakerNumber, Texts)}: {partial.Text}" : "";
    public string LastError
    {
        get => _lastError?.Resolve(Texts) ?? "";
        private set => Set(ref _lastError, UiMessage.FromDiagnostic(value), nameof(LastError));
    }
    public string ScrollText => Texts[_scrollPaused ? "ResumeScrolling" : "PauseScrolling"];
    public string AnalysisStatus => _analysisStatus.Resolve(Texts);
    public string AnalysisError
    {
        get => _analysisError?.Resolve(Texts) ?? "";
        private set => Set(ref _analysisError, UiMessage.FromDiagnostic(value), nameof(AnalysisError));
    }
    public bool CanAnalyzeConversation => _conversation.CurrentConversation is not null &&
        TranscriptItems.Count > 0 && !_analysisBusy && !WorkspaceBusy && !QwenSettingsBusy &&
        !_hasUnsavedQwenSettings && _savedQwen.HasAnalysisApiKey;
    public Visibility EmptySuggestionsVisibility => SuggestedQuestions.Count == 0
        ? Visibility.Visible : Visibility.Collapsed;
    public string PendingAnalysisText => Texts.Format("AnalysisPendingCount", _pendingAnalysisCount);
    public string AnalysisScheduleText => Texts.Format(AutomaticAnalysis
        ? "AnalysisScheduleOn" : "AnalysisScheduleOff", _pendingAnalysisCount);
    public bool AutomaticAnalysis
    {
        get => Settings.AutomaticAnalysis;
        set
        {
            if (Settings.AutomaticAnalysis == value) return;
            Settings.AutomaticAnalysis = value;
            _conversation.UpdateWorkspacePreferences(Settings);
            Notify(nameof(AutomaticAnalysis), nameof(AnalysisScheduleText));
            if (value) _conversation.AnalyzeIfDue();
        }
    }
    public AnswerRow? SelectedAnswer
    {
        get => _selectedAnswer;
        set
        {
            if (ReferenceEquals(_selectedAnswer, value)) return;
            _followNewestAnswers = false;
            SelectAnswer(value);
        }
    }
    public AudioDeviceRow? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (_refreshingDevices) return;
            if (!CanChangeAudioSettings)
            {
                LastError = Texts["ErrorPauseAudioSettings"];
                Notify(nameof(SelectedDevice));
                return;
            }
            if (Set(ref _selectedDevice, value)) Settings.InputDeviceId = value?.Id;
        }
    }
    public AudioDeviceRow? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set
        {
            if (_refreshingDevices) return;
            if (!CanChangeAudioSettings)
            {
                LastError = Texts["ErrorPauseAudioSettings"];
                Notify(nameof(SelectedOutputDevice));
                return;
            }
            if (Set(ref _selectedOutputDevice, value)) Settings.OutputDeviceId = value?.Id;
        }
    }

    public void RefreshDevices()
    {
        try
        {
            var inputs = _conversation.ListDevices(AudioDeviceKind.Input);
            var outputs = _conversation.ListDevices(AudioDeviceKind.Output);
            _refreshingDevices = true;
            try
            {
                AudioDevices.Clear();
                OutputDevices.Clear();
                foreach (var device in inputs) AudioDevices.Add(new AudioDeviceRow(device, Texts));
                foreach (var device in outputs) OutputDevices.Add(new AudioDeviceRow(device, Texts, AudioDeviceKind.Output));
                _selectedDevice = AudioDevices.FirstOrDefault(row => row.Id == (Settings.InputDeviceId ?? "default"));
                _selectedOutputDevice = OutputDevices.FirstOrDefault(row => row.Id == (Settings.OutputDeviceId ?? "default"));
                Notify(nameof(SelectedDevice), nameof(SelectedOutputDevice));
            }
            finally { _refreshingDevices = false; }
            if (inputs.Count == 0 && Settings.AudioCapture.IncludesMicrophone) LastError = Texts["ErrorAudioInputUnavailable"];
            else if (outputs.Count == 0 && Settings.AudioCapture.IncludesSystemAudio) LastError = Texts["ErrorAudioOutputUnavailable"];
        }
        catch (Exception ex) when (ex is InvalidOperationException or NAudio.MmException or
            System.Runtime.InteropServices.COMException)
        {
            LastError = ex.Message;
        }
    }

    public Task StartAsync() => StartConversationAsync(newChat: false);
    public Task<bool> NewChatAsync() => StartConversationAsync(newChat: true);

    private async Task<bool> StartConversationAsync(bool newChat)
    {
        if (newChat ? !CanNewChat : !CanStart) return false;
        _starting = true;
        var captureAudio = _savedQwen.SpeechEnabled;
        State = ConversationUiState.Starting;
        NotifyWorkspaceControls();
        try
        {
            await Task.Run(async () =>
            {
                if (newChat) await _conversation.StartNewConversationAsync(Settings, captureAudio);
                else _conversation.StartConversation(Settings, captureAudio);
            });
            OnUi(() =>
            {
                LastError = "";
                Notify(nameof(ListeningText));
            });
            _logger.LogInformation("Conversation started with audio capture: {CaptureAudio}", captureAudio);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NAudio.MmException or
            System.Runtime.InteropServices.COMException or UnauthorizedAccessException or
            PlatformNotSupportedException or NotSupportedException or IOException or InvalidDataException or ArgumentException or
            DllNotFoundException or BadImageFormatException)
        {
            OnUi(() => LastError = ex.Message);
            _logger.LogWarning("Conversation startup failed: {ErrorType}", ex.GetType().Name);
            return false;
        }
        finally
        {
            OnUi(() => { _starting = false; State = _conversation.State; NotifyWorkspaceControls(); });
        }
    }

    public async Task EndAsync()
    {
        if (_ending || _importing || _starting) return;
        _ending = true;
        NotifyWorkspaceControls();
        try
        {
            await Task.Run(_conversation.EndConversationAsync);
            _logger.LogInformation("Conversation ended; its workspace and JSON record are retained");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NAudio.MmException or
            System.Runtime.InteropServices.COMException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            OnUi(() => LastError = ex.Message);
        }
        finally { OnUi(() => { _ending = false; NotifyWorkspaceControls(); }); }
    }

    public async Task PauseAsync()
    {
        try
        {
            await Task.Run(_conversation.Pause);
            OnUi(() => Notify(nameof(ListeningText)));
        }
        catch (InvalidOperationException ex) { OnUi(() => LastError = ex.Message); }
    }

    public async Task ResumeAsync()
    {
        _starting = true;
        State = ConversationUiState.Starting;
        Notify(nameof(CanStart), nameof(CanEnd), nameof(CanPause), nameof(CanResume));
        try
        {
            await Task.Run(_conversation.Resume);
            OnUi(() => { LastError = ""; Notify(nameof(ListeningText)); });
        }
        catch (Exception ex) when (ex is InvalidOperationException or NAudio.MmException or
            IOException or UnauthorizedAccessException or DllNotFoundException or BadImageFormatException)
        { OnUi(() => LastError = ex.Message); }
        finally
        {
            OnUi(() =>
            {
                _starting = false;
                State = _conversation.State;
            });
        }
    }

    public bool Ask(string text)
    {
        if (!CanSubmitToQwen()) return false;
        if (string.IsNullOrWhiteSpace(text))
        {
            LastError = Texts["ErrorEmptyQuestion"];
            return false;
        }
        try
        {
            var request = _conversation.AskManually(text);
            _followNewestAnswers = false;
            SelectAnswer(UpsertAnswer(request));
            ShowAnswerPanel();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { LastError = ex.Message; return false; }
    }

    public void Retry()
    {
        if (!CanSubmitToQwen()) return;
        if (SelectedAnswer is not null && !_conversation.Retry(SelectedAnswer.Id))
            LastError = Texts["ErrorOnlyFailed"];
    }

    public void Dismiss()
    {
        SelectAnswer(null);
        _followNewestAnswers = true;
    }

    public void AnalyzeConversation()
    {
        if (!CanSubmitToQwen()) return;
        try
        {
            var request = _conversation.AnalyzeConversation();
            RefreshAnalysis();
            SelectedAnalysis = AnalysisHistory.First(row => row.Id == request.Id);
        }
        catch (InvalidOperationException error) { LastError = error.Message; }
    }

    public void OpenSuggestedQuestion(Guid questionId)
    {
        if (!CanSubmitToQwen()) return;
        try
        {
            _followNewestAnswers = false;
            var request = _conversation.AskSuggestedQuestion(questionId);
            SelectAnswer(UpsertAnswer(request));
            ShowAnswerPanel();
        }
        catch (InvalidOperationException error) { LastError = error.Message; }
    }

    private void SelectAnswer(AnswerRow? answer)
    {
        Set(ref _selectedAnswer, answer, nameof(SelectedAnswer));
        foreach (var card in TranscriptItems)
            card.IsSelected = card.Id == answer?.TranscriptSegmentId;
    }

    public void ShowRenderingError(string message) => LastError = message;

    public void ClearTranscript()
    {
        try
        {
            _conversation.ClearTranscript();
            TranscriptItems.Clear();
            RefreshTranscriptLayout();
            SetPartial(null);
        }
        catch (InvalidOperationException ex) { LastError = ex.Message; }
    }

    public void ToggleScroll()
    {
        _scrollPaused = !_scrollPaused;
        Notify(nameof(ScrollText));
        if (!_scrollPaused) TranscriptScrollRequested?.Invoke();
    }

    public void Tick()
    {
        if (State == ConversationUiState.Starting)
        {
            TimerText = "00:00";
            return;
        }
        if (_conversation.CurrentConversation is { } session)
        {
            TimerText = ((session.EndTime ?? DateTimeOffset.Now) - session.StartTime).ToString(@"hh\:mm\:ss");
            _conversation.AnalyzeIfDue();
        }
    }

    private void OnTranscriptUpdate(TranscriptSegment? partial)
    {
        SetPartial(partial);
        var finalized = _conversation.TranscriptSegments;
        if (TranscriptItems.Count > finalized.Count) TranscriptItems.Clear();
        for (var i = TranscriptItems.Count; i < finalized.Count; i++)
        {
            var card = new TranscriptRow(finalized[i], Texts.Language)
            {
                IsSelected = finalized[i].Id == _selectedAnswer?.TranscriptSegmentId
            };
            if (_conversation.GetAnswerForTranscript(card.Id) is { } request)
                card.UpdateRequest(request);
            TranscriptItems.Add(card);
        }
        RefreshTranscriptLayout();
        if (!_scrollPaused) TranscriptScrollRequested?.Invoke();
    }

    private void SetPartial(TranscriptSegment? partial)
    {
        _partialSegment = partial;
        Notify(nameof(PartialText));
    }

    private void OnAnswerUpdate(QuestionRequest request)
    {
        if (!_conversation.AnswerHistory.Any(item => item.Id == request.Id)) return;
        UpsertAnswer(request);
        RefreshSelectedAnalysis();
        if (request.Status == QuestionStatus.Completed)
        {
            LastError = "";
            _logger.LogInformation("Qwen answer completed");
        }
        else if (request.Status == QuestionStatus.Failed)
            _logger.LogWarning("Qwen answer failed");
        Notify(nameof(CanEditQwenSettings), nameof(QwenEditHintVisibility), nameof(CanTestQwen));
    }

    private void RefreshAnalysis()
    {
        RefreshProductGuides();
        var analyses = _conversation.Analyses;
        foreach (var request in analyses)
        {
            var row = AnalysisHistory.FirstOrDefault(item => item.Id == request.Id);
            if (row is null) AnalysisHistory.Add(new AnalysisRow(request, AnalysisHistory.Count + 1, Texts));
            else row.Refresh(Texts);
        }
        var latest = _conversation.Analysis;
        if (_latestAnalysisId != latest?.Id)
        {
            _latestAnalysisId = latest?.Id;
            _selectedAnalysis = AnalysisHistory.FirstOrDefault(item => item.Id == latest?.Id);
            Notify(nameof(SelectedAnalysis));
        }
        _analysisBusy = analyses.Any(item => item.GroupId is null &&
            item.Status is QuestionStatus.Pending or QuestionStatus.Processing);
        _pendingAnalysisCount = _conversation.PendingAnalysisCount;
        RefreshAssistance();
        RefreshSelectedAnalysis();
        Notify(nameof(AnalysisScheduleText), nameof(PendingAnalysisText),
            nameof(CanAnalyzeConversation), nameof(CanEditQwenSettings), nameof(QwenEditHintVisibility), nameof(CanTestQwen), nameof(IntentSummary), nameof(StageSummaries),
            nameof(EmptyIntentVisibility), nameof(EmptyStageSummariesVisibility), nameof(StageDocumentSections));
    }

    private void RefreshSelectedAnalysis()
    {
        var analysis = SelectedAnalysis?.Request;
        AnalysisError = analysis?.Error ?? "";
        Set(ref _analysisStatus, analysis?.Status switch
        {
            QuestionStatus.Pending => new UiMessage("AnalysisPending", analysis.Transcript.Count),
            QuestionStatus.Processing => new UiMessage("AnalysisProcessing", analysis.Transcript.Count),
            QuestionStatus.Completed => analysis.AddedQuestionCount == 0
                ? new UiMessage("AnalysisCompletedEmpty")
                : new UiMessage("AnalysisCompletedNew", analysis.AddedQuestionCount),
            QuestionStatus.Failed => new UiMessage("AnalysisFailed"),
            QuestionStatus.Cancelled => new UiMessage("AnalysisCancelled"),
            _ => new UiMessage("AnalysisIdle")
        }, nameof(AnalysisStatus));
        var questions = analysis is null ? [] : _conversation.GetSuggestedQuestionsForAnalysis(analysis.Id);
        var existingRows = SuggestedQuestions.ToDictionary(row => row.Id);
        var rows = questions.Select(question => existingRows.TryGetValue(question.Id, out var row)
            ? row : new SuggestedQuestionRow(question, Texts.Language)).ToArray();
        foreach (var row in rows)
        {
            row.RefreshRecommendation();
            if (_conversation.GetAnswerForSuggestion(row.Id) is { } request)
                row.UpdateRequest(request);
        }
        Synchronize(SuggestedQuestions, rows);
        Notify(nameof(EmptySuggestionsVisibility), nameof(SelectedAssistanceText), nameof(SelectedAssistanceVisibility),
            nameof(IntentDocumentSections));
    }

    private AnswerRow UpsertAnswer(QuestionRequest request)
    {
        var row = Answers.FirstOrDefault(x => x.Id == request.Id);
        if (row is null)
        {
            row = new AnswerRow(request, Texts.Language);
            Answers.Add(row);
            if (_followNewestAnswers) SelectAnswer(row);
            _logger.LogInformation("Question queued: {Manual}", request.IsManual);
        }
        else row.Refresh();
        var card = TranscriptItems.FirstOrDefault(x => x.Id == request.TranscriptSegmentId);
        card?.UpdateRequest(request);
        var suggestion = SuggestedQuestions.FirstOrDefault(x => x.Id == request.SuggestedQuestionId);
        suggestion?.UpdateRequest(request);
        return row;
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }

    private static IReadOnlyList<LocalizedOption> Options(params string[] keys) =>
        keys.Select(key => new LocalizedOption(key, UiText.For(ConversationLanguage.Chinese))).ToArray();

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name!);
        return true;
    }

    private void Notify(params string[] properties)
    {
        foreach (var property in properties)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
