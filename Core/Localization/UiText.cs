using System.Collections;
using System.Globalization;
using System.Resources;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Localization;

public sealed class UiText : IReadOnlyDictionary<string, string>
{
    private static readonly ResourceManager Resources =
        new("ConversationAssistant.Core.Localization.UiStrings", typeof(UiText).Assembly);
    private static readonly Lazy<UiText> English = new(() => new(ConversationLanguage.English, "en-US"));
    private static readonly Lazy<UiText> Chinese = new(() => new(ConversationLanguage.Chinese, "zh-CN"));
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    private UiText(ConversationLanguage language, string languageTag)
    {
        Language = language;
        Culture = CultureInfo.GetCultureInfo(languageTag);
        var resources = Resources.GetResourceSet(Culture, true, true)
            ?? throw new MissingManifestResourceException($"UI resources are missing for {languageTag}.");
        foreach (DictionaryEntry entry in resources)
        {
            if (entry.Key is not string key || entry.Value is not string value)
                throw new InvalidOperationException("UI resources must contain only named strings.");
            _values.Add(key, value);
        }
    }

    public static UiText For(ConversationLanguage language) => language switch
    {
        ConversationLanguage.Chinese => Chinese.Value,
        ConversationLanguage.English => English.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Select Chinese or English for the interface.")
    };

    public ConversationLanguage Language { get; }
    public CultureInfo Culture { get; }
    public string LanguageTag => Culture.Name;
    public string this[string key] => _values.TryGetValue(key, out var value) ? value :
        throw new MissingManifestResourceException($"UI resource '{key}' is missing for {LanguageTag}.");
    public string Format(string key, params object?[] arguments) => string.Format(Culture, this[key], arguments);
    public string LocalizeDiagnostic(string? message) => UiMessage.FromDiagnostic(message)?.Resolve(this) ?? "";
    public IEnumerable<string> Keys => _values.Keys;
    public IEnumerable<string> Values => _values.Values;
    public int Count => _values.Count;
    public bool ContainsKey(string key) => _values.ContainsKey(key);
    public bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
