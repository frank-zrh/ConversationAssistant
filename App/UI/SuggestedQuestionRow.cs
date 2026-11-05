using System.ComponentModel;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Transcript;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.WorkIQ;

namespace ConversationAssistant_App.UI;

public sealed class SuggestedQuestionRow(SuggestedQuestion question,
    ConversationLanguage language = ConversationLanguage.Chinese) : INotifyPropertyChanged
{
    private QuestionStatus? _status;
    public Guid Id => question.Id;
    public string Question => question.Question;
    public string Reason => question.Reason;
    public string CategoryText => Texts["Recommendation" + question.Kind];
    public string ReasonDetails => string.Join(Environment.NewLine + Environment.NewLine, new[]
    {
        Reason,
        question.Evidence.Length == 0 ? "" : Texts["RecommendationEvidence"] + Environment.NewLine + question.Evidence,
        question.Role.Length == 0 ? "" : Texts["RecommendationRole"] + Environment.NewLine + question.Role
    }.Where(text => !string.IsNullOrWhiteSpace(text)));
    public bool IsAnswered => _status == QuestionStatus.Completed;
    public UiText Texts { get; private set; } = UiText.For(language);
    public string StatusText => _status is null
        ? Texts["SuggestionUnasked"] : TranscriptCardAppearance.For(_status, Texts.Language).StatusText;
    public string AutomationLabel => $"{CategoryText}: {Question} — {StatusText}";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateLanguage(UiText texts)
    {
        Texts = texts;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Texts)));
        RefreshRecommendation();
        NotifyStatus();
    }

    public void UpdateRequest(QuestionRequest request)
    {
        if (request.SuggestedQuestionId != Id && !RecommendedQuestionBacklog.MatchesManualRequest(question, request))
            throw new ArgumentException("The request belongs to a different suggested question.", nameof(request));
        if (_status == request.Status) return;
        _status = request.Status;
        NotifyStatus();
    }

    public void RefreshRecommendation()
    {
        foreach (var property in new[] { nameof(Reason), nameof(CategoryText), nameof(ReasonDetails), nameof(AutomationLabel) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private void NotifyStatus()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAnswered)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomationLabel)));
    }
}
