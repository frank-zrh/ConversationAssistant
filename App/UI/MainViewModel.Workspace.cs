using Microsoft.UI.Xaml;

namespace ConversationAssistant_App.UI;

public sealed partial class MainViewModel
{
    private readonly AnswerPanelLayout _answerPanelLayout = new();

    public GridLength AnalysisPanelHeight => new(_answerPanelLayout.AnalysisWeight, GridUnitType.Star);
    public GridLength AnswerPanelHeight => _answerPanelLayout.IsOpen
        ? new GridLength(_answerPanelLayout.AnswerWeight, GridUnitType.Star) : GridLength.Auto;
    public Visibility AnswerPanelVisibility => _answerPanelLayout.IsOpen ? Visibility.Visible : Visibility.Collapsed;
    public double AnswerPanelRowSpacing => _answerPanelLayout.IsOpen ? 8 : 0;
    public string AnswerPanelToggleText => Texts[_answerPanelLayout.IsOpen ? "CollapseAnswerPanel" : "ShowAnswerPanel"];
    public string AnswerPanelSizeText => Texts[_answerPanelLayout.Mode == AnswerPanelMode.Expanded
        ? "RestoreAnswerPanel" : "ExpandAnswerPanel"];

    public void ToggleAnswerPanel()
    {
        _answerPanelLayout.ToggleVisibility();
        NotifyAnswerPanel();
    }

    public void ToggleAnswerPanelExpansion()
    {
        _answerPanelLayout.ToggleExpansion();
        NotifyAnswerPanel();
    }

    private void ShowAnswerPanel()
    {
        _answerPanelLayout.Open();
        NotifyAnswerPanel();
    }

    private void ResetAnswerPanel()
    {
        _answerPanelLayout.Reset();
        NotifyAnswerPanel();
    }

    private void NotifyAnswerPanel() => Notify(nameof(AnalysisPanelHeight), nameof(AnswerPanelHeight),
        nameof(AnswerPanelVisibility), nameof(AnswerPanelRowSpacing), nameof(AnswerPanelToggleText), nameof(AnswerPanelSizeText));
}
