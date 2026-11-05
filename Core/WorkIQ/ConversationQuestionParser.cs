using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.WorkIQ;

public sealed record DiscoveredQuestion(string Question, string Reason)
{
    public RecommendationKind Kind { get; init; }
    public string Evidence { get; init; } = "";
    public string Role { get; init; } = "";
}
public sealed record ResolvedQuestion(string Question, string AnswerEvidence);
public sealed record DiscoveredAnalysis(string IntentSummary, string StageSummary,
    IReadOnlyList<DiscoveredQuestion> Questions, IReadOnlyList<ProductGuide> Products,
    ConversationAssistance? Assistance = null)
{
    public IReadOnlyList<ResolvedQuestion> ResolvedQuestions { get; init; } = [];
}

public static partial class ConversationQuestionParser
{
    public static IReadOnlyList<DiscoveredQuestion> Parse(string response) => ParseAnalysis(response).Questions;

    public static DiscoveredAnalysis ParseAnalysis(string response)
    {
        const string invalid = "Work IQ returned an invalid conversation analysis. Retry conversation analysis.";
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
                var kind = RecommendationKind.Unspecified;
                if (item.TryGetProperty("kind", out var kindValue))
                {
                    if (kindValue.ValueKind != JsonValueKind.String || kindValue.GetString() is not
                        ("Unanswered" or "FollowUp" or "ConversationControl")) throw new WorkIqException(invalid);
                    kind = Enum.Parse<RecommendationKind>(kindValue.GetString()!);
                }
                var evidence = OptionalText(item, "evidence", SuggestedQuestion.MaximumEvidenceLength, invalid);
                var role = OptionalText(item, "role", SuggestedQuestion.MaximumRoleLength, invalid);
                if (kind != RecommendationKind.Unspecified && evidence.Length == 0 ||
                    kind == RecommendationKind.ConversationControl && role.Length == 0)
                    throw new WorkIqException(invalid);
                if (seen.Add(QuestionKey(text)))
                    result.Add(new DiscoveredQuestion(text, reason.GetString()!.Trim())
                    {
                        Kind = kind, Evidence = evidence, Role = role
                    });
            }
            return new DiscoveredAnalysis(Summary("intentSummary"), Summary("stageSummary"), result,
                ParseProducts(document.RootElement, invalid), ParseAssistance(document.RootElement, invalid))
            {
                ResolvedQuestions = ParseResolutions(document.RootElement, seen, invalid)
            };

