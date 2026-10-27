using ConversationAssistant.Core.Models;
using Microsoft.UI.Xaml;

namespace ConversationAssistant_App.UI;

public sealed partial class MainViewModel
{
    private ConversationAnalysisRequest? _currentAssistanceAnalysis;
    private int _assistanceTranscriptCount;
    private bool _assistanceHistorical;
    private bool _assistanceFailed;

    private ConversationAssistance? CurrentAssistance => _currentAssistanceAnalysis?.Assistance;
    public string AssistanceConclusion => CurrentAssistance?.Conclusion ?? "";
    public string AssistanceWording => CurrentAssistance?.SuggestedWording ?? "";
    public string AssistanceKeyPoints => string.Join(Environment.NewLine,
        CurrentAssistance?.KeyPoints.Select(point => "• " + point) ?? []);
    public string AssistanceUncertainty => CurrentAssistance?.Uncertainty ?? "";
    public string AssistanceSources => string.Join(Environment.NewLine, CurrentAssistance?.Sources ?? []);
    public Visibility AssistanceVisibility => CurrentAssistance is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AssistanceWordingVisibility => VisibleContent(AssistanceWording);
    public Visibility AssistanceKeyPointsVisibility => VisibleContent(AssistanceKeyPoints);
    public Visibility AssistanceUncertaintyVisibility => VisibleContent(AssistanceUncertainty);
    public Visibility AssistanceSourcesVisibility => VisibleContent(AssistanceSources);
    public Visibility AssistanceCoverageVisibility => _currentAssistanceAnalysis is null ? Visibility.Collapsed : Visibility.Visible;
    public string AssistanceStatus => Texts[_analysisBusy ? "AssistanceUpdating" :
        _assistanceFailed ? "AssistanceUnavailable" :
        _currentAssistanceAnalysis is null ? "AssistanceWaiting" :
        CurrentAssistance is null ? "AssistanceNothingToAdd" : "AssistanceReady"];
    public string AssistanceCoverage => _currentAssistanceAnalysis is not { } analysis ? "" :
        Texts.Format(_assistanceHistorical ? "AssistanceSavedCoverage" :
            analysis.Transcript.Count < _assistanceTranscriptCount ? "AssistancePendingCoverage" : "AssistanceCurrentCoverage",
            analysis.Transcript.Last().TimestampEnd.ToLocalTime().ToString("HH:mm:ss"),
            analysis.Transcript.Count, _assistanceTranscriptCount);
    public string SelectedAssistanceText => SelectedAnalysis?.Request.Assistance is not { } assistance ? "" :
        string.Join(Environment.NewLine + Environment.NewLine, new[]
        {
            assistance.Conclusion,
            string.IsNullOrWhiteSpace(assistance.SuggestedWording) ? "" :
                Texts["AssistanceWordingHeader"] + Environment.NewLine + assistance.SuggestedWording,
            string.Join(Environment.NewLine, assistance.KeyPoints.Select(point => "• " + point)),
            string.IsNullOrWhiteSpace(assistance.Uncertainty) ? "" :
                Texts["AssistanceUncertaintyHeader"] + Environment.NewLine + assistance.Uncertainty,
            assistance.Sources.Count == 0 ? "" :
                Texts["AssistanceSourcesHeader"] + Environment.NewLine + string.Join(Environment.NewLine, assistance.Sources)
        }.Where(text => !string.IsNullOrWhiteSpace(text)));
    public Visibility SelectedAssistanceVisibility => SelectedAnalysis?.Id == _currentAssistanceAnalysis?.Id
        ? Visibility.Collapsed : VisibleContent(SelectedAssistanceText);

    private void RefreshAssistance()
    {
        _currentAssistanceAnalysis = _conversation.CurrentAssistanceAnalysis;
        var transcript = _conversation.TranscriptSegments;
        _assistanceTranscriptCount = transcript.Count;
        _assistanceHistorical = _conversation.Session is null;
        var latest = _conversation.Analyses.LastOrDefault(item => item.GroupId is null &&
            item.Transcript.Count > 0 && item.Transcript.Count <= transcript.Count &&
            item.Transcript.Select(segment => segment.Id).SequenceEqual(
                transcript.Take(item.Transcript.Count).Select(segment => segment.Id)));
        _assistanceFailed = latest?.Status == QuestionStatus.Failed;
        NotifyAssistance();
    }

    private void NotifyAssistance() => Notify(nameof(AssistanceConclusion), nameof(AssistanceWording),
        nameof(AssistanceKeyPoints), nameof(AssistanceUncertainty), nameof(AssistanceSources),
        nameof(AssistanceVisibility), nameof(AssistanceWordingVisibility), nameof(AssistanceKeyPointsVisibility),
        nameof(AssistanceUncertaintyVisibility), nameof(AssistanceSourcesVisibility),
        nameof(AssistanceCoverage), nameof(AssistanceCoverageVisibility), nameof(AssistanceStatus),
        nameof(SelectedAssistanceText), nameof(SelectedAssistanceVisibility), nameof(IntentDocumentSections));

    private static Visibility VisibleContent(string text) =>
        string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
}
