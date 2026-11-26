using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using ConversationAssistant.Core.Settings;

namespace ConversationAssistant.Core.Qwen;

public enum QwenBillingMode { PayAsYouGo, TokenPlan }

public static class QwenModels
{
    public const string DefaultSpeech = "qwen3-asr-flash-realtime";
    public const string AlternateSpeech = "qwen-audio-3.1-asr-flash-streaming";
    public const string DefaultAnalysis = "qwen-plus";
    public const string DefaultTokenPlanAnalysis = "qwen3.8-flash";
    public const string TokenPlanAsrFlash = "qwen-audio-3.0-asr-flash";
    public static IReadOnlyList<string> Speech { get; } = Array.AsReadOnly(new[] { DefaultSpeech, AlternateSpeech });
    public static IReadOnlyList<string> Analysis { get; } = Array.AsReadOnly(new[] { DefaultAnalysis, "qwen-flash", "qwen-max" });
    public static IReadOnlyList<string> TokenPlanAnalysis { get; } = Array.AsReadOnly(new[] { DefaultTokenPlanAnalysis, "qwen3.8-max" });
}

public sealed class QwenSettings
{
    private QwenStoredKey _payAsYouGoKey = new("");
    private QwenStoredKey _tokenPlanKey = new("");

    [JsonIgnore]
    public string ApiKey { get => _payAsYouGoKey.Read(QwenBillingMode.PayAsYouGo); init => _payAsYouGoKey = new(value); }
    public string SpeechModel { get; init; } = QwenModels.DefaultSpeech;
    public string AnalysisModel { get; init; } = QwenModels.DefaultAnalysis;
    public QwenBillingMode BillingMode { get; init; }
    [JsonIgnore]
    public string TokenPlanApiKey { get => _tokenPlanKey.Read(QwenBillingMode.TokenPlan); init => _tokenPlanKey = new(value); }
    public string TokenPlanAnalysisModel { get; init; } = QwenModels.DefaultTokenPlanAnalysis;
    public string? TokenPlanSpeechModel { get; init; }
    public bool SpeechEnabled => BillingMode == QwenBillingMode.PayAsYouGo || TokenPlanSpeechModel is not null;
    public bool HasApiKey => _payAsYouGoKey.Ciphertext is not null || GetKeyKind(NormalizeKey(_payAsYouGoKey.Input)) == KeyKind.PayAsYouGo;
    public bool HasTokenPlanApiKey => _tokenPlanKey.Ciphertext is not null || GetKeyKind(NormalizeKey(_tokenPlanKey.Input)) == KeyKind.TokenPlan;
    public bool HasAnalysisApiKey => BillingMode == QwenBillingMode.TokenPlan ? HasTokenPlanApiKey : HasApiKey;

    public string RequireApiKey()
    {
        var key = NormalizeKey(ApiKey);
        return GetKeyKind(key) switch
        {
            KeyKind.PayAsYouGo => key,
            KeyKind.TokenPlan => throw new InvalidOperationException(WrongPayAsYouGoKeyMessage),
            KeyKind.Empty => throw new InvalidOperationException("Save a Qwen AI Platform pay-as-you-go API key in Settings first."),
            _ => throw new InvalidOperationException(InvalidKeyMessage)
        };
    }

    internal string RequireSpeechApiKey() => BillingMode switch
    {
        QwenBillingMode.PayAsYouGo => RequireApiKey(),
        QwenBillingMode.TokenPlan when TokenPlanSpeechModel == QwenModels.TokenPlanAsrFlash => RequireTokenPlanApiKey(),
        QwenBillingMode.TokenPlan when TokenPlanSpeechModel is null =>
            throw new InvalidOperationException("Token Plan transcription is off. Select a Token Plan speech model in Settings to record audio."),
        _ => throw new InvalidOperationException("Select a supported Qwen speech model in settings.")
    };

