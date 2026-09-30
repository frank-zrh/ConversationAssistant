using System.Globalization;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Speech;

public static class SpeechLocaleSelector
{
    public static CultureInfo? Find(IEnumerable<CultureInfo> installed, ConversationLanguage language)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var cultures = installed.ToArray();
        return language switch
        {
            ConversationLanguage.Chinese => cultures.FirstOrDefault(c =>
                c.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)),
            ConversationLanguage.English => cultures.FirstOrDefault(c =>
                c.Name.Equals("en-US", StringComparison.OrdinalIgnoreCase)) ??
                cultures.FirstOrDefault(c => c.TwoLetterISOLanguageName == "en"),
            _ => null
        };
    }

    public static CultureInfo Select(IEnumerable<CultureInfo> installed, ConversationLanguage language)
    {
        if (language == ConversationLanguage.Auto)
            throw new InvalidOperationException(
                "Automatic speech language detection is unavailable. Select Chinese or English.");
        return Find(installed, language) ??
            throw new InvalidOperationException(language == ConversationLanguage.Chinese
                ? "Chinese (Simplified, zh-CN) local speech recognizer is not installed. Install the Windows Chinese speech language pack."
                : "English local speech recognizer is not installed. Install a Windows English speech language pack.");
    }
}
