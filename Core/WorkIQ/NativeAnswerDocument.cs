using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ConversationAssistant.Core.WorkIQ;

public sealed record NativeAnswerRun(string Text, bool Bold = false, bool Italic = false, bool Code = false, Uri? Link = null);
public sealed record NativeAnswerParagraph(IReadOnlyList<NativeAnswerRun> Runs, int Heading = 0, bool Code = false);

public static class NativeAnswerDocument
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    public const int MaximumCharacters = 200_000;

    public static IReadOnlyList<NativeAnswerParagraph> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (markdown.Length > MaximumCharacters) markdown = markdown[..MaximumCharacters] + "\n\n[Content truncated]";
        var result = new List<NativeAnswerParagraph>();
        AppendBlocks(Markdown.Parse(markdown, Pipeline), result, "");
        return result;
    }

    private static void AppendBlocks(ContainerBlock container, List<NativeAnswerParagraph> result, string prefix)
    {
        foreach (var block in container)
        {
            if (block is Table table)
            {
                foreach (var row in table.OfType<TableRow>())
                {
                    var cells = new List<string>();
                    foreach (var cell in row.OfType<TableCell>())
                    {
                        var paragraphs = new List<NativeAnswerParagraph>();
                        AppendBlocks(cell, paragraphs, "");
                        cells.Add(string.Join(" ", paragraphs.SelectMany(paragraph => paragraph.Runs).Select(run => run.Text)));
                    }
                    result.Add(new NativeAnswerParagraph([new(string.Join("  |  ", cells), Bold: row.IsHeader)]));
                }
            }
            else if (block is ListBlock list)
            {
                var index = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                foreach (var item in list.OfType<ListItemBlock>())
                    AppendBlocks(item, result, prefix + (list.IsOrdered ? $"{index++}. " : "• "));
            }
            else if (block is QuoteBlock quote) AppendBlocks(quote, result, prefix + "│ ");
            else if (block is CodeBlock code)
                result.Add(new NativeAnswerParagraph([new(prefix + code.Lines.ToString(), Code: true)], Code: true));
            else if (block is LeafBlock leaf && leaf.Inline is not null)
            {
                var runs = new List<NativeAnswerRun>();
                if (prefix.Length > 0) runs.Add(new(prefix));
                AppendInlines(leaf.Inline, runs, false, false, null);
                result.Add(new NativeAnswerParagraph(runs, block is HeadingBlock heading ? heading.Level : 0));
            }
            else if (block is HtmlBlock html)
                result.Add(new NativeAnswerParagraph([new(prefix + html.Lines.ToString())]));
            else if (block is ContainerBlock nested) AppendBlocks(nested, result, prefix);
        }
    }

    private static void AppendInlines(ContainerInline container, List<NativeAnswerRun> runs, bool bold, bool italic, Uri? link)
    {
        foreach (var inline in container)
        {
            if (inline is LinkInline hyperlink)
            {
                var target = SafeLink(hyperlink.Url);
                if (hyperlink.IsImage) runs.Add(new("[Image: ", bold, italic));
                AppendInlines(hyperlink, runs, bold, italic, target);
                if (hyperlink.IsImage) runs.Add(new("]", bold, italic));
            }
            else if (inline is EmphasisInline emphasis)
                AppendInlines(emphasis, runs, bold || emphasis.DelimiterCount >= 2, italic || emphasis.DelimiterCount == 1, link);
            else if (inline is LiteralInline literal) runs.Add(new(literal.Content.ToString(), bold, italic, Link: link));
            else if (inline is CodeInline code) runs.Add(new(code.Content, bold, italic, true, link));
            else if (inline is LineBreakInline) runs.Add(new("\n", bold, italic));
            else if (inline is AutolinkInline auto) runs.Add(new(auto.Url, bold, italic, Link: SafeLink(auto.IsEmail ? "mailto:" + auto.Url : auto.Url)));
            else if (inline is HtmlInline html) runs.Add(new(html.Tag, bold, italic));
            else if (inline is ContainerInline nested) AppendInlines(nested, runs, bold, italic, link);
            else runs.Add(new(inline.ToString() ?? "", bold, italic));
        }
    }

    private static Uri? SafeLink(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "mailto" && string.IsNullOrEmpty(uri.UserInfo)
            ? uri : null;
}