    internal (Uri Endpoint, string Key, string Model) RequireAnalysisConfiguration() => BillingMode switch
    {
        QwenBillingMode.PayAsYouGo => (QwenPayAsYouGoEndpoints.ChatCompletions, RequireApiKey(), AnalysisModel),
        QwenBillingMode.TokenPlan => (QwenTokenPlanEndpoints.ChatCompletions, RequireTokenPlanApiKey(), TokenPlanAnalysisModel),
        _ => throw new InvalidOperationException(InvalidModeMessage)
    };

    private string RequireTokenPlanApiKey()
    {
        var key = NormalizeKey(TokenPlanApiKey);
        return GetKeyKind(key) switch
        {
            KeyKind.TokenPlan => key,
            KeyKind.Empty => throw new InvalidOperationException("Save a Qwen AI Platform Token Plan API key in Settings first."),
            _ => throw new InvalidOperationException(InvalidTokenPlanKeyMessage)
        };
    }

    public static QwenSettings FromInput(string apiKey, string speechModel, string analysisModel,
        QwenBillingMode billingMode = QwenBillingMode.PayAsYouGo, string tokenPlanApiKey = "",
        string tokenPlanAnalysisModel = QwenModels.DefaultTokenPlanAnalysis, string? tokenPlanSpeechModel = null) =>
        new QwenSettings().WithChanges(billingMode, speechModel, analysisModel, tokenPlanAnalysisModel, apiKey, tokenPlanApiKey,
            tokenPlanSpeechModel);

    public QwenSettings WithChanges(QwenBillingMode billingMode, string speechModel, string analysisModel,
        string tokenPlanAnalysisModel, string payAsYouGoReplacement, string tokenPlanReplacement, string? tokenPlanSpeechModel)
    {
        if (!Enum.IsDefined(billingMode)) throw new InvalidOperationException(InvalidModeMessage);
        if (!QwenModels.Speech.Contains(speechModel) || !QwenModels.Analysis.Contains(analysisModel) ||
            !QwenModels.TokenPlanAnalysis.Contains(tokenPlanAnalysisModel) ||
            tokenPlanSpeechModel is not (null or QwenModels.TokenPlanAsrFlash))
            throw new InvalidOperationException("Select supported Qwen models for speech and analysis.");

        return new QwenSettings
        {
            SpeechModel = speechModel, AnalysisModel = analysisModel, BillingMode = billingMode,
            TokenPlanAnalysisModel = tokenPlanAnalysisModel,
            TokenPlanSpeechModel = tokenPlanSpeechModel,
            _payAsYouGoKey = UpdateKey(_payAsYouGoKey, payAsYouGoReplacement, QwenBillingMode.PayAsYouGo),
            _tokenPlanKey = UpdateKey(_tokenPlanKey, tokenPlanReplacement, QwenBillingMode.TokenPlan)
        };
    }

    private static QwenStoredKey UpdateKey(QwenStoredKey current, string replacement, QwenBillingMode mode)
    {
        if (string.IsNullOrWhiteSpace(replacement) && current.Ciphertext is not null) return current;
        var key = NormalizeKey(string.IsNullOrWhiteSpace(replacement) ? current.Input : replacement);
        var kind = GetKeyKind(key);
        if (mode == QwenBillingMode.TokenPlan && kind is not (KeyKind.Empty or KeyKind.TokenPlan))
            throw new InvalidOperationException(InvalidTokenPlanKeyMessage);
        if (mode == QwenBillingMode.PayAsYouGo && kind == KeyKind.TokenPlan)
            throw new InvalidOperationException(WrongPayAsYouGoKeyMessage);
        if (kind == KeyKind.Invalid) throw new InvalidOperationException(InvalidKeyMessage);
        return new QwenStoredKey(key);
    }

    internal QwenSettings WithProtectedKeys(byte[]? payAsYouGo, byte[]? tokenPlan) => new()
    {
        SpeechModel = SpeechModel, AnalysisModel = AnalysisModel, BillingMode = BillingMode,
        TokenPlanAnalysisModel = TokenPlanAnalysisModel,
        TokenPlanSpeechModel = TokenPlanSpeechModel,
        _payAsYouGoKey = new QwenStoredKey("", payAsYouGo ?? _payAsYouGoKey.Protect(QwenBillingMode.PayAsYouGo)),
        _tokenPlanKey = new QwenStoredKey("", tokenPlan ?? _tokenPlanKey.Protect(QwenBillingMode.TokenPlan))
    };

