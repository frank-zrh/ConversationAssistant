using System.Text.RegularExpressions;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.QuestionDetection;

public interface IQuestionDetector
{
    QuestionDetectionResult Detect(TranscriptSegment finalSegment, string recentContext,
        QuestionSensitivity sensitivity, ConversationLanguage language);
}

public sealed partial class QuestionDetector : IQuestionDetector
{
    private const string ChineseAction =
        "(?:查找|查询|搜索|检索|寻找|找|查|搜|看|总结|汇总|整理|列出|列|分析|比较|对比|解释|说明|介绍|" +
        "推荐|建议|确认|核实|检查|生成|起草|撰写|写|制定|提供|计算|翻译|发送|安排|告诉)";

    [GeneratedRegex(@"^(?:(?:and|so|but)\s+)?(?:who|what|when|where|why|how|which|can|could|would|should|do|does|did|is|are|will)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishOpening();

    [GeneratedRegex(@"^(?:(?:and|so|then)\s+)?(?:(?:please|kindly)\s+)?" +
        @"(?:(?:help\s+(?:me|us)(?:\s+to)?|(?:I|we)\s+(?:need|want)\s+you\s+to)\s+)?" +
        @"(?:find|search|look\s+(?:up|for|into)|check|show|tell|summari[sz]e|list|compare|analy[sz]e|" +
        @"review|draft|write|create|prepare|identify|locate|calculate|translate|retrieve|gather|provide|" +
        @"generate|outline|recommend|suggest|explain|send|schedule)\b|(?:\b(?:recommendations?|suggestions?)\b.*[?？])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishRequest();

    [GeneratedRegex(@"^(?:(?:那(?:么)?|然后|接下来|另外)[，,\s]*)?(?:" +
        @"(?:(?:请(?:你|您)?|麻烦(?:你|您)?|劳驾(?:你|您)?)\s*(?:(?:帮|替|给)(?:我们|我)\s*)?|" +
        @"(?:你|您)?\s*(?:帮|替|给)(?:我们|我)\s*)" + ChineseAction + "|" + ChineseAction +
        @"(?:一下|一份|一个|一封|一段|一张|一篇)|找找|查查|搜搜|看看|找一找|查一查|搜一搜|看一看|" +
        @"告诉(?:我们|我)|给(?:我们|我)(?:一些|一个|一份|几点|点))")]
    private static partial Regex ChineseRequest();

    [GeneratedRegex(@"为什么|怎么|如何|什么|哪个|哪些|是不是|能不能|可不可以|有没有|怎么看|有什么建议|请.*建议|[吗呢][?？。！!]*$")]
    private static partial Regex ChineseQuestion();

    [GeneratedRegex(@"(?:讨论|研究|介绍|说明|分析|讲解|汇报|记录)(?:了|一下)?(?:如何|怎么|为什么|什么|哪个|哪些)")]
    private static partial Regex EmbeddedQuestionInStatement();

    [GeneratedRegex(@"[。！？!?；;]\s*|\.(?:\s+|$)")]
    private static partial Regex SentenceBoundary();

    [GeneratedRegex(@"[,，]\s*")]
    private static partial Regex ClauseBoundary();

    public QuestionDetectionResult Detect(TranscriptSegment finalSegment, string recentContext,
        QuestionSensitivity sensitivity, ConversationLanguage language)
    {
        ArgumentNullException.ThrowIfNull(finalSegment);
        if (language is not (ConversationLanguage.Chinese or ConversationLanguage.English))
            throw new ArgumentOutOfRangeException(nameof(language), "Select Chinese or English for question detection.");
        if (!finalSegment.IsFinal)
            return new(false, 0, finalSegment.Text, "Partial speech", finalSegment.TimestampEnd);
        var text = finalSegment.Text.Trim();
        var boundary = SentenceBoundary().Matches(text)
            .Cast<Match>().LastOrDefault(match => match.Index + match.Length < text.Length);
        var start = boundary is null ? 0 : boundary.Index + boundary.Length;
        var prefix = text[..start].Trim();
        var candidate = text[start..].Trim();
        var chineseMode = language == ConversationLanguage.Chinese;
        var englishMode = language == ConversationLanguage.English;
        if (!IsRequest(candidate, language))
        {
            var requestBoundary = ClauseBoundary().Matches(candidate).Cast<Match>()
                .FirstOrDefault(match => IsRequest(candidate[(match.Index + match.Length)..], language));
            var clauseStart = requestBoundary is null ? -1 : requestBoundary.Index + requestBoundary.Length;
            if (clauseStart < 0 && chineseMode)
            {
                var comma = candidate.LastIndexOfAny([',', '，']);
                if (comma >= 0 && ChineseQuestion().IsMatch(candidate[(comma + 1)..]))
                    clauseStart = comma + 1;
            }
            if (clauseStart >= 0)
            {
                prefix = string.Join(" ", new[] { prefix, candidate[..clauseStart] }
                    .Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
                candidate = candidate[clauseStart..].Trim();
            }
        }
        var punctuation = candidate.EndsWith('?') || candidate.EndsWith('？');
        var opening = englishMode && EnglishOpening().IsMatch(candidate);
        var request = IsRequest(candidate, language);
        var chinese = chineseMode && ChineseQuestion().IsMatch(candidate) &&
            (punctuation || !EmbeddedQuestionInStatement().IsMatch(candidate));
        var containsHan = candidate.Any(c => c is >= '\u3400' and <= '\u9fff');
        var punctuationInLanguage = punctuation && (chineseMode && containsHan ||
            englishMode && !containsHan &&
            candidate.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'));
        var confidence = opening || chinese || request ? .9 : punctuationInLanguage ? .75 : 0;
        var min = sensitivity switch
        {
            QuestionSensitivity.Low => .85,
            QuestionSensitivity.High => .7,
            _ => .75
        };
        var question = candidate.Length >= 3 && confidence >= min;
        var questionText = question && !request
            ? candidate.TrimEnd('。', '.', '?', '？').TrimEnd() +
                (chineseMode ? "？" : "?")
            : candidate;
        return new(question, confidence, questionText,
            request ? "Request" : opening ? "English interrogative" : chinese ? "Chinese interrogative" :
            punctuation ? "Question punctuation" : "Statement",
            finalSegment.TimestampEnd, prefix);
    }

    private static bool IsRequest(string text, ConversationLanguage language) => language switch
    {
        ConversationLanguage.Chinese => ChineseRequest().IsMatch(text),
        ConversationLanguage.English => EnglishRequest().IsMatch(text),
        _ => false
    };
}
