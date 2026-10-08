using System.Collections.ObjectModel;
using System.ComponentModel;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ConversationAssistant_App.UI;

public interface ITranscriptListItem { }

public sealed class TranscriptGroupRow(TranscriptGroup group, UiText texts,
    Action<Guid, bool> setExpanded) : ITranscriptListItem, INotifyPropertyChanged
{
    public Guid Id => group.Id;
    public UiText Texts { get; private set; } = texts;
    public string Name => group.Name ?? Texts.Format("GroupDefaultName", group.Number);
    public string CountText => Texts.Format("GroupEntryCount", group.TranscriptIds.Count);
    public string AutomationLabel => $"{Name}, {CountText}";
    public ObservableCollection<TranscriptRow> Items { get; } = [];
    public bool IsExpanded
    {
        get => group.IsExpanded;
        set { if (value != group.IsExpanded) setExpanded(Id, value); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh(UiText language)
    {
        Texts = language;
        foreach (var name in new[] { nameof(Texts), nameof(Name), nameof(CountText),
            nameof(AutomationLabel), nameof(IsExpanded) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class TranscriptTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TranscriptTemplate { get; set; }
    public DataTemplate? GroupTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item) => item switch
    {
        TranscriptRow => TranscriptTemplate ?? throw new InvalidOperationException("Transcript template is missing."),
        TranscriptGroupRow => GroupTemplate ?? throw new InvalidOperationException("Group template is missing."),
        _ => throw new InvalidOperationException("Unknown transcript row type.")
    };

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
