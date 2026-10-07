using System.ComponentModel;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant_App.UI;

public sealed class AudioDeviceRow(AudioDevice device, UiText texts,
    AudioDeviceKind kind = AudioDeviceKind.Input) : INotifyPropertyChanged
{
    private UiText _texts = texts;
    public AudioDevice Device => device;
    public string Id => device.Id;
    public string Name => device.Id switch
    {
        "default" => _texts[kind == AudioDeviceKind.Input ? "DefaultMicrophone" : "DefaultOutput"],
        "communications" => _texts[kind == AudioDeviceKind.Input ? "DefaultCommunicationsMicrophone" : "DefaultCommunicationsOutput"],
        _ => device.Name
    };
    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateLanguage(UiText value)
    {
        _texts = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
    }
}