    internal (byte[]? PayAsYouGo, byte[]? TokenPlan) ProtectKeys() =>
        (_payAsYouGoKey.Protect(QwenBillingMode.PayAsYouGo), _tokenPlanKey.Protect(QwenBillingMode.TokenPlan));

    private sealed class QwenStoredKey(string input, byte[]? ciphertext = null)
    {
        public string Input => input;
        public byte[]? Ciphertext => ciphertext;

        public byte[]? Protect(QwenBillingMode mode)
        {
            if (ciphertext is not null || input.Length == 0) return ciphertext;
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var bytes = Encoding.UTF8.GetBytes(input);
            try { return ProtectedData.Protect(bytes, Entropy(mode), DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        public string Read(QwenBillingMode mode)
        {
            if (ciphertext is null) return input;
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            byte[] bytes;
            try { bytes = ProtectedData.Unprotect(ciphertext, Entropy(mode), DataProtectionScope.CurrentUser); }
            catch (CryptographicException)
            {
                throw new InvalidDataException("Qwen settings could not be read. Save the API key and models again.");
            }
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        private static byte[] Entropy(QwenBillingMode mode) => Encoding.UTF8.GetBytes($"ConversationAssistant.QwenSettings.{mode}.v2");
    }

    private const string InvalidKeyMessage = "Paste the full pay-as-you-go API key from the Qwen AI Platform (starts with sk-ws- or sk-). A masked key or key ID will not work.";
    private const string WrongPayAsYouGoKeyMessage = "This is a Token Plan key. Paste it into the Token Plan API key field instead.";
    private const string InvalidTokenPlanKeyMessage = "Paste the full Token Plan API key (starts with sk-sp-). A pay-as-you-go or masked key will not work.";
    private const string InvalidModeMessage = "Select a supported Qwen analysis billing mode.";
    private enum KeyKind { Empty, PayAsYouGo, TokenPlan, Invalid }

    private static KeyKind GetKeyKind(string key)
    {
        const string legacyPrefix = "sk-";
        const string workspacePrefix = "sk-ws-";
        const string tokenPlanPrefix = "sk-sp-";
        if (key.Length == 0) return KeyKind.Empty;
        if (!key.StartsWith(legacyPrefix, StringComparison.Ordinal) || key.Length > 512) return KeyKind.Invalid;
        var isTokenPlan = key.StartsWith(tokenPlanPrefix, StringComparison.OrdinalIgnoreCase);
        var prefixLength = isTokenPlan ? tokenPlanPrefix.Length :
            key.StartsWith(workspacePrefix, StringComparison.Ordinal) ? workspacePrefix.Length : legacyPrefix.Length;
        var body = key.AsSpan(prefixLength);
        if (body.Contains("...", StringComparison.Ordinal)) return KeyKind.Invalid;
        var hasLetterOrDigit = false;
        // Accept Bearer token characters without assuming a specific key encoding.
        foreach (var character in body.TrimEnd('='))
        {
            if (char.IsAsciiLetterOrDigit(character)) hasLetterOrDigit = true;
            else if (character is not ('-' or '_' or '.' or '~' or '+' or '/')) return KeyKind.Invalid;
        }
        return !hasLetterOrDigit ? KeyKind.Invalid : isTokenPlan ? KeyKind.TokenPlan : KeyKind.PayAsYouGo;
    }

    private static string NormalizeKey(string input)
    {
        var key = Unquote(TrimClipboardEdges(input.AsSpan()));
        const string bearer = "Bearer";
        const string authorization = "Authorization:";
        if (key.IndexOfAny('\r', '\n') < 0 && key.StartsWith(authorization, StringComparison.OrdinalIgnoreCase))
        {
            var value = key[authorization.Length..].TrimStart(" \t".AsSpan());
            if (value.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)) key = value;
        }
        if (key.Length > bearer.Length && key.StartsWith(bearer, StringComparison.OrdinalIgnoreCase) &&
            (key[bearer.Length] is ' ' or '\t') && key.IndexOfAny('\r', '\n') < 0)
            key = Unquote(TrimClipboardEdges(key[bearer.Length..]));
        return key.Length == input.Length ? input : key.ToString();
    }

    private static ReadOnlySpan<char> Unquote(ReadOnlySpan<char> value) =>
        value.Length >= 2 && (value[0], value[^1]) is ('"', '"') or ('\'', '\'') or ('\u201c', '\u201d') or ('\u2018', '\u2019')
            ? TrimClipboardEdges(value[1..^1]) : value;

    private static ReadOnlySpan<char> TrimClipboardEdges(ReadOnlySpan<char> value)
    {
        static bool IsDecoration(char character) => char.IsWhiteSpace(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format;
        while (!value.IsEmpty && IsDecoration(value[0])) value = value[1..];
        while (!value.IsEmpty && IsDecoration(value[^1])) value = value[..^1];
        return value;
    }
}

internal static class QwenPayAsYouGoEndpoints
{
    private const string Host = "maas.qianwenaiapi.com";
    internal static readonly Uri ChatCompletions = new($"https://{Host}/compatible-mode/v1/chat/completions");
    internal static readonly Uri SpeechRealtime = new($"wss://{Host}/api-ws/v1/realtime?model={QwenModels.DefaultSpeech}");
    internal static readonly Uri SpeechInference = new($"wss://{Host}/api-ws/v1/inference");
}

internal static class QwenTokenPlanEndpoints
{
    internal static readonly Uri ChatCompletions = new("https://token-plan.maas.qianwenaiapi.com/compatible-mode/v1/chat/completions");
    internal static readonly Uri SpeechRecognition = new("https://token-plan.maas.qianwenaiapi.com/api/v1/services/aigc/multimodal-generation/generation");
}

public interface IQwenSettingsStore
{
    QwenSettings Load();
    void Save(QwenSettings settings);
}

public sealed class ProtectedQwenSettingsStore(string path) : IQwenSettingsStore
{
    private readonly CurrentUserProtectedFile _file = new(path, "ConversationAssistant.QwenSettings.v1");

    public QwenSettings Load()
    {
        var bytes = _file.Read();
        if (bytes is null) return new QwenSettings();
        try
        {
            var data = JsonSerializer.Deserialize<StoredSettings>(bytes) ?? throw new JsonException();
            return QwenSettings.FromInput(data.ApiKey ?? "", data.SpeechModel ?? QwenModels.DefaultSpeech,
                data.AnalysisModel ?? QwenModels.DefaultAnalysis, data.BillingMode, data.TokenPlanApiKey ?? "",
                data.TokenPlanAnalysisModel ?? QwenModels.DefaultTokenPlanAnalysis, data.TokenPlanSpeechModel)
                .WithProtectedKeys(data.ProtectedPayAsYouGoKey, data.ProtectedTokenPlanKey);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or CryptographicException)
        {
            throw new InvalidDataException("Qwen settings could not be read. Save the API key and models again.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Save(QwenSettings settings)
    {
        var validated = settings.WithChanges(settings.BillingMode, settings.SpeechModel, settings.AnalysisModel,
            settings.TokenPlanAnalysisModel, "", "", settings.TokenPlanSpeechModel);
        var (payAsYouGo, tokenPlan) = validated.ProtectKeys();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new StoredSettings(null, validated.SpeechModel,
            validated.AnalysisModel, validated.BillingMode, null, validated.TokenPlanAnalysisModel, payAsYouGo, tokenPlan,
            validated.TokenPlanSpeechModel));
        try { _file.Write(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private sealed record StoredSettings(string? ApiKey, string? SpeechModel, string? AnalysisModel,
        QwenBillingMode BillingMode, string? TokenPlanApiKey, string? TokenPlanAnalysisModel,
        byte[]? ProtectedPayAsYouGoKey, byte[]? ProtectedTokenPlanKey, string? TokenPlanSpeechModel);
}