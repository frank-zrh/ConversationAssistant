using System.Security.Cryptography;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Qwen;
using Microsoft.Extensions.Logging;

namespace ConversationAssistant_App.UI;

internal sealed class QwenAutoSave(TimeProvider clock)
{
    private static readonly TimeSpan Delay = TimeSpan.FromMinutes(3);
    private long? _lastEdit;

    internal void Edited() => _lastEdit = clock.GetTimestamp();
    internal void Cancel() => _lastEdit = null;

    internal bool TryBegin(bool canSave)
    {
        if (!canSave || _lastEdit is not long timestamp || clock.GetElapsedTime(timestamp) < Delay) return false;
        Cancel();
        return true;
    }
}

internal static class QwenSettingsSave
{
    internal readonly record struct Result(QwenSettings? SavedSettings, UiMessage Message);

    internal static Result Save(IQwenSettingsStore store, QwenSettings current, string replacementKey,
        string speechModel, string analysisModel, ILogger logger, QwenBillingMode billingMode = QwenBillingMode.PayAsYouGo,
        string tokenPlanReplacementKey = "", string tokenPlanAnalysisModel = QwenModels.DefaultTokenPlanAnalysis,
        string? tokenPlanSpeechModel = null)
    {
        QwenSettings requested;
        try
        {
            requested = current.WithChanges(billingMode, speechModel, analysisModel, tokenPlanAnalysisModel,
                replacementKey, tokenPlanReplacementKey, tokenPlanSpeechModel);
        }
        catch (InvalidOperationException error)
        {
            var reason = UiMessage.FromDiagnostic(error.Message)?.Key switch
            {
                "ErrorQwenKeyInvalid" => "ErrorQwenKeyInvalid",
                "ErrorQwenTokenPlanKeyInvalid" => "ErrorQwenTokenPlanKeyInvalid",
                "ErrorQwenTokenPlanKey" => "ErrorQwenTokenPlanKey",
                "ErrorQwenBillingMode" => "ErrorQwenBillingMode",
                "ErrorQwenModelInvalid" => "ErrorQwenModelInvalid",
                _ => "ErrorQwenSettingsSaveInput"
            };
            return new(null, new UiMessage(reason));
        }

        try { store.Save(requested); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            CryptographicException or NotSupportedException or System.Security.SecurityException)
        {
            var reason = error switch
            {
                UnauthorizedAccessException or System.Security.SecurityException => "ErrorQwenSettingsAccess",
                CryptographicException => "ErrorQwenSettingsEncrypt",
                NotSupportedException => "ErrorQwenSettingsLocation",
                IOException when error.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) => "ErrorQwenSettingsDiskFull",
                IOException when error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) => "ErrorQwenSettingsLocked",
                _ => "ErrorQwenSettingsWrite"
            };
            return Failure(reason, error, logger);
        }

        try
        {
            var saved = store.Load();
            if (saved.HasApiKey == requested.HasApiKey && saved.HasTokenPlanApiKey == requested.HasTokenPlanApiKey &&
                saved.SpeechModel == requested.SpeechModel && saved.AnalysisModel == requested.AnalysisModel &&
                saved.BillingMode == requested.BillingMode && saved.TokenPlanAnalysisModel == requested.TokenPlanAnalysisModel &&
                saved.TokenPlanSpeechModel == requested.TokenPlanSpeechModel &&
                (string.IsNullOrWhiteSpace(replacementKey) || saved.ApiKey == requested.ApiKey) &&
                (string.IsNullOrWhiteSpace(tokenPlanReplacementKey) || saved.TokenPlanApiKey == requested.TokenPlanApiKey))
                return new(saved, new UiMessage(saved.HasAnalysisApiKey ? "QwenSaved" : "QwenSavedWithoutKey"));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            CryptographicException or NotSupportedException or System.Security.SecurityException)
        {
            return Failure("ErrorQwenSettingsVerify", error, logger);
        }

        logger.LogWarning("Qwen settings verification failed: the saved configuration did not match the requested configuration.");
        return new(null, new UiMessage("ErrorQwenSettingsVerifyMismatch"));
    }

    private static Result Failure(string reason, Exception error, ILogger logger)
    {
        var code = $"0x{error.HResult:X8}";
        logger.LogWarning("Qwen settings operation failed: {Reason} ({ErrorType}, {Code})", reason, error.GetType().Name, code);
        return new(null, new UiMessage(reason, code));
    }
}