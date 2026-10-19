using System.ComponentModel;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant_App.UI;

public sealed class ProductGuideRow(ProductGuide product, UiText texts) : INotifyPropertyChanged
{
    private bool _isExpanded;
    public ProductGuide Product { get; } = product;
    public UiText Texts { get; private set; } = texts;
    public string SourcesText => Product.Sources.Count == 0 ? Texts["ProductInfoUnavailable"] : string.Join(Environment.NewLine, Product.Sources);
    public string AdditionalNotesText => Product.AdditionalNotes;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateLanguage(UiText language)
    {
        Texts = language;
        foreach (var name in new[] { nameof(Texts), nameof(SourcesText), nameof(AdditionalNotesText) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
