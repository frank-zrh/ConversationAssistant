using System.Security.Cryptography;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Qwen;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ConversationAssistant_App.UI;

public sealed partial class MainViewModel
{
    private readonly IQwenSettingsStore _qwenSettings;
    private readonly QwenSpeechRecognitionService _qwenSpeech;
    private readonly QwenAnalysisClient _qwenAnalysis;
    private QwenSettings _savedQwen = new();
    private string _qwenSpeechModel = QwenModels.DefaultSpeech;
    private string _qwenAnalysisModel = QwenModels.DefaultAnalysis;
    private bool _qwenSettingsBusy;
    private UiMessage? _qwenSettingsStatus;

    public IReadOnlyList<string> QwenSpeechModels => QwenModels.Speech;
    public IReadOnlyList<string> QwenAnalysisModels => QwenModels.Analysis;
    public string QwenSpeechModel
    {
        get => _qwenSpeechModel;
        set
        {
            if (QwenModels.Speech.Contains(value) && Set(ref _qwenSpeechModel, value))
                SetQwenStatus(new UiMessage("QwenUnsaved"));
        }
    }
    public string QwenAnalysisModel
    {
        get => _qwenAnalysisModel;
        set
        {
            if (QwenModels.Analysis.Contains(value) && Set(ref _qwenAnalysisModel, value))
                SetQwenStatus(new UiMessage("QwenUnsaved"));
        }
    }

    public bool QwenSettingsBusy => _qwenSettingsBusy;
    public bool CanEditQwenSettings => _conversation.Session is null && !WorkspaceBusy &&
        !QwenSettingsBusy && !_conversation.HasPendingWork;
    public bool CanTestQwen => CanEditQwenSettings && _savedQwen.HasApiKey;
    public string QwenStatus => Texts[_savedQwen.IsTokenPlanKey ? "QwenReplaceTokenPlanKey" :
        _savedQwen.HasApiKey ? "QwenConfigured" : "QwenNotConfigured"];
    public string QwenKeyHint => Texts[_savedQwen.HasApiKey ? "QwenKeyStored" : "QwenKeyPlaceholder"];
    public string QwenSettingsStatus => _qwenSettingsStatus?.Resolve(Texts) ?? "";
    public bool HasQwenSettingsStatus => _qwenSettingsStatus is not null;
    public InfoBarSeverity QwenSettingsStatusSeverity => _qwenSettingsStatus?.Key switch
    {
        "QwenSaved" or "QwenTestSuccess" => InfoBarSeverity.Success,
        string key when key.StartsWith("Error", StringComparison.Ordinal) => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational
    };
    public Visibility QwenEditHintVisibility => !CanEditQwenSettings && !QwenSettingsBusy ? Visibility.Visible : Visibility.Collapsed;

    private void LoadQwenSettings()
    {
        try
        {
            _savedQwen = _qwenSettings.Load();
            _qwenSpeechModel = _savedQwen.SpeechModel;
            _qwenAnalysisModel = _savedQwen.AnalysisModel;
            if (_savedQwen.IsTokenPlanKey) SetQwenStatus(new UiMessage("ErrorQwenTokenPlanKey"));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException)
        {
            SetQwenStatus(new UiMessage("ErrorQwenSettingsRead"));
            _logger.LogWarning("Qwen settings could not be read: {ErrorType}", error.GetType().Name);
        }
    }

    public async Task<bool> SaveQwenSettingsAsync(string replacementKey)
    {
        if (!CanEditQwenSettings)
        {
            SetQwenStatus(new UiMessage("ErrorQwenSettingsBusy"));
            return false;
        }
        var current = _savedQwen;
        var speechModel = QwenSpeechModel;
        var analysisModel = QwenAnalysisModel;
        SetQwenBusy(true);
        SetQwenStatus(new UiMessage("QwenSaving"));
        try
        {
            var result = await Task.Run(() => QwenSettingsSave.Save(_qwenSettings, current, replacementKey,
                speechModel, analysisModel, _logger));
            SetQwenStatus(result.Message);
            if (result.SavedSettings is not { } saved) return false;
            _savedQwen = saved;
            LastError = "";
            Notify(nameof(QwenStatus), nameof(QwenKeyHint));
            return true;
        }
        finally { SetQwenBusy(false); }
    }

    public async Task TestQwenAsync(bool unsavedKey, CancellationToken cancellationToken)
    {
        if (!CanTestQwen) return;
        if (unsavedKey || QwenSpeechModel != _savedQwen.SpeechModel || QwenAnalysisModel != _savedQwen.AnalysisModel)
        {
            SetQwenStatus(new UiMessage("QwenUnsaved"));
            return;
        }
        SetQwenBusy(true);
        SetQwenStatus(new UiMessage("QwenTesting"));
        var stage = "ErrorQwenSpeechTest";
        try
        {
            await _qwenSpeech.TestAsync(Settings.Language, cancellationToken);
            stage = "ErrorQwenAnalysisTest";
            await _qwenAnalysis.TestAsync(cancellationToken);
            SetQwenStatus(new UiMessage("QwenTestSuccess"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is InvalidOperationException or WorkIqException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            var message = UiMessage.FromDiagnostic(error.Message)?.Resolve(Texts);
            SetQwenStatus(new UiMessage(stage, message));
        }
        finally { SetQwenBusy(false); }
    }

    private void SetQwenStatus(UiMessage status)
    {
        if (Set(ref _qwenSettingsStatus, status, nameof(QwenSettingsStatus)))
            Notify(nameof(HasQwenSettingsStatus), nameof(QwenSettingsStatusSeverity));
    }

    private void SetQwenBusy(bool isBusy)
    {
        _qwenSettingsBusy = isBusy;
        NotifyWorkspaceControls();
    }

    private bool CanSubmitToQwen()
    {
        if (QwenSettingsBusy || WorkspaceBusy) return false;
        if (_savedQwen.HasApiKey) return true;
        LastError = Texts[_savedQwen.IsTokenPlanKey ? "ErrorQwenTokenPlanKey" : "ErrorQwenKeyRequired"];
        return false;
    }
}