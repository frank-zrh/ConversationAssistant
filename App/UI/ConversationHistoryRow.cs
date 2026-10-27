using System.ComponentModel;
using System.Globalization;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Persistence;

namespace ConversationAssistant_App.UI;

public sealed class ConversationHistoryRow(ConversationHistoryEntry entry, UiText texts) : INotifyPropertyChanged
{
    public ConversationHistoryEntry Entry { get; } = entry;
    public string Path => Entry.Path;
    public UiText Texts { get; private set; } = texts;
    public string Title => string.IsNullOrWhiteSpace(Entry.Title) ? Texts["HistoryUntitled"] : Entry.Title;
    public string StartTimeText => FormatTime(Entry.StartTime);
    public string EndTimeText => Entry.EndTime is { } end ? FormatTime(end) : Texts["HistoryEndNotSaved"];
    public string TimeRange => StartTimeText + "~" + EndTimeText;
    public string IntentSummary => string.IsNullOrWhiteSpace(Entry.IntentSummary)
        ? Texts["HistoryNoIntent"] : Entry.IntentSummary;
    public string AutomationLabel => Texts.Format("HistoryStartedAt", StartTimeText) + Environment.NewLine +
        Texts.Format("HistoryEndedAt", EndTimeText) + Environment.NewLine + IntentSummary;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateLanguage(UiText value)
    {
        Texts = value;
        foreach (var property in new[] { nameof(Texts), nameof(Title), nameof(StartTimeText), nameof(EndTimeText),
            nameof(TimeRange), nameof(IntentSummary), nameof(AutomationLabel) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private static string FormatTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
