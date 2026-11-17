using ConversationAssistant.Core.WorkIQ;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class NativeAnswerDocumentTests
{
    [TestMethod]
    public void PreservesHeadingsEmphasisListsAndCode()
    {
        var paragraphs = NativeAnswerDocument.Parse("# Title\n\n**bold** and *italic*\n\n- first\n- second\n\n```csharp\nvar x = 1;\n``` ");
        Assert.AreEqual(1, paragraphs[0].Heading);
        Assert.IsTrue(paragraphs.SelectMany(item => item.Runs).Any(run => run.Bold && run.Text == "bold"));
        Assert.IsTrue(paragraphs.SelectMany(item => item.Runs).Any(run => run.Italic && run.Text == "italic"));
        Assert.IsTrue(paragraphs.Any(item => item.Code && item.Runs.Any(run => run.Text.Contains("var x", StringComparison.Ordinal))));
        Assert.IsTrue(paragraphs.SelectMany(item => item.Runs).Any(run => run.Text.Contains('•')));
    }

    [TestMethod]
    public void TablesRemainSelectableText()
    {
        var paragraphs = NativeAnswerDocument.Parse("| Name | Value |\n|---|---|\n| Test | 42 |");
        var text = string.Concat(paragraphs.SelectMany(item => item.Runs).Select(run => run.Text));
        StringAssert.Contains(text, "Name");
        StringAssert.Contains(text, "42");
        Assert.IsTrue(paragraphs[0].Runs[0].Bold);
    }

    [TestMethod]
    public void ActiveContentAndUnsafeLinksCannotExecute()
    {
        var paragraphs = NativeAnswerDocument.Parse("<script>alert(1)</script>\n\n[bad](javascript:alert) [file](file:///c:/secret) [good](https://example.com)");
        var runs = paragraphs.SelectMany(item => item.Runs).ToArray();
        Assert.AreEqual(1, runs.Count(run => run.Link is not null));
        Assert.AreEqual("https://example.com/", runs.Single(run => run.Link is not null).Link!.AbsoluteUri);
    }

    [TestMethod]
    public void RemoteImagesAreExplicitLinksNotNetworkRequests()
    {
        var paragraphs = NativeAnswerDocument.Parse("![diagram](https://example.com/image.png)");
        var runs = paragraphs.SelectMany(item => item.Runs).ToArray();
        StringAssert.Contains(string.Concat(runs.Select(run => run.Text)), "[Image: diagram]");
        Assert.AreEqual("https://example.com/image.png", runs.Single(run => run.Link is not null).Link!.AbsoluteUri);
    }
}
