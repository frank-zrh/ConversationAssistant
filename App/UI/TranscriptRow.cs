using System.ComponentModel;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Transcript;
using ConversationAssistant.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ConversationAssistant_App.UI;

public sealed class TranscriptRow(TranscriptSegment segment,
    ConversationLanguage language = ConversationLanguage.Chinese) : ITranscriptListItem, INotifyPropertyChanged
{
    private QuestionStatus? _status;
    private bool _selected;
    public Guid Id => segment.Id;
    public string Time => segment.TimestampStart.ToLocalTime().ToString("HH:mm:ss");
    public string SpeakerText => TranscriptSpeaker.Label(segment.SpeakerNumber, Texts);
    public string CopyText => $"{Time}  {SpeakerText}: {Text}";
    public string Text => segment.Text;
    public Guid? QuestionId { get; private set; }
    public UiText Texts { get; private set; } = UiText.For(language);
    public string StatusText => TranscriptCardAppearance.For(_status, Texts.Language).StatusText;
    public string AutomationLabel => $"{Time} {SpeakerText}: {Text} — {StatusText}";
    public Brush CardBackground
    {
        get
        {
            var color = TranscriptCardAppearance.For(_status).BackgroundArgb;
            return new SolidColorBrush(Color.FromArgb((byte)(color >> 24), (byte)(color >> 16),
                (byte)(color >> 8), (byte)color));
        }
    }
    public Brush SelectionBorder => new SolidColorBrush(_selected
        ? Color.FromArgb(255, 37, 99, 235) : Color.FromArgb(255, 205, 214, 220));
    public Thickness SelectionThickness => new(_selected ? 2 : 1);
    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Notify(nameof(IsSelected), nameof(SelectionBorder), nameof(SelectionThickness));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateLanguage(UiText texts)
    {
        Texts = texts;
        Notify(nameof(Texts), nameof(SpeakerText), nameof(CopyText), nameof(StatusText), nameof(AutomationLabel));
    }

    public void UpdateRequest(QuestionRequest request)
    {
        if (request.TranscriptSegmentId != Id)
            throw new ArgumentException("The request belongs to a different transcript card.", nameof(request));
        QuestionId = request.Id;
        _status = request.Status;
        Notify(nameof(QuestionId), nameof(StatusText), nameof(AutomationLabel), nameof(CardBackground));
    }

    private void Notify(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
