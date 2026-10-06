using System.ComponentModel;
using ConversationAssistant.Core.Localization;

namespace ConversationAssistant_App.UI;

public sealed class LocalizedOption(string key, UiText texts) : INotifyPropertyChanged
{
    private UiText _texts = texts;
    public string Key => key;
    public string Label => _texts[key];
    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateLanguage(UiText value)
    {
        _texts = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
    }
}
