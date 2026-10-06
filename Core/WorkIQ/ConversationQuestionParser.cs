using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ConversationAssistant.Core.Conversation;

namespace ConversationAssistant.Core.WorkIQ;

public sealed record DiscoveredQuestion(string Question, string Reason);

public static partial class ConversationQuestionParser
{
    public static IReadOnlyList<DiscoveredQuestion> Parse(string response)
    {
        const string invalid = "Work IQ returned an invalid question list. Retry conversation analysis.";
        var json = response.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = json.IndexOf('\n');
            var language = firstLine < 0 ? "" : json[3..firstLine].Trim();
            if (firstLine < 0 || language.Length > 0 &&
                !language.Equals("json", StringComparison.OrdinalIgnoreCase) ||
                !json.EndsWith("```", StringComparison.Ordinal))
                throw new WorkIqException(invalid);
            json = json[(firstLine + 1)..^3].Trim();
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("questions", out var questions) ||
                questions.ValueKind != JsonValueKind.Array)
                throw new WorkIqException(invalid);
            var result = new List<DiscoveredQuestion>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in questions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("question", out var question) ||
                    question.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(question.GetString()) ||
                    !item.TryGetProperty("reason", out var reason) ||
                    reason.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(reason.GetString()))
                    throw new WorkIqException(invalid);
                var text = question.GetString()!.Trim();
                if (!text.Any(char.IsLetterOrDigit)) throw new WorkIqException(invalid);
                if (seen.Add(QuestionKey(text)))
                    result.Add(new DiscoveredQuestion(text, reason.GetString()!.Trim()));
            }
            return result;
        }
        catch (JsonException error)
        {
            throw new WorkIqException(invalid, error);
        }
    }

    public static string QuestionKey(string question) =>
        Whitespace().Replace(question.Normalize(NormalizationForm.FormKC).Trim(), " ")
            .TrimEnd('?', '!', '.', '\u3002').TrimEnd().ToUpperInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
