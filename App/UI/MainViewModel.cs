using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Settings;
using ConversationAssistant.Core.WorkIQ;
using ConversationAssistant_App.Speech;
using ConversationAssistant_App.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

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

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ConversationSessionManager _conversation;
    private readonly IAuthenticationService _auth;
    private readonly ILogger<MainViewModel> _logger;
    private readonly ISpeechSettingsStore _speechSettings;
    private readonly AzureSpeechConnectionTester _speechConnectionTester;
    private readonly SpeechEntraIdentity _speechIdentity;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private AnswerRow? _selectedAnswer;
    private bool _followNewestAnswers = true;
    private AudioDeviceRow? _selectedDevice;
    private ConversationUiState _state = ConversationUiState.Idle;
    private string _partialText = "";
    private UiMessage? _lastError;
    private bool _speechSettingsLoadFailed;
    private string _timerText = "00:00";
    private UiMessage _analysisStatus = new("AnalysisIdle");
    private UiMessage? _analysisError;
    private int _pendingAnalysisCount;
    private bool _analysisBusy;
    private bool _scrollPaused;
    private bool _starting;
    private bool _verifying;
    private SpeechServiceConfiguration _savedSpeech = new();
    private int _speechProviderIndex;
    private string _speechServiceUri = "";
    private string _speechTenantId = "";
    private string _speechClientId = "";
    private bool _cloudAudioConsent;
    private bool _savingSpeechSettings;
    private bool _testingSpeechConnection;
    private UiMessage? _speechSettingsStatus;
    public ConversationSettings Settings { get; } = new();
    public UiText Texts { get; private set; } = UiText.For(ConversationLanguage.Chinese);
    public IReadOnlyList<LocalizedOption> SpeechLanguageOptions { get; } = Options("ChineseSpeech", "EnglishSpeech");
    public IReadOnlyList<LocalizedOption> SpeechProviderOptions { get; } = Options("AzureProvider", "WhisperProvider");
    public IReadOnlyList<LocalizedOption> ContextOptions { get; } = Options("ShortContext", "MediumContext", "LongContext");
    public IReadOnlyList<LocalizedOption> AnswerStyleOptions { get; } = Options("Concise", "Balanced", "Detailed");
    public IReadOnlyList<LocalizedOption> AnswerLanguageOptions { get; } = Options("AutomaticLanguage", "English", "Chinese");
    public ObservableCollection<TranscriptRow> TranscriptItems { get; } = [];
    public ObservableCollection<AnswerRow> Answers { get; } = [];
    public ObservableCollection<SuggestedQuestionRow> SuggestedQuestions { get; } = [];
    public ObservableCollection<AudioDeviceRow> AudioDevices { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? TranscriptScrollRequested;

    public MainViewModel(ConversationSessionManager conversation, IAuthenticationService auth,
        ILogger<MainViewModel> logger, ISpeechSettingsStore speechSettings,
        AzureSpeechConnectionTester speechConnectionTester, SpeechEntraIdentity speechIdentity)
    {
        _conversation = conversation;
        _auth = auth;
        _logger = logger;
        _speechSettings = speechSettings;
        _speechConnectionTester = speechConnectionTester;
        _speechIdentity = speechIdentity;
        speechIdentity.StateChanged += () => OnUi(UpdateAuthenticationStatus);
        conversation.StateChanged += state => OnUi(() => State = state);
        conversation.TranscriptUpdated += segment => OnUi(() => OnTranscriptUpdate(segment));
        conversation.AnswerUpdated += request => OnUi(() => OnAnswerUpdate(request));
        conversation.AnalysisUpdated += () => OnUi(RefreshAnalysis);
        conversation.AudioDevicesChanged += () => OnUi(RefreshDevices);
        conversation.ErrorOccurred += message => OnUi(() =>
        {
            LastError = message;
            _logger.LogWarning("Conversation operation failed; state {State}", State);
        });
        conversation.ConversationEnded += () => OnUi(() =>
        {
            TranscriptItems.Clear();
            Answers.Clear();
            SuggestedQuestions.Clear();
            SelectedAnswer = null;
            _followNewestAnswers = true;
            PartialText = "";
            TimerText = "00:00";
            RefreshAnalysis();
        });
        RefreshDevices();
        try
        {
            _savedSpeech = _speechSettings.Load();
            _speechProviderIndex = (int)_savedSpeech.Provider;
            _speechServiceUri = _savedSpeech.ServiceUri;
            _speechTenantId = _savedSpeech.TenantId;
            _speechClientId = _savedSpeech.ClientId;
            _cloudAudioConsent = _savedSpeech.CloudAudioConsent;
            UpdateSpeechConfigurationStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException)
        {
            _speechSettingsLoadFailed = true;
            SetSpeechError(ex.Message);
        }
        UpdateAuthenticationStatus();
    }

    public ConversationUiState State
    {
        get => _state;
        set
        {
            _state = value;
            Notify(nameof(State), nameof(StatusText), nameof(StatusBrush), nameof(ListeningText),
                nameof(CanStart), nameof(CanEnd), nameof(CanPause), nameof(CanResume),
                nameof(CanChangeSpeechLanguage), nameof(CanEditSpeechSettings), nameof(CanSignIn),
                nameof(CanAnalyzeConversation));
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
            Texts = UiText.For(language);
            foreach (var options in new[] { SpeechLanguageOptions, SpeechProviderOptions, ContextOptions,
                AnswerStyleOptions, AnswerLanguageOptions })
                foreach (var option in options) option.UpdateLanguage(Texts);
            foreach (var row in TranscriptItems) row.UpdateLanguage(Texts);
            foreach (var row in SuggestedQuestions) row.UpdateLanguage(Texts);
            foreach (var row in Answers) row.UpdateLanguage(Texts);
            foreach (var row in AudioDevices) row.UpdateLanguage(Texts);
            Notify(nameof(Texts), nameof(SpeechLanguageIndex), nameof(StatusText), nameof(ListeningText),
                nameof(AuthText), nameof(SignInButtonText), nameof(SpeechConnectionButtonText),
                nameof(SpeechEndpointPreview), nameof(SpeechSettingsStatus), nameof(ModelStatus),
                nameof(ScrollText), nameof(LastError), nameof(AnalysisStatus), nameof(AnalysisError),
                nameof(PendingAnalysisText), nameof(AnalysisScheduleText));
        }
    }

    public string StatusText => Texts[State switch
    {
        ConversationUiState.Listening => "StatusListening",
        ConversationUiState.Starting => _savedSpeech.Provider == SpeechProvider.AzureSpeech
            ? "StatusStartingAzure" : "StatusStartingOffline",
        ConversationUiState.SpeechDetected => "StatusSpeechDetected",
        ConversationUiState.QuestionDetected => "StatusQuestionDetected",
        ConversationUiState.Thinking => "StatusThinking",
        ConversationUiState.Answering => "StatusAnswering",
        ConversationUiState.Paused => "StatusPaused",
        ConversationUiState.Offline => "StatusOffline",
        ConversationUiState.Error => "StatusError",
        _ => "StatusIdle"
    }];

    public Brush StatusBrush => new SolidColorBrush(State switch
    {
        ConversationUiState.Listening or ConversationUiState.SpeechDetected => Color.FromArgb(255, 35, 177, 106),
        ConversationUiState.QuestionDetected => Color.FromArgb(255, 70, 151, 235),
        ConversationUiState.Thinking or ConversationUiState.Answering => Color.FromArgb(255, 166, 112, 240),
        ConversationUiState.Error or ConversationUiState.Offline => Color.FromArgb(255, 238, 88, 93),
        _ => Color.FromArgb(255, 135, 144, 160)
    });
    public string ListeningText => Texts[_conversation.IsListening
        ? _savedSpeech.Provider == SpeechProvider.AzureSpeech
            ? "MicrophoneAzure" : "MicrophoneWhisper"
        : "MicrophoneOff"];
    public string AuthText
    {
        get
        {
            if (_verifying) return Texts[_savedSpeech.Provider == SpeechProvider.AzureSpeech
                ? "AuthAzureInProgress" : "AuthWorkInProgress"];
            var work = Texts[_auth.IsSignedIn ? "AuthWorkReady" : "AuthWorkNotSignedIn"];
            return _savedSpeech.Provider == SpeechProvider.OfflineWhisper
                ? work + " · " + Texts["AuthLocalSpeech"]
                : Texts[_speechIdentity.IsSignedIn ? "AuthEntraReady" : "AuthEntraNotSignedIn"] + " · " + work;
        }
    }
    public string SignInButtonText => Texts[_verifying ? "SigningIn" : "SignIn"];
    public bool CanSignIn => !_verifying && _conversation.Session is null && !_starting &&
        !_savingSpeechSettings && !_testingSpeechConnection;
    public bool CanStart => _conversation.Session is null && !_starting && !_savingSpeechSettings &&
        !_testingSpeechConnection && !_verifying;
    public bool CanEnd => _conversation.Session is not null && !_starting;
    public bool CanPause => _conversation.IsListening && !_starting;
    public bool CanResume => _conversation.Session is not null && !_conversation.IsListening;
    public bool CanChangeSpeechLanguage => !_conversation.IsListening && !_starting;
    public bool CanEditSpeechSettings => _conversation.Session is null && !_starting &&
        !_savingSpeechSettings && !_testingSpeechConnection && !_verifying;
    public string SpeechConnectionButtonText => Texts[_testingSpeechConnection
        ? "TestingSpeechConnection" : "TestSpeechConnection"];
    public int ContextWindowIndex
    {
        get => Settings.ContextWindowDuration.TotalMinutes switch { 1 => 0, 5 => 2, _ => 1 };
        set
        {
            if (value is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(value));
            Settings.ContextWindowDuration = TimeSpan.FromMinutes(value switch { 0 => 1, 2 => 5, _ => 3 });
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
            Notify(nameof(AnswerLanguageIndex));
        }
    }
    public int SpeechProviderIndex
    {
        get => _speechProviderIndex;
        set { if (Set(ref _speechProviderIndex, value)) SetSpeechStatus("SettingsDirty"); }
    }
    public string SpeechServiceUri
    {
        get => _speechServiceUri;
        set
        {
            if (Set(ref _speechServiceUri, value))
            {
                SetSpeechStatus("EndpointDirty");
                Notify(nameof(SpeechEndpointPreview));
            }
        }
    }
    public string SpeechEndpointPreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SpeechServiceUri))
                return Texts["EndpointHelp"];
            try { return Texts.Format("EndpointPreview", AzureSpeechEndpoint.GetSdkUri(SpeechServiceUri).AbsoluteUri); }
            catch (InvalidOperationException) { return Texts["EndpointInvalid"]; }
        }
    }
    public bool CloudAudioConsent
    {
        get => _cloudAudioConsent;
        set { if (Set(ref _cloudAudioConsent, value)) SetSpeechStatus("SettingsDirty"); }
    }
    public string SpeechTenantId
    {
        get => _speechTenantId;
        set { if (Set(ref _speechTenantId, value)) SetSpeechStatus("TenantDirty"); }
    }
    public string SpeechClientId
    {
        get => _speechClientId;
        set { if (Set(ref _speechClientId, value)) SetSpeechStatus("ClientDirty"); }
    }
    public string SpeechSettingsStatus => _speechSettingsStatus?.Resolve(Texts) ?? "";
    private void SetSpeechStatus(string key) =>
        Set(ref _speechSettingsStatus, new UiMessage(key), nameof(SpeechSettingsStatus));
    private void SetSpeechError(string message) =>
        Set(ref _speechSettingsStatus, UiMessage.FromDiagnostic(message), nameof(SpeechSettingsStatus));
    public string TimerText { get => _timerText; private set => Set(ref _timerText, value); }
    public string PartialText { get => _partialText; private set => Set(ref _partialText, value); }
    public string LastError
    {
        get => _lastError?.Resolve(Texts) ?? "";
        private set => Set(ref _lastError, UiMessage.FromDiagnostic(value), nameof(LastError));
    }
    public string ModelStatus => Texts[_speechSettingsLoadFailed ? "ModelSettingsReadFailed" :
        _savedSpeech.Provider == SpeechProvider.AzureSpeech ? "ModelAzure" : "ModelWhisper"];
    public string ScrollText => Texts[_scrollPaused ? "ResumeScrolling" : "PauseScrolling"];
    public string AnalysisStatus => _analysisStatus.Resolve(Texts);
    public string AnalysisError
    {
        get => _analysisError?.Resolve(Texts) ?? "";
        private set => Set(ref _analysisError, UiMessage.FromDiagnostic(value), nameof(AnalysisError));
    }
    public bool CanAnalyzeConversation => _conversation.Session is not null &&
        TranscriptItems.Count > 0 && !_analysisBusy && !_starting;
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
            if (Set(ref _selectedDevice, value)) Settings.InputDeviceId = value?.Id;
        }
    }

    public void RefreshDevices()
    {
        try
        {
            var devices = _conversation.ListDevices();
            var previous = SelectedDevice?.Device.Name;
            AudioDevices.Clear();
            foreach (var device in devices) AudioDevices.Add(new AudioDeviceRow(device, Texts));
            SelectedDevice = AudioDevices.FirstOrDefault(x => x.Device.Name == previous) ?? AudioDevices.FirstOrDefault();
            if (devices.Count == 0) LastError = Texts["ErrorNoMicrophone"];
        }
        catch (Exception ex) when (ex is InvalidOperationException or NAudio.MmException)
        {
            LastError = ex.Message;
        }
    }

    public async Task StartAsync()
    {
        _starting = true;
        State = ConversationUiState.Starting;
        Notify(nameof(CanStart));
        try
        {
            await Task.Run(() => _conversation.StartConversation(Settings));
            OnUi(() =>
            {
                LastError = Settings.AutomaticAnalysis && !_auth.IsSignedIn
                    ? Texts["ErrorAutoAnalysisSignIn"]
                    : "";
                Notify(nameof(ListeningText));
            });
            _logger.LogInformation("Conversation started; audio and local speech initialized");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NAudio.MmException or
            System.Runtime.InteropServices.COMException or UnauthorizedAccessException or
            PlatformNotSupportedException or NotSupportedException or IOException or ArgumentException or
            DllNotFoundException or BadImageFormatException)
        {
            OnUi(() => LastError = ex.Message);
            _logger.LogWarning("Conversation startup failed: {ErrorType}", ex.GetType().Name);
        }
        finally
        {
            OnUi(() => { _starting = false; Notify(nameof(CanStart), nameof(CanEnd),
                nameof(CanPause), nameof(CanResume), nameof(CanChangeSpeechLanguage),
                nameof(CanEditSpeechSettings), nameof(CanSignIn), nameof(CanAnalyzeConversation)); });
        }
    }

    public async Task EndAsync()
    {
        try
        {
            await Task.Run(_conversation.EndConversationAsync);
            _logger.LogInformation("Conversation ended and transient context cleared");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NAudio.MmException or
            System.Runtime.InteropServices.COMException)
        {
            OnUi(() => LastError = ex.Message);
        }
        finally { OnUi(() => Notify(nameof(ListeningText))); }
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

    public async Task SignInAsync()
    {
        if (!CanSignIn) return;
        _verifying = true;
        UpdateAuthenticationStatus();
        Notify(nameof(CanSignIn), nameof(SignInButtonText), nameof(CanStart), nameof(CanEditSpeechSettings));
        try
        {
            LastError = "";
            await _auth.SignInAsync();
            OnUi(() =>
            {
                LastError = "";
            });
            _logger.LogInformation("Microsoft sign-in flow completed");
        }

        catch (Exception ex) when (ex is WorkIqException or TimeoutException or InvalidOperationException or
            IOException or UnauthorizedAccessException)
        {
            OnUi(() =>
            {
                LastError = ex.Message;
            });
            _logger.LogWarning("Work IQ access verification failed: {ErrorType}", ex.GetType().Name);
        }
        finally
        {
            OnUi(() =>
            {
                _verifying = false;
                UpdateAuthenticationStatus();
                Notify(nameof(CanSignIn), nameof(SignInButtonText), nameof(CanStart), nameof(CanEditSpeechSettings));
            });
        }
    }

    public async Task<bool> SaveSpeechSettingsAsync()
    {
        if (!CanEditSpeechSettings)
        {
            SetSpeechStatus("SettingsEndFirst");
            return false;
        }
        _savingSpeechSettings = true;
        Notify(nameof(CanEditSpeechSettings), nameof(CanStart), nameof(CanSignIn));
        try
        {
            var configuration = SpeechServiceConfiguration.FromInput(
                (SpeechProvider)SpeechProviderIndex, SpeechServiceUri, SpeechTenantId, SpeechClientId, CloudAudioConsent);
            await Task.Run(() => _speechSettings.Save(configuration));
            if (configuration.Provider != _savedSpeech.Provider || !configuration.HasSameIdentity(_savedSpeech))
                _speechIdentity.Invalidate();
            _savedSpeech = configuration;
            SpeechServiceUri = configuration.ServiceUri;
            LastError = "";
            if (_conversation.Session is null) _conversation.SetState(ConversationUiState.Idle);
            UpdateSpeechConfigurationStatus();
            UpdateAuthenticationStatus();
            Notify(nameof(ListeningText), nameof(StatusText));
            _logger.LogInformation("Speech provider settings saved: {Provider}", configuration.Provider);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or
            IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            if (ex is System.Security.Cryptography.CryptographicException) SetSpeechStatus("SettingsEncryptionError");
            else SetSpeechError(ex.Message);
            return false;
        }
        finally
        {
            _savingSpeechSettings = false;
            Notify(nameof(CanEditSpeechSettings), nameof(CanStart), nameof(CanSignIn));
        }
    }

    private void UpdateSpeechConfigurationStatus()
    {
        var azure = _savedSpeech.Provider == SpeechProvider.AzureSpeech;
        var ready = _savedSpeech.ServiceUri.Length > 0 && _savedSpeech.TenantId.Length > 0;
        _speechSettingsLoadFailed = false;
        Notify(nameof(ModelStatus));
        SetSpeechStatus(azure ? ready ? "SettingsReadyAzure" : "SettingsMissingAzure" : "SettingsReadyWhisper");
    }

    private void UpdateAuthenticationStatus() => Notify(nameof(AuthText));

    public async Task TestSpeechConnectionAsync()
    {
        if (!CanEditSpeechSettings)
        {
            SetSpeechStatus("SettingsBusy");
            return;
        }
        bool sameEndpoint;
        try { sameEndpoint = AzureSpeechEndpoint.AreEquivalent(SpeechServiceUri, _savedSpeech.ServiceUri); }
        catch (InvalidOperationException) { sameEndpoint = false; }
        if ((SpeechProvider)SpeechProviderIndex != _savedSpeech.Provider ||
            !sameEndpoint ||
            CloudAudioConsent != _savedSpeech.CloudAudioConsent ||
            !string.Equals(SpeechTenantId.Trim(), _savedSpeech.TenantId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(SpeechClientId.Trim(), _savedSpeech.ClientId, StringComparison.OrdinalIgnoreCase))
        {
            SetSpeechStatus("SettingsSaveFirst");
            return;
        }
        _testingSpeechConnection = true;
        SetSpeechStatus("SettingsTesting");
        Notify(nameof(CanEditSpeechSettings), nameof(CanStart), nameof(SpeechConnectionButtonText), nameof(CanSignIn));
        try
        {
            await Task.Run(() => _speechConnectionTester.TestAsync(Settings.Language));
            SetSpeechStatus("SettingsConnectionSuccess");
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or
            UnauthorizedAccessException or DllNotFoundException or BadImageFormatException)
        {
            SetSpeechError(error.Message);
        }
        finally
        {
            _testingSpeechConnection = false;
            Notify(nameof(CanEditSpeechSettings), nameof(CanStart), nameof(SpeechConnectionButtonText), nameof(CanSignIn));
        }
    }

    public bool Ask(string text)
    {
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
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { LastError = ex.Message; return false; }
    }

    public void Retry()
    {
        if (SelectedAnswer is not null && !_conversation.Retry(SelectedAnswer.Id))
            LastError = Texts["ErrorOnlyFailed"];
    }

    public void Dismiss()
    {
        SelectAnswer(null);
        _followNewestAnswers = true;
    }

    public void OpenTranscriptCard(Guid segmentId)
    {
        try
        {
            _followNewestAnswers = false;
            var request = _conversation.AskFromTranscript(segmentId);
            SelectAnswer(UpsertAnswer(request));
        }
        catch (InvalidOperationException error) { LastError = error.Message; }
    }

    public void AnalyzeConversation()
    {
        try
        {
            _conversation.AnalyzeConversation();
            RefreshAnalysis();
        }
        catch (InvalidOperationException error) { LastError = error.Message; }
    }

    public void OpenSuggestedQuestion(Guid questionId)
    {
        try
        {
            _followNewestAnswers = false;
            var request = _conversation.AskSuggestedQuestion(questionId);
            SelectAnswer(UpsertAnswer(request));
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
        try { _conversation.ClearTranscript(); TranscriptItems.Clear(); PartialText = ""; }
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
        if (_conversation.Session is { } session)
        {
            TimerText = (DateTimeOffset.Now - session.StartTime).ToString(@"hh\:mm\:ss");
            _conversation.AnalyzeIfDue();
        }
    }

    private void OnTranscriptUpdate(TranscriptSegment? partial)
    {
        PartialText = partial?.Text ?? "";
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
        if (!_scrollPaused) TranscriptScrollRequested?.Invoke();
    }

    private void OnAnswerUpdate(QuestionRequest request)
    {
        UpsertAnswer(request);
        if (request.Status == QuestionStatus.Completed)
        {
            LastError = "";
            _logger.LogInformation("Work IQ answer completed");
        }
        else if (request.Status == QuestionStatus.Failed)
            _logger.LogWarning("Work IQ answer failed");
    }

    private void RefreshAnalysis()
    {
        var analysis = _conversation.Analysis;
        _analysisBusy = analysis?.Status is QuestionStatus.Pending or QuestionStatus.Processing;
        _pendingAnalysisCount = _conversation.PendingAnalysisCount;
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
        var questions = _conversation.SuggestedQuestions;
        var ids = questions.Select(question => question.Id).ToHashSet();
        for (var index = SuggestedQuestions.Count - 1; index >= 0; index--)
            if (!ids.Contains(SuggestedQuestions[index].Id)) SuggestedQuestions.RemoveAt(index);
        foreach (var question in questions)
        {
            var row = SuggestedQuestions.FirstOrDefault(item => item.Id == question.Id);
            if (row is null)
            {
                row = new SuggestedQuestionRow(question, Texts.Language);
                SuggestedQuestions.Add(row);
            }
            if (_conversation.GetAnswerForSuggestion(question.Id) is { } request)
                row.UpdateRequest(request);
        }
        Notify(nameof(AnalysisScheduleText), nameof(PendingAnalysisText),
            nameof(CanAnalyzeConversation), nameof(EmptySuggestionsVisibility));
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
