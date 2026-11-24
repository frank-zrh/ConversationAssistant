using System.Security.Cryptography;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Qwen;
using Microsoft.Extensions.Logging;

namespace ConversationAssistant_App.UI;

internal static class QwenSettingsSave
{
    internal readonly record struct Result(QwenSettings? SavedSettings, UiMessage Message);

    internal static Result Save(IQwenSettingsStore store, QwenSettings current, string replacementKey,
        string speechModel, string analysisModel, ILogger logger)
    {
        QwenSettings requested;
        try
        {
            requested = QwenSettings.FromInput(string.IsNullOrWhiteSpace(replacementKey) ? current.ApiKey : replacementKey,
                speechModel, analysisModel);
            requested.RequireApiKey();
        }
        catch (InvalidOperationException error)
        {
            var reason = UiMessage.FromDiagnostic(error.Message)?.Key switch
            {
                "ErrorQwenKeyRequired" => "ErrorQwenSaveKeyRequired",
                "ErrorQwenKeyInvalid" => "ErrorQwenKeyInvalid",
                "ErrorQwenTokenPlanKey" => "ErrorQwenTokenPlanKey",
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
            if (saved.HasApiKey && saved.ApiKey == requested.ApiKey &&
                saved.SpeechModel == requested.SpeechModel && saved.AnalysisModel == requested.AnalysisModel)
                return new(saved, new UiMessage("QwenSaved"));
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