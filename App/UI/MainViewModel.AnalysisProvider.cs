using ConversationAssistant.Core.Analysis;
using ConversationAssistant.Core.Copilot;
using ConversationAssistant.Core.Localization;
using ConversationAssistant_App.Authentication;
using Microsoft.UI.Xaml;

namespace ConversationAssistant_App.UI;

public sealed partial class MainViewModel
{
    private readonly CopilotEntraIdentity _copilotIdentity;
    private int _analysisProviderIndex;
    private string _copilotTenantId = "";
    private string _copilotClientId = "";
    private bool _copilotPreviewConsent;
    private UiMessage? _analysisSettingsStatus;
    public IReadOnlyList<LocalizedOption> AnalysisProviderOptions { get; } = Options("WorkIqProvider", "CopilotProvider");
    public bool CanEditAnalysisSettings => CanEditSpeechSettings;
    public string AnalysisSettingsStatus => _analysisSettingsStatus?.Resolve(Texts) ?? "";
    public Visibility CopilotSettingsVisibility => AnalysisProviderIndex == (int)AnalysisProvider.Copilot
        ? Visibility.Visible : Visibility.Collapsed;
    public int AnalysisProviderIndex
    {
        get => _analysisProviderIndex;
        set
        {
            if (value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
            if (!CanEditAnalysisSettings) { Notify(nameof(AnalysisProviderIndex)); return; }
            if (Set(ref _analysisProviderIndex, value)) Notify(nameof(CopilotSettingsVisibility));
        }
    }
    public string CopilotTenantId { get => _copilotTenantId; set => Set(ref _copilotTenantId, value); }
    public string CopilotClientId { get => _copilotClientId; set => Set(ref _copilotClientId, value); }
    public bool CopilotPreviewConsent { get => _copilotPreviewConsent; set => Set(ref _copilotPreviewConsent, value); }

    private void LoadAnalysisSettings()
    {
        _analysisProviderIndex = (int)_savedSpeech.AnalysisProvider;
        _copilotTenantId = _savedSpeech.Copilot.TenantId;
        _copilotClientId = _savedSpeech.Copilot.ClientId;
        _copilotPreviewConsent = _savedSpeech.Copilot.PreviewConsent;
        Settings.AnalysisProvider = _savedSpeech.AnalysisProvider;
    }

    public async Task<bool> SaveAnalysisSettingsAsync()
    {
        if (!CanEditAnalysisSettings)
        {
            _analysisSettingsStatus = new UiMessage("AnalysisSettingsBusy");
            Notify(nameof(AnalysisSettingsStatus));
            return false;
        }
        _savingSpeechSettings = true;
        NotifyWorkspaceControls();
        try
        {
            var provider = (AnalysisProvider)AnalysisProviderIndex;
            var configuration = new CopilotConfiguration
            {
                TenantId = CopilotTenantId.Trim(), ClientId = CopilotClientId.Trim(), PreviewConsent = CopilotPreviewConsent
            };
            if (provider == AnalysisProvider.Copilot) configuration.Validate();
            await Task.Run(() => _speechSettings.SaveAnalysis(provider, configuration));
            if (provider != _savedSpeech.AnalysisProvider || !configuration.HasSameIdentity(_savedSpeech.Copilot) ||
                configuration.PreviewConsent != _savedSpeech.Copilot.PreviewConsent)
            {
                _conversation.ResetWorkIqContext();
                _copilotIdentity.Invalidate();
            }
            _savedSpeech = _savedSpeech.WithAnalysis(provider, configuration);
            Settings.AnalysisProvider = provider;
            _conversation.UpdateWorkspacePreferences(Settings);
            _analysisSettingsStatus = new UiMessage("AnalysisSettingsSaved");
            LastError = "";
            UpdateAuthenticationStatus();
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or
            UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            LastError = error.Message;
            _analysisSettingsStatus = UiMessage.FromDiagnostic(error.Message);
            return false;
        }
        finally
        {
            _savingSpeechSettings = false;
            Notify(nameof(AnalysisSettingsStatus));
            NotifyWorkspaceControls();
        }
    }
}
