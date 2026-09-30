using System.Text;
using System.Text.RegularExpressions;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Speech;

public static partial class SpeechTextNormalizer
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?<=\p{IsCJKUnifiedIdeographs})\s+(?=\p{IsCJKUnifiedIdeographs})")]
    private static partial Regex HanSpacing();

    [GeneratedRegex(@"\s+([,.，。！？?!])")]
    private static partial Regex BeforePunctuation();

    [GeneratedRegex(@"(?<=\p{IsCJKUnifiedIdeographs}),\s*(?=\p{IsCJKUnifiedIdeographs})")]
    private static partial Regex ChineseComma();

    [GeneratedRegex(@"(?<=\p{IsCJKUnifiedIdeographs})\?(?=$|\s|\p{IsCJKUnifiedIdeographs})")]
    private static partial Regex ChineseQuestionMark();

    [GeneratedRegex(@"(?<=\p{IsCJKUnifiedIdeographs})\.(?=$|\s|\p{IsCJKUnifiedIdeographs})")]
    private static partial Regex ChinesePeriod();

    public static string Normalize(string text, ConversationLanguage language)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (language is not (ConversationLanguage.Chinese or ConversationLanguage.English))
            throw new ArgumentOutOfRangeException(nameof(language), "Select Chinese or English for speech recognition.");
        var normalized = Whitespace().Replace(text.Normalize(NormalizationForm.FormKC), " ").Trim();
        normalized = BeforePunctuation().Replace(normalized, "$1");
        if (language == ConversationLanguage.Chinese)
        {
            normalized = HanSpacing().Replace(normalized, "");
            normalized = ChineseComma().Replace(normalized, "，");
            normalized = ChineseQuestionMark().Replace(normalized, "？");
            normalized = ChinesePeriod().Replace(normalized, "。");
        }
        return normalized;
    }
}
