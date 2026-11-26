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
    private string _qwenTokenPlanAnalysisModel = QwenModels.DefaultTokenPlanAnalysis;
    private string? _qwenTokenPlanSpeechModel;
    private QwenBillingMode _qwenBillingMode;
    private bool _qwenSettingsBusy;
    private UiMessage? _qwenSettingsStatus;
    private bool _hasUnsavedQwenSettings;

    internal event Action? QwenSettingsEdited;

    public IReadOnlyList<string> QwenSpeechModels => QwenModels.Speech;
    public IReadOnlyList<string> QwenAnalysisModels => QwenModels.Analysis;
    public IReadOnlyList<string> QwenTokenPlanAnalysisModels => QwenModels.TokenPlanAnalysis;
    public IReadOnlyList<LocalizedOption> QwenTokenPlanSpeechOptions { get; } = Options("QwenTokenPlanSpeechOff", "QwenTokenPlanSpeechFlash");
    public IReadOnlyList<LocalizedOption> QwenBillingOptions { get; } = Options("QwenPayAsYouGo", "QwenTokenPlan");
    public int QwenBillingIndex
    {
        get => (int)_qwenBillingMode;
        set
        {
            if (value is not (0 or 1) || !Set(ref _qwenBillingMode, (QwenBillingMode)value)) return;
            Notify(nameof(QwenTestLabel), nameof(QwenPayAsYouGoVisibility), nameof(QwenTokenPlanVisibility),
                nameof(QwenSpeechSettingsVisibility), nameof(QwenModeHelp));
            MarkQwenSettingsEdited();
        }
    }
    private bool EditingTokenPlan => _qwenBillingMode == QwenBillingMode.TokenPlan;
    public Visibility QwenPayAsYouGoVisibility => EditingTokenPlan ? Visibility.Collapsed : Visibility.Visible;
    public Visibility QwenTokenPlanVisibility => EditingTokenPlan ? Visibility.Visible : Visibility.Collapsed;
    public Visibility QwenSpeechSettingsVisibility => !EditingTokenPlan || _qwenTokenPlanSpeechModel is not null
        ? Visibility.Visible : Visibility.Collapsed;
    public string QwenModeHelp => Texts[EditingTokenPlan ? "QwenTokenPlanHelp" : "QwenSetupHelp"];
    public string QwenSpeechModel
    {
        get => _qwenSpeechModel;
        set
        {
            if (QwenModels.Speech.Contains(value) && Set(ref _qwenSpeechModel, value))
                MarkQwenSettingsEdited();
        }
    }
    public string QwenAnalysisModel
    {
        get => _qwenAnalysisModel;
        set
        {
            if (QwenModels.Analysis.Contains(value) && Set(ref _qwenAnalysisModel, value))
                MarkQwenSettingsEdited();
        }
    }
    public int QwenTokenPlanSpeechIndex
    {
        get => _qwenTokenPlanSpeechModel is null ? 0 : 1;
        set
        {
            if (value is not (0 or 1) || !Set(ref _qwenTokenPlanSpeechModel, value == 0 ? null : QwenModels.TokenPlanAsrFlash)) return;
            Notify(nameof(QwenSpeechSettingsVisibility));
            MarkQwenSettingsEdited();
        }
    }
    public string QwenTokenPlanAnalysisModel
    {
        get => _qwenTokenPlanAnalysisModel;
        set
        {
            if (QwenModels.TokenPlanAnalysis.Contains(value) && Set(ref _qwenTokenPlanAnalysisModel, value))
                MarkQwenSettingsEdited();
        }
    }

    public bool QwenSettingsBusy => _qwenSettingsBusy;
    public bool CanEditQwenSettings => _conversation.Session is null && !WorkspaceBusy &&
        !QwenSettingsBusy && !_conversation.HasPendingWork;
    public bool CanTestQwen => CanEditQwenSettings && _savedQwen.HasAnalysisApiKey;
    public string QwenStatus => Texts[!_savedQwen.HasAnalysisApiKey ? "QwenNotConfigured" :
        _savedQwen.BillingMode == QwenBillingMode.TokenPlan ? "QwenTokenPlanConfigured" : "QwenConfigured"];
    public string QwenKeyHint => Texts[_savedQwen.HasApiKey ? "QwenKeyStored" : "QwenKeyPlaceholder"];
    public string QwenTokenPlanKeyHint => Texts[_savedQwen.HasTokenPlanApiKey ? "QwenKeyStored" : "QwenTokenPlanKeyPlaceholder"];
    public string QwenTestLabel => Texts[EditingTokenPlan ? "QwenTestTokenPlan" : "QwenTest"];
    public string StartConversationLabel => Texts[_savedQwen.SpeechEnabled ? "StartConversation" : "QwenStartTextConversation"];
    public string QwenSettingsStatus => _qwenSettingsStatus?.Resolve(Texts) ?? "";
    public bool HasQwenSettingsStatus => _qwenSettingsStatus is not null;
    public InfoBarSeverity QwenSettingsStatusSeverity => _qwenSettingsStatus?.Key switch
    {
        "QwenSaved" or "QwenSavedWithoutKey" or "QwenAutoSaved" or "QwenAutoSavedWithoutKey" or
            "QwenTestSuccess" or "QwenTokenPlanTestSuccess" => InfoBarSeverity.Success,
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
            _qwenTokenPlanAnalysisModel = _savedQwen.TokenPlanAnalysisModel;
            _qwenTokenPlanSpeechModel = _savedQwen.TokenPlanSpeechModel;
            _qwenBillingMode = _savedQwen.BillingMode;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException)
        {
            SetQwenStatus(new UiMessage("ErrorQwenSettingsRead"));
            _logger.LogWarning("Qwen settings could not be read: {ErrorType}", error.GetType().Name);
        }
    }

    internal void MarkQwenSettingsEdited()
    {
        _hasUnsavedQwenSettings = true;
        SetQwenStatus(new UiMessage("QwenUnsaved"));
        NotifyWorkspaceControls();
        QwenSettingsEdited?.Invoke();
    }

    public async Task<bool> SaveQwenSettingsAsync(string replacementKey, string tokenPlanReplacementKey, bool automatic = false)
    {
        if (!CanEditQwenSettings)
        {
            SetQwenStatus(new UiMessage("ErrorQwenSettingsBusy"));
            return false;
        }
        var current = _savedQwen;
        var speechModel = QwenSpeechModel;
        var analysisModel = _qwenAnalysisModel;
        var billingMode = _qwenBillingMode;
        var tokenPlanAnalysisModel = _qwenTokenPlanAnalysisModel;
        var tokenPlanSpeechModel = _qwenTokenPlanSpeechModel;
        SetQwenBusy(true);
        SetQwenStatus(new UiMessage("QwenSaving"));
        try
        {
            var result = await Task.Run(() => QwenSettingsSave.Save(_qwenSettings, current, replacementKey,
                speechModel, analysisModel, _logger, billingMode, tokenPlanReplacementKey, tokenPlanAnalysisModel, tokenPlanSpeechModel));
            if (result.SavedSettings is not { } saved)
            {
                SetQwenStatus(result.Message);
                return false;
            }
            _savedQwen = saved;
            _hasUnsavedQwenSettings = false;
            SetQwenStatus(automatic
                ? new UiMessage(saved.HasAnalysisApiKey ? "QwenAutoSaved" : "QwenAutoSavedWithoutKey")
                : result.Message);
            LastError = "";
            Notify(nameof(QwenStatus), nameof(QwenKeyHint), nameof(QwenTokenPlanKeyHint), nameof(StartConversationLabel));
            return true;
        }
        finally { SetQwenBusy(false); }
    }

    public async Task TestQwenAsync(CancellationToken cancellationToken)
    {
        if (!CanTestQwen) return;
        if (_hasUnsavedQwenSettings)
        {
            SetQwenStatus(new UiMessage("QwenSaveBeforeTest"));
            return;
        }
        SetQwenBusy(true);
        var tokenPlan = _savedQwen.BillingMode == QwenBillingMode.TokenPlan;
        SetQwenStatus(new UiMessage(tokenPlan ? "QwenTokenPlanTesting" : "QwenTesting"));
        var stage = tokenPlan ? "ErrorQwenAnalysisTest" : "ErrorQwenSpeechTest";
        try
        {
            if (!tokenPlan) await _qwenSpeech.TestAsync(Settings.Language, cancellationToken);
            stage = "ErrorQwenAnalysisTest";
            await _qwenAnalysis.TestAsync(cancellationToken);
            SetQwenStatus(new UiMessage(tokenPlan ? "QwenTokenPlanTestSuccess" : "QwenTestSuccess"));
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
        if (_hasUnsavedQwenSettings)
        {
            LastError = Texts["QwenUnsaved"];
            return false;
        }
        if (_savedQwen.HasAnalysisApiKey) return true;
        LastError = Texts[_savedQwen.BillingMode == QwenBillingMode.TokenPlan ? "ErrorQwenTokenPlanKeyRequired" : "ErrorQwenKeyRequired"];
        return false;
    }
}