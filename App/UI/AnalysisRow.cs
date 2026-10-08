using System.ComponentModel;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant_App.UI;

public sealed class AnalysisRow(ConversationAnalysisRequest request, int number, UiText texts)
    : INotifyPropertyChanged
{
    public ConversationAnalysisRequest Request { get; } = request;
    public Guid Id => Request.Id;
    public UiText Texts { get; private set; } = texts;
    public string Scope => Request.GroupId is null ? Texts["AnalysisAllScope"] :
        Request.GroupName ?? Texts.Format("GroupDefaultName", Request.GroupNumber);
    public string Caption => Texts.Format("AnalysisHistoryItem", number,
        Request.Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss"), Scope);
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh(UiText language)
    {
        Texts = language;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Texts)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scope)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Caption)));
    }
}
