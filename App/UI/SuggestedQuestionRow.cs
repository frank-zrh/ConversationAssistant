using System.ComponentModel;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Transcript;
using ConversationAssistant.Core.Localization;

namespace ConversationAssistant_App.UI;

public sealed class SuggestedQuestionRow(SuggestedQuestion question,
    ConversationLanguage language = ConversationLanguage.Chinese) : INotifyPropertyChanged
{
    private QuestionStatus? _status;
    public Guid Id => question.Id;
    public string Question => question.Question;
    public string Reason => question.Reason;
    public UiText Texts { get; private set; } = UiText.For(language);
    public string StatusText => _status is null
        ? Texts["SuggestionUnasked"] : TranscriptCardAppearance.For(_status, Texts.Language).StatusText;
    public string AutomationLabel => $"{Question} — {StatusText}";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateLanguage(UiText texts)
    {
        Texts = texts;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Texts)));
        NotifyStatus();
    }

    public void UpdateRequest(QuestionRequest request)
    {
        if (request.SuggestedQuestionId != Id)
            throw new ArgumentException("The request belongs to a different suggested question.", nameof(request));
        if (_status == request.Status) return;
        _status = request.Status;
        NotifyStatus();
    }

    private void NotifyStatus()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomationLabel)));
    }
}