            string Summary(string name)
            {
                if (!document.RootElement.TryGetProperty(name, out var value)) return "";
                if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 8000)
                    throw new WorkIqException(invalid);
                return value.GetString()!.Trim();
            }
        }
        catch (JsonException error)
        {
            throw new WorkIqException(invalid, error);
        }
        catch (ArgumentException error)
        {
            throw new WorkIqException(invalid, error);
        }
    }

    private static string OptionalText(JsonElement item, string name, int maximum, string invalid)
    {
        if (!item.TryGetProperty(name, out var value)) return "";
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > maximum)
            throw new WorkIqException(invalid);
        return value.GetString()!.Trim();
    }

    private static IReadOnlyList<ResolvedQuestion> ParseResolutions(JsonElement root, HashSet<string> recommended, string invalid)
    {
        if (!root.TryGetProperty("resolvedQuestions", out var values)) return [];
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 100) throw new WorkIqException(invalid);
        var result = new List<ResolvedQuestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new WorkIqException(invalid);
            var question = OptionalText(item, "question", 8000, invalid);
            var evidence = OptionalText(item, "answerEvidence", SuggestedQuestion.MaximumEvidenceLength, invalid);
            var key = QuestionKey(question);
            if (key.Length == 0 || evidence.Length == 0 || recommended.Contains(key)) throw new WorkIqException(invalid);
            if (seen.Add(key)) result.Add(new ResolvedQuestion(question, evidence));
        }
        return result;
    }

    public static void ValidateEvidence(DiscoveredAnalysis analysis, IReadOnlyList<TranscriptSegment> transcript)
    {
        static string Normalize(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var turns = transcript.Select(item => Normalize(item.Text)).ToArray();
        bool Grounded(string evidence) => turns.Any(turn => turn.Contains(Normalize(evidence), StringComparison.OrdinalIgnoreCase));
        if (analysis.Questions.Any(question => question.Evidence.Length > 0 && !Grounded(question.Evidence)) ||
            analysis.ResolvedQuestions.Any(item => !Grounded(item.AnswerEvidence)))
            throw new WorkIqException("Work IQ returned an invalid conversation analysis. Retry conversation analysis.");
    }

    private static ConversationAssistance? ParseAssistance(JsonElement root, string invalid)
    {
        if (!root.TryGetProperty("assistance", out var item) || item.ValueKind == JsonValueKind.Null) return null;
        if (item.ValueKind != JsonValueKind.Object) throw new WorkIqException(invalid);
        var assistance = new ConversationAssistance
        {
            Conclusion = Text("conclusion", ConversationAssistance.MaximumConclusionLength),
            SuggestedWording = Text("suggestedWording", ConversationAssistance.MaximumWordingLength),
            KeyPoints = Items("keyPoints", ConversationAssistance.MaximumKeyPoints, ConversationAssistance.MaximumPointLength),
            Uncertainty = Text("uncertainty", ConversationAssistance.MaximumUncertaintyLength),
            Sources = Items("sources", ConversationAssistance.MaximumSources, ConversationAssistance.MaximumSourceLength)
        };
        if (!assistance.IsValid()) throw new WorkIqException(invalid);
        return assistance;

        string Text(string name, int maximum)
        {
            if (!item.TryGetProperty(name, out var value)) return "";
            if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > maximum)
                throw new WorkIqException(invalid);
            return value.GetString()!.Trim();
        }

        string[] Items(string name, int maximumCount, int maximumLength)
        {
            if (!item.TryGetProperty(name, out var values)) return [];
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > maximumCount)
                throw new WorkIqException(invalid);
            return values.EnumerateArray().Select(value =>
            {
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) ||
                    value.GetString()!.Length > maximumLength)
                    throw new WorkIqException(invalid);
                return value.GetString()!.Trim();
            }).Distinct(StringComparer.Ordinal).ToArray();
        }
    }

    private static IReadOnlyList<ProductGuide> ParseProducts(JsonElement root, string invalid)
    {
        if (!root.TryGetProperty("products", out var products)) return [];
        if (products.ValueKind != JsonValueKind.Array || products.GetArrayLength() > 100)
            throw new WorkIqException(invalid);
        var result = new List<ProductGuide>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in products.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new WorkIqException(invalid);
            var product = new ProductGuide
            {
                Name = Text(item, "name", ProductGuide.MaximumNameLength),
                Aliases = Metadata(item, "aliases", ProductGuide.MaximumNameLength),
                Overview = Text(item, "overview"),
                LicensingAndPricing = Text(item, "licensingAndPricing"),
                SuitableScenarios = Text(item, "suitableScenarios"),
                Limitations = Text(item, "limitations"),
                AdditionalNotes = Text(item, "additionalNotes", required: false),
                Sources = Metadata(item, "sources", 2048)
            };
            if (!product.Name.Any(char.IsLetterOrDigit)) throw new WorkIqException(invalid);
            var keys = product.IdentityNames.Select(ProductGuide.IdentityKey).ToArray();
            if (!keys.Any(seen.Contains)) result.Add(product);
            seen.UnionWith(keys);
        }
        return result;

        string Text(JsonElement item, string name, int maximum = ProductGuide.MaximumTextLength, bool required = true)
        {
            if (!item.TryGetProperty(name, out var value))
                return required ? throw new WorkIqException(invalid) : "";
            if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > maximum)
                throw new WorkIqException(invalid);
            var text = value.GetString()!.Trim();
            if (required && string.IsNullOrWhiteSpace(text)) throw new WorkIqException(invalid);
            return text;
        }

        string[] Metadata(JsonElement item, string name, int maximum)
        {
            if (!item.TryGetProperty(name, out var values)) return [];
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > ProductGuide.MaximumMetadataItems)
                throw new WorkIqException(invalid);
            var result = new List<string>();
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) ||
                    value.GetString()!.Length > maximum) throw new WorkIqException(invalid);
                result.Add(value.GetString()!.Trim());
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public static string QuestionKey(string question) =>
        Whitespace().Replace(question.Normalize(NormalizationForm.FormKC).Trim(), " ")
            .TrimEnd('?', '!', '.', '\u3002').TrimEnd().ToUpperInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
