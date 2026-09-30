using System.Speech.Recognition;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Speech;

namespace ConversationAssistant_App.Speech;

public sealed class SpeechModelManager
{
    public IReadOnlyList<ConversationLanguage> AvailableLanguages()
    {
        var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
        var languages = new List<ConversationLanguage>();
        var cultures = recognizers.Select(r => r.Culture).ToArray();
        if (SpeechLocaleSelector.Find(cultures, ConversationLanguage.Chinese) is not null)
            languages.Add(ConversationLanguage.Chinese);
        if (SpeechLocaleSelector.Find(cultures, ConversationLanguage.English) is not null)
            languages.Add(ConversationLanguage.English);
        return languages;
    }

    public RecognizerInfo Resolve(ConversationLanguage language)
    {
        var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
        var culture = SpeechLocaleSelector.Select(recognizers.Select(r => r.Culture), language);
        return recognizers.First(r => r.Culture.Name.Equals(culture.Name, StringComparison.OrdinalIgnoreCase));
    }
}
