using ConversationAssistant.Core.WorkIQ;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace ConversationAssistant_App.UI;

public sealed class NativeAnswerView : UserControl
{
    private readonly RichTextBlock _document = new()
    { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, LineHeight = 23 };
    public event Action<Uri>? LinkRequested;

    public NativeAnswerView() => Content = _document;

    public void Render(string markdown)
    {
        var paragraphs = NativeAnswerDocument.Parse(markdown);
        _document.Blocks.Clear();
        foreach (var section in paragraphs)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, section.Heading > 0 ? 10 : 0, 0, 8) };
            if (section.Heading > 0)
            {
                paragraph.FontSize = section.Heading switch { 1 => 24, 2 => 20, _ => 17 };
                paragraph.FontWeight = FontWeights.SemiBold;
            }
            foreach (var part in section.Runs)
            {
                var run = new Run { Text = part.Text };
                if (part.Bold) run.FontWeight = FontWeights.Bold;
                if (part.Italic) run.FontStyle = FontStyle.Italic;
                if (part.Code || section.Code) run.FontFamily = new FontFamily("Consolas");
                if (part.Link is { } uri)
                {
                    var hyperlink = new Hyperlink();
                    hyperlink.Inlines.Add(run);
                    hyperlink.Click += (_, _) => LinkRequested?.Invoke(uri);
                    paragraph.Inlines.Add(hyperlink);
                }
                else paragraph.Inlines.Add(run);
            }
            _document.Blocks.Add(paragraph);
        }
    }
}
