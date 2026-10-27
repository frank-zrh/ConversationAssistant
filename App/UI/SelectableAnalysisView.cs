using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace ConversationAssistant_App.UI;

public sealed class SelectableAnalysisView : UserControl
{
    private readonly RichTextBlock _document = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
    private IReadOnlyList<AnalysisDocumentSection> _renderedSections = [];
    public static readonly DependencyProperty SectionsProperty = DependencyProperty.Register(
        nameof(Sections), typeof(object), typeof(SelectableAnalysisView),
        new PropertyMetadata(null, (sender, _) => ((SelectableAnalysisView)sender).RefreshDocument()));

    public IReadOnlyList<AnalysisDocumentSection>? Sections
    {
        get => (IReadOnlyList<AnalysisDocumentSection>?)GetValue(SectionsProperty);
        set => SetValue(SectionsProperty, value);
    }

    public SelectableAnalysisView()
    {
        Content = _document;
    }

    private void RefreshDocument()
    {
        var sections = Sections ?? [];
        if (_renderedSections.SequenceEqual(sections)) return;
        _renderedSections = sections.ToArray();
        _document.Blocks.Clear();
        foreach (var section in sections)
        {
            if (!string.IsNullOrWhiteSpace(section.Heading))
            {
                var heading = new Paragraph { Margin = new Thickness(0, 8, 0, 4) };
                heading.Inlines.Add(new Run { Text = section.Heading, FontWeight = FontWeights.SemiBold, FontSize = 16 });
                _document.Blocks.Add(heading);
            }
            if (!string.IsNullOrWhiteSpace(section.Text))
            {
                var content = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                content.Inlines.Add(new Run { Text = section.Text });
                _document.Blocks.Add(content);
            }
        }
    }
}
