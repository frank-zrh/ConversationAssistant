using Microsoft.UI.Xaml;

namespace ConversationAssistant_App.UI;

public sealed partial class MainViewModel
{
    public IReadOnlyList<AnalysisDocumentSection> IntentDocumentSections => AnalysisDocument.CreateIntent(
        Texts, CurrentAssistance, AssistanceStatus, AssistanceCoverage, IntentSummary,
        SelectedAssistanceVisibility == Visibility.Visible ? SelectedAssistanceText : "");

    public IReadOnlyList<AnalysisDocumentSection> StageDocumentSections => AnalysisDocument.CreateStages(
        Texts, StageSummaries.Select(row => (row.Caption, row.Summary)));
}
