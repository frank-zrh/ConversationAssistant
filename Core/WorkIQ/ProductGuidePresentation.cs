using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ConversationAssistant.Core.Models;
using System.Text.RegularExpressions;

namespace ConversationAssistant.Core.WorkIQ;

public sealed record ProductGuideBlock(string Text, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public bool IsTable => Rows.Count > 0;
}

/// <summary>Extracts inert text and tables; never fetches images or opens reference links.</summary>
public static partial class ProductGuidePresentation
{
    public static IReadOnlyList<ProductGuideBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];
        if (markdown.Length > ProductGuide.MaximumTextLength)
            markdown = markdown[..ProductGuide.MaximumTextLength];
        var html = new AnswerMarkdownFormatter().Format(markdown).Html;
        var document = new HtmlParser().ParseDocument("<body>" + html + "</body>");
        foreach (var image in document.QuerySelectorAll("img").ToArray()) image.Remove();
        foreach (var link in document.QuerySelectorAll("a").ToArray())
        {
            // Retain descriptive words, not URLs or numeric citation labels.
            var label = Clean(link.TextContent);
            link.Parent?.ReplaceChild(document.CreateTextNode(label), link);
        }
        foreach (var br in document.QuerySelectorAll("br").ToArray())
            br.Parent?.ReplaceChild(document.CreateTextNode("\n"), br);

        var blocks = new List<ProductGuideBlock>();
        foreach (var element in document.Body!.Children)
        {
            if (element.LocalName == "table")
            {
                var rows = element.QuerySelectorAll("tr")
                    .Select(row => (IReadOnlyList<string>)row.Children
                        .Where(cell => cell.LocalName is "th" or "td")
                        .Select(cell => Clean(cell.TextContent)).ToArray())
                    .Where(row => row.Count > 0).ToArray();
                if (rows.Length > 0) blocks.Add(new ProductGuideBlock("", rows));
            }
            else
            {
                var text = element.LocalName is "ul" or "ol"
                    ? string.Join("\n", element.Children.Select(item => "• " + Clean(item.TextContent)))
                    : Clean(element.TextContent);
                if (!string.IsNullOrWhiteSpace(text)) blocks.Add(new ProductGuideBlock(text, []));
            }
        }
        return blocks;
    }

    private static string Clean(string text) => References().Replace(text, "").Trim();

    [GeneratedRegex(@"https?://[^\s<>]+|\[(?:\d+(?:\s*[,–-]\s*\d+)*)\]|【[^】]*】", RegexOptions.IgnoreCase)]
    private static partial Regex References();
}
