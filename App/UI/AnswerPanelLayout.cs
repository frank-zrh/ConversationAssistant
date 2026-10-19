namespace ConversationAssistant_App.UI;

internal enum AnswerPanelMode { Collapsed, Normal, Expanded }

internal sealed class AnswerPanelLayout
{
    public AnswerPanelMode Mode { get; private set; } = AnswerPanelMode.Collapsed;
    public bool IsOpen => Mode != AnswerPanelMode.Collapsed;
    public double AnalysisWeight => Mode == AnswerPanelMode.Normal ? 2 : 1;
    public double AnswerWeight => Mode switch
    {
        AnswerPanelMode.Normal => 1,
        AnswerPanelMode.Expanded => 3,
        _ => 0
    };

    public void Open()
    {
        if (!IsOpen) Mode = AnswerPanelMode.Normal;
    }

    public void ToggleVisibility() => Mode = IsOpen ? AnswerPanelMode.Collapsed : AnswerPanelMode.Normal;

    public void Cycle() => Mode = Mode switch
    {
        AnswerPanelMode.Collapsed => AnswerPanelMode.Normal,
        AnswerPanelMode.Normal => AnswerPanelMode.Expanded,
        _ => AnswerPanelMode.Collapsed
    };

    public void ToggleExpansion() => Mode = Mode == AnswerPanelMode.Expanded
        ? AnswerPanelMode.Normal : AnswerPanelMode.Expanded;

    public void Reset() => Mode = AnswerPanelMode.Collapsed;
}
