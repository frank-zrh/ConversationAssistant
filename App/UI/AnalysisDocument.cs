using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant_App.UI;

public sealed record AnalysisDocumentSection(string Heading, string Text);

public static class AnalysisDocument
{
    public static IReadOnlyList<AnalysisDocumentSection> CreateIntent(UiText texts,
        ConversationAssistance? assistance, string status, string coverage, string intent, string selectedAssistance)
    {
        var sections = new List<AnalysisDocumentSection> { new(texts["AssistanceHeader"], status) };
        void Add(string heading, string text)
        {
            if (!string.IsNullOrWhiteSpace(text)) sections.Add(new(heading, text));
        }
        if (assistance is not null)
        {
            Add("", assistance.Conclusion);
            Add(texts["AssistanceWordingHeader"], assistance.SuggestedWording);
            Add("", string.Join(Environment.NewLine, assistance.KeyPoints.Select(point => "• " + point)));
            Add(texts["AssistanceUncertaintyHeader"], assistance.Uncertainty);
            Add(texts["AssistanceSourcesHeader"], string.Join(Environment.NewLine, assistance.Sources));
        }
        Add("", coverage);
        Add(texts["IntentSummaryHeader"], texts["IntentHint"] + Environment.NewLine + Environment.NewLine +
            (string.IsNullOrWhiteSpace(intent) ? texts["IntentEmpty"] : intent));
        Add(texts["AssistanceHistoryHeader"], selectedAssistance);
        return sections;
    }

    public static IReadOnlyList<AnalysisDocumentSection> CreateStages(UiText texts,
        IEnumerable<(string Caption, string Summary)> summaries)
    {
        var sections = summaries.Select(item => new AnalysisDocumentSection(item.Caption, item.Summary)).ToArray();
        return sections.Length == 0 ? [new("", texts["StageSummariesEmpty"])] : sections;
    }
}
