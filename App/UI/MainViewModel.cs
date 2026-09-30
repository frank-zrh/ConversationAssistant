using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Settings;
using ConversationAssistant.Core.WorkIQ;
using ConversationAssistant_App.Speech;
using ConversationAssistant_App.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ConversationAssistant_App.UI;

public sealed class AnswerRow(QuestionRequest request) : INotifyPropertyChanged
{
    private AnswerSections _sections = AnswerSections.Parse(request.Answer);
    public Guid Id => request.Id;
    public Guid? TranscriptSegmentId => request.TranscriptSegmentId;
    public string Question => request.Question;
    public string Time => request.Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string Status => request.Status.ToString();
    public string Answer => request.Answer;
    public string SuggestedAnswer => _sections.SuggestedAnswer;
    public string KeyPoints => _sections.KeyPoints;
    public string Error => request.Error ?? "";
    public string Sources => string.Join(Environment.NewLine,
        new[] { _sections.Sources }.Concat(request.Sources)
            .Where(x => !string.IsNullOrWhiteSpace(x)));
    public IReadOnlyList<string> SourceReferences => request.Sources;
    public string DisplayMarkdown => AnswerPresentation.Compose(request.Answer, request.Status, request.Sources);
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        _sections = AnswerSections.Parse(request.Answer);
        foreach (var property in new[] { nameof(Status), nameof(Answer), nameof(SuggestedAnswer),
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
    private AudioDevice? _selectedDevice;
    private ConversationUiState _state = ConversationUiState.Idle;
    private string _partialText = "";
    private string _lastError = "";
    private string _modelStatus = "";
    private string _authStatus = "Work IQ: not verified";
    private string _timerText = "00:00";
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
    private string _speechSettingsStatus = "";
    public ConversationSettings Settings { get; } = new();
    public ObservableCollection<TranscriptRow> TranscriptItems { get; } = [];
    public ObservableCollection<AnswerRow> Answers { get; } = [];
    public ObservableCollection<AudioDevice> AudioDevices { get; } = [];
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
        conversation.QuestionSuppressed += () => _logger.LogInformation("Duplicate automatic question suppressed");
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
            SelectedAnswer = null;
            _followNewestAnswers = true;
            PartialText = "";
            TimerText = "00:00";
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
            ModelStatus = "无法读取 Speech 设置。请重新填写 Endpoint 和 Entra 设置后保存。";
            SpeechSettingsStatus = ex.Message;
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
                nameof(CanChangeSpeechLanguage), nameof(CanEditSpeechSettings), nameof(CanSignIn));
        }
    }

    public string StatusText => State switch
    {
        ConversationUiState.Listening => "● Listening",
        ConversationUiState.Starting => _savedSpeech.Provider == SpeechProvider.AzureSpeech
            ? "◌ Connecting to Azure Speech..."
            : "◌ Loading offline model...",
        ConversationUiState.SpeechDetected => "● Speech detected",
        ConversationUiState.QuestionDetected => "● Question or request detected",
        ConversationUiState.Thinking => "◌ Thinking with Work IQ",
        ConversationUiState.Answering => "● Answer ready",
        ConversationUiState.Paused => "● Paused",
        ConversationUiState.Offline => "● Work IQ offline · transcript continues",
        ConversationUiState.Error => "● Error",
        _ => "● Idle"
    };

    public Brush StatusBrush => new SolidColorBrush(State switch
    {
        ConversationUiState.Listening or ConversationUiState.SpeechDetected => Color.FromArgb(255, 35, 177, 106),
        ConversationUiState.QuestionDetected => Color.FromArgb(255, 70, 151, 235),
        ConversationUiState.Thinking or ConversationUiState.Answering => Color.FromArgb(255, 166, 112, 240),
        ConversationUiState.Error or ConversationUiState.Offline => Color.FromArgb(255, 238, 88, 93),
        _ => Color.FromArgb(255, 135, 144, 160)
    });
    public string ListeningText => _conversation.IsListening
        ? _savedSpeech.Provider == SpeechProvider.AzureSpeech
            ? "Microphone on · Azure online" : "Microphone on · local Whisper"
        : "Microphone off";
    public string AuthText { get => _authStatus; private set => Set(ref _authStatus, value); }
    public string SignInButtonText => _verifying ? "Signing in..." : "Microsoft 登录";
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
    public string SpeechConnectionButtonText => _testingSpeechConnection ? "正在测试连接..." : "测试已保存连接（不录音）";
    public int SpeechProviderIndex
    {
        get => _speechProviderIndex;
        set { if (Set(ref _speechProviderIndex, value)) SpeechSettingsStatus = "设置已修改；保存后生效。"; }
    }
    public string SpeechServiceUri
    {
        get => _speechServiceUri;
        set
        {
            if (Set(ref _speechServiceUri, value))
            {
                SpeechSettingsStatus = "Endpoint 已修改；请保存。";
                Notify(nameof(SpeechEndpointPreview));
            }
        }
    }
    public string SpeechEndpointPreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SpeechServiceUri))
                return "支持 Azure AI Services 多服务资源和独立 Speech 资源的根 Endpoint。";
            try { return "SDK 连接地址：" + AzureSpeechEndpoint.GetSdkUri(SpeechServiceUri).AbsoluteUri; }
            catch (InvalidOperationException) { return "请填写有效的 Azure AI Services / Speech Endpoint。"; }
        }
    }
    public bool CloudAudioConsent
    {
        get => _cloudAudioConsent;
        set { if (Set(ref _cloudAudioConsent, value)) SpeechSettingsStatus = "设置已修改；保存后生效。"; }
    }
    public string SpeechTenantId
    {
        get => _speechTenantId;
        set { if (Set(ref _speechTenantId, value)) SpeechSettingsStatus = "资源 Tenant ID 已修改；请保存并重新 Microsoft 登录，旧租户令牌不能继续使用。"; }
    }
    public string SpeechClientId
    {
        get => _speechClientId;
        set { if (Set(ref _speechClientId, value)) SpeechSettingsStatus = "应用设置已修改；请保存后重新登录。"; }
    }
    public string SpeechSettingsStatus
    {
        get => _speechSettingsStatus;
        private set => Set(ref _speechSettingsStatus, value);
    }
    public string TimerText { get => _timerText; private set => Set(ref _timerText, value); }
    public string PartialText { get => _partialText; private set => Set(ref _partialText, value); }
    public string LastError { get => _lastError; private set => Set(ref _lastError, value); }
    public string ModelStatus { get => _modelStatus; private set => Set(ref _modelStatus, value); }
    public string ScrollText => _scrollPaused ? "Resume scrolling" : "Pause scrolling";
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
    public AudioDevice? SelectedDevice
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
            var previous = SelectedDevice?.Name;
            AudioDevices.Clear();
            foreach (var device in devices) AudioDevices.Add(device);
            SelectedDevice = devices.FirstOrDefault(x => x.Name == previous) ?? devices.FirstOrDefault();
            if (devices.Count == 0) LastError = "No microphone available. Check Windows microphone privacy settings.";
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
                LastError = Settings.AutomaticQuestions && !_auth.IsSignedIn
                    ? "Auto Ask is on. Sign in with Microsoft to enable automatic Work IQ answers."
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
                nameof(CanEditSpeechSettings), nameof(CanSignIn)); });
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
        AuthText = _savedSpeech.Provider == SpeechProvider.AzureSpeech
            ? "正在完成 Azure Entra 与 Work IQ 登录；可能需要分别同意授权。"
            : "正在登录并检查 Work IQ...";
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
            SpeechSettingsStatus = "请先 End Conversation，再修改服务设置。";
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
            SpeechSettingsStatus = ex is System.Security.Cryptography.CryptographicException
                ? "Windows 无法加密配置，设置未保存。"
                : ex.Message;
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
        ModelStatus = azure
            ? "Azure AI Speech · Microsoft Entra ID（无 Key）。与 Work IQ 使用同一登录入口，令牌各自独立。"
            : "Whisper 离线转写（中文/英文）。音频在本机处理，不会发送至 Azure。";
        SpeechSettingsStatus = azure
            ? ready ? "Endpoint 已保存。请点击 Microsoft 登录获取 Entra 授权，再测试资源连接。"
                : "请先保存资源 Endpoint、资源所属目录 Tenant ID 和音频授权，再进行 Microsoft 登录。"
            : "当前使用本地 Whisper。登录按钮仅处理 Work IQ。";
    }

    private void UpdateAuthenticationStatus()
    {
        if (_verifying) return;
        var work = _auth.IsSignedIn ? "Work IQ ✓" : "Work IQ 未登录";
        AuthText = _savedSpeech.Provider == SpeechProvider.OfflineWhisper ? work + " · Speech 本地" :
            (_speechIdentity.IsSignedIn ? "Azure Entra 令牌就绪" : "Azure Entra 未登录") + " · " + work;
    }

    public async Task TestSpeechConnectionAsync()
    {
        if (!CanEditSpeechSettings)
        {
            SpeechSettingsStatus = "请先结束对话，或等待当前设置操作完成。";
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
            SpeechSettingsStatus = "请先保存当前设置，再测试连接。";
            return;
        }
        _testingSpeechConnection = true;
        SpeechSettingsStatus = "正在使用已登录的 Entra 身份和 Endpoint 测试资源连接；不会录音。";
        Notify(nameof(CanEditSpeechSettings), nameof(CanStart), nameof(SpeechConnectionButtonText), nameof(CanSignIn));
        try
        {
            await Task.Run(() => _speechConnectionTester.TestAsync(Settings.Language));
            SpeechSettingsStatus = "Azure 服务连接成功（未录音）。这不等同于实际转写效果测试。";
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or
            UnauthorizedAccessException or DllNotFoundException or BadImageFormatException)
        {
            SpeechSettingsStatus = error.Message;
        }
        finally
        {
            _testingSpeechConnection = false;
            Notify(nameof(CanEditSpeechSettings), nameof(CanStart), nameof(SpeechConnectionButtonText), nameof(CanSignIn));
        }
    }

    public bool Ask(string text)
    {
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
            LastError = "Only failed questions can be retried.";
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
            TimerText = (DateTimeOffset.Now - session.StartTime).ToString(@"hh\:mm\:ss");
    }

    private void OnTranscriptUpdate(TranscriptSegment? partial)
    {
        PartialText = partial?.Text ?? "";
        var finalized = _conversation.TranscriptSegments;
        if (TranscriptItems.Count > finalized.Count) TranscriptItems.Clear();
        for (var i = TranscriptItems.Count; i < finalized.Count; i++)
        {
            var card = new TranscriptRow(finalized[i])
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

    private AnswerRow UpsertAnswer(QuestionRequest request)
    {
        var row = Answers.FirstOrDefault(x => x.Id == request.Id);
        if (row is null)
        {
            row = new AnswerRow(request);
            Answers.Add(row);
            if (_followNewestAnswers) SelectAnswer(row);
            _logger.LogInformation("Question queued: {Manual}", request.IsManual);
        }
        else row.Refresh();
        var card = TranscriptItems.FirstOrDefault(x => x.Id == request.TranscriptSegmentId);
        card?.UpdateRequest(request);
        return row;
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }

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
