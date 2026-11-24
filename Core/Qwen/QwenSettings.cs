using System.Security.Cryptography;
using System.Text.Json;
using System.Globalization;
using ConversationAssistant.Core.Settings;

namespace ConversationAssistant.Core.Qwen;

public static class QwenModels
{
    public const string DefaultSpeech = "qwen3-asr-flash-realtime";
    public const string AlternateSpeech = "qwen-audio-3.1-asr-flash-streaming";
    public const string DefaultAnalysis = "qwen-plus";
    public static IReadOnlyList<string> Speech { get; } = Array.AsReadOnly(new[] { DefaultSpeech, AlternateSpeech });
    public static IReadOnlyList<string> Analysis { get; } = Array.AsReadOnly(new[] { DefaultAnalysis, "qwen-flash", "qwen-max" });
}

public sealed class QwenSettings
{
    public string ApiKey { get; init; } = "";
    public string SpeechModel { get; init; } = QwenModels.DefaultSpeech;
    public string AnalysisModel { get; init; } = QwenModels.DefaultAnalysis;
    public bool HasApiKey => GetKeyKind(NormalizeKey(ApiKey)) == KeyKind.PayAsYouGo;
    public bool IsTokenPlanKey => GetKeyKind(NormalizeKey(ApiKey)) == KeyKind.TokenPlan;

    public string RequireApiKey()
    {
        var key = NormalizeKey(ApiKey);
        return GetKeyKind(key) switch
        {
            KeyKind.PayAsYouGo => key,
            KeyKind.TokenPlan => throw new InvalidOperationException("Token Plan keys are not supported in this app. Use a Qwen AI Platform pay-as-you-go API key."),
            KeyKind.Empty => throw new InvalidOperationException("Save a Qwen AI Platform pay-as-you-go API key in Settings first."),
            _ => throw new InvalidOperationException(InvalidKeyMessage)
        };
    }

    public static QwenSettings FromInput(string apiKey, string speechModel, string analysisModel)
    {
        apiKey = NormalizeKey(apiKey);
        if (GetKeyKind(apiKey) == KeyKind.Invalid) throw new InvalidOperationException(InvalidKeyMessage);
        if (!QwenModels.Speech.Contains(speechModel) || !QwenModels.Analysis.Contains(analysisModel))
            throw new InvalidOperationException("Select supported Qwen models for speech and analysis.");
        return new QwenSettings { ApiKey = apiKey, SpeechModel = speechModel, AnalysisModel = analysisModel };
    }

    private const string InvalidKeyMessage = "Paste the full pay-as-you-go API key from the Qwen AI Platform (starts with sk-ws- or sk-). A masked key or key ID will not work.";
    private enum KeyKind { Empty, PayAsYouGo, TokenPlan, Invalid }

    private static KeyKind GetKeyKind(string key)
    {
        const string legacyPrefix = "sk-";
        const string workspacePrefix = "sk-ws-";
        if (key.Length == 0) return KeyKind.Empty;
        if (key.StartsWith("sk-sp-", StringComparison.OrdinalIgnoreCase)) return KeyKind.TokenPlan;
        if (!key.StartsWith(legacyPrefix, StringComparison.Ordinal) || key.Length > 512) return KeyKind.Invalid;
        var prefixLength = key.StartsWith(workspacePrefix, StringComparison.Ordinal) ? workspacePrefix.Length : legacyPrefix.Length;
        var body = key.AsSpan(prefixLength);
        if (body.Contains("...", StringComparison.Ordinal)) return KeyKind.Invalid;
        var hasLetterOrDigit = false;
        // Accept Bearer token characters without assuming a specific key encoding.
        foreach (var character in body.TrimEnd('='))
        {
            if (char.IsAsciiLetterOrDigit(character)) hasLetterOrDigit = true;
            else if (character is not ('-' or '_' or '.' or '~' or '+' or '/')) return KeyKind.Invalid;
        }
        return hasLetterOrDigit ? KeyKind.PayAsYouGo : KeyKind.Invalid;
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
                data.AnalysisModel ?? QwenModels.DefaultAnalysis);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("Qwen settings could not be read. Save the API key and models again.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Save(QwenSettings settings)
    {
        var validated = QwenSettings.FromInput(settings.ApiKey, settings.SpeechModel, settings.AnalysisModel);
        validated.RequireApiKey();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new StoredSettings(validated.ApiKey, validated.SpeechModel, validated.AnalysisModel));
        try { _file.Write(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private sealed record StoredSettings(string? ApiKey, string? SpeechModel, string? AnalysisModel);
}