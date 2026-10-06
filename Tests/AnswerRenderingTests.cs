using ConversationAssistant.Core.WorkIQ;
using ConversationAssistant.Core.Context;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class AnswerRenderingTests
{
    [TestMethod]
    public void RapidAnswerChangesNavigateSeriallyAndCoalesceToLatestDocument()
    {
        var queue = new AnswerRenderQueue();
        queue.Enqueue(1, "<p>First answer</p>");
        var first = queue.StartNext();
        Assert.IsNotNull(first);
        queue.Enqueue(2, "<p>Intermediate selection</p>");
        queue.Enqueue(3, "<p>Latest selection</p>");
        Assert.IsNull(queue.StartNext());
        Assert.AreSame(first, queue.Active);
        Assert.IsTrue(queue.Complete(first.Revision));
        var next = queue.StartNext();
        Assert.IsNotNull(next);
        Assert.AreEqual(3L, next.Revision);
        Assert.AreEqual("<p>Latest selection</p>", next.Html);
        Assert.IsFalse(queue.Complete(first.Revision));
        Assert.AreSame(next, queue.Active);
        Assert.IsTrue(queue.Complete(next.Revision));
        Assert.IsNull(queue.StartNext());
    }

    [TestMethod]
    public void ClearingRenderQueueDropsPendingDocumentsAndIgnoresLateCompletion()
    {
        var queue = new AnswerRenderQueue();
        queue.Enqueue(1, "<p>Old answer</p>");
        queue.StartNext();
        queue.Enqueue(2, "<p>Pending answer</p>");
        queue.Clear();
        Assert.IsNull(queue.Active);
        Assert.IsNull(queue.StartNext());
        Assert.IsFalse(queue.Complete(1));
        queue.Enqueue(3, "<p>New answer</p>");
        Assert.AreEqual(3L, queue.StartNext()!.Revision);
    }

    [TestMethod]
    public void LocalWebViewNavigationIsAllowedOnlyForAppRequestedHtml()
    {
        Assert.IsTrue(AnswerNavigationPolicy.IsAppHtmlDocument(
            "data:text/html;charset=utf-8,%3Chtml%3E", false, true));
        Assert.IsTrue(AnswerNavigationPolicy.IsAppHtmlDocument("data:text/html,<html>", false, true));
        Assert.IsFalse(AnswerNavigationPolicy.IsAppHtmlDocument("data:text/html,<html>", true, true));
        Assert.IsFalse(AnswerNavigationPolicy.IsAppHtmlDocument("data:text/html,<html>", false, false));
        Assert.IsFalse(AnswerNavigationPolicy.IsAppHtmlDocument("https://example.com", false, true));
        Assert.IsFalse(AnswerNavigationPolicy.IsAppHtmlDocument("data:text/html-malicious,<html>", false, true));
        Assert.IsFalse(AnswerNavigationPolicy.IsAppHtmlDocument("file:///secret.txt", false, true));
    }

    [TestMethod]
    public void FailedQuestionIsNotDisplayedAsThinkingAndPlainAnswerIsPreserved()
    {
        var message = AnswerPresentation.Compose("", QuestionStatus.Failed, []);
        StringAssert.Contains(message, "could not answer");
        Assert.IsFalse(message.Contains("Thinking", StringComparison.Ordinal));
        const string original = "Raw answer\n\n|Column|\n|---|\n|Value|";
        Assert.AreEqual(original, AnswerPresentation.Compose(original, QuestionStatus.Completed, []));
        StringAssert.Contains(AnswerHtmlPage.Build(new RenderedAnswer("<p>ok</p>",
            new Dictionary<string, string>()), 12), "name=\"mc-render-id\" content=\"12\"");
    }

    [TestMethod]
    public void PromptReconstructsSpeechQuestionBeforeAnswerWithoutChangingOriginalInput()
    {
        const string original = "How do we set dee el pee for twenty environments not two";
        var prompt = new ContextBuilder(new PromptBuilder()).Build(original,
            "Our security team wants to prevent data leaks.", new ConversationSettings());
        StringAssert.Contains(prompt, original);
        StringAssert.Contains(prompt, "QUESTION RECONSTRUCTION:");
        StringAssert.Contains(prompt, "negation, names, product terms, numbers and constraints");
        StringAssert.Contains(prompt, "label it as an assumption");
        StringAssert.Contains(prompt, "ask one focused clarification");
        Assert.IsLessThan(prompt.IndexOf("## SUGGESTED ANSWER", StringComparison.Ordinal),
            prompt.IndexOf("## UNDERSTOOD QUESTION", StringComparison.Ordinal));
        StringAssert.Contains(prompt, "untrusted context");
        StringAssert.Contains(prompt, "CONVERSATION ASSISTANT APPLICATION INSTRUCTIONS");
        StringAssert.Contains(prompt, "RECENT CONVERSATION CONTEXT");
        StringAssert.Contains(prompt, "action request rather than an interrogative");
    }

    [TestMethod]
    public void ReconstructedQuestionDoesNotGetMixedIntoSuggestedAnswer()
    {
        var sections = AnswerSections.Parse("""
            ## UNDERSTOOD QUESTION
            How should we set DLP for twenty environments?
            ## SUGGESTED ANSWER
            Start with a tenant-wide baseline.
            ## KEY POINTS
            - Review exceptions.
            ## SOURCES / CONTEXT
            Conversation context only.
            """);
        Assert.AreEqual("How should we set DLP for twenty environments?", sections.UnderstoodQuestion);
        Assert.AreEqual("Start with a tenant-wide baseline.", sections.SuggestedAnswer);
    }

    [TestMethod]
    public void MarkdownPreservesStructuredAnswerTableAndHttpsCitations()
    {
        var result = new AnswerMarkdownFormatter().Format("""
            ## SUGGESTED ANSWER
            Use a layered DLP policy.

            ## KEY POINTS
            | Environment | Recommendation |
            | --- | --- |
            | Production | Restrict external connectors |

            ## SOURCES / CONTEXT
            [Policy](https://contoso.sharepoint.com/sites/policy)
            """);
        StringAssert.Contains(result.Html, "<h2>SUGGESTED ANSWER</h2>");
        StringAssert.Contains(result.Html, "<table>");
        StringAssert.Contains(result.Html, "<th>Environment</th>");
        StringAssert.Contains(result.Html, "https://contoso.sharepoint.com/sites/policy");
    }

    [TestMethod]
    public void RemoteImagesAreVisibleAsExplicitLinksWithoutAutomaticNetworkLoading()
    {
        var result = new AnswerMarkdownFormatter().Format(
            "![DLP architecture](https://contoso.sharepoint.com/images/dlp.png)");
        Assert.HasCount(1, result.RemoteImages);
        StringAssert.Contains(result.Html, "Image: DLP architecture");
        StringAssert.Contains(result.Html, "Open image from contoso.sharepoint.com");
        Assert.IsFalse(result.Html.Contains("<img", StringComparison.OrdinalIgnoreCase));
        var page = AnswerHtmlPage.Build(result);
        StringAssert.Contains(page, "img-src data:");
        StringAssert.Contains(page, "script-src 'none'");
        Assert.IsFalse(page.Contains("img-src https:", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void EmbeddedPngDisplaysInlineButScriptsAndLocalUrlsCannotExecute()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO2VgqUAAAAASUVORK5CYII=";
        var result = new AnswerMarkdownFormatter().Format(
            $"![small chart](data:image/png;base64,{png})\n\n" +
            "[bad](javascript:alert(1)) <script>alert(2)</script> " +
            "<img src=\"file:///C:/Users/secret.png\" onerror=\"alert(3)\">");
        StringAssert.Contains(result.Html, "<img");
        StringAssert.Contains(result.Html, "data:image/png;base64");
        Assert.IsFalse(result.Html.Contains("javascript:", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Html.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Html.Contains("onerror", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Html.Contains("file://", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void NonHttpsAndLoopbackLinksAreNotNavigable()
    {
        var formatter = new AnswerMarkdownFormatter();
        var result = formatter.Format("[local](https://127.0.0.1/admin) " +
            "[insecure](http://example.com) [safe](https://example.com/source)");
        Assert.IsFalse(result.Html.Contains("https://127.0.0.1", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Html.Contains("http://example.com", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(result.Html, "href=\"https://example.com/source\"");
        Assert.IsFalse(AnswerMarkdownFormatter.IsSafeRemoteUri("https://localhost/a", false, out _));
        Assert.IsFalse(AnswerMarkdownFormatter.IsSafeRemoteUri("https://user:pass@example.com/a", false, out _));
        Assert.IsTrue(AnswerMarkdownFormatter.CanFetchInlineImage(
            new Uri("https://contoso.sharepoint.com/diagrams/dlp.png")));
        Assert.IsFalse(AnswerMarkdownFormatter.CanFetchInlineImage(
            new Uri("https://sharepoint.com.attacker.example/diagrams/dlp.png")));
        Assert.IsFalse(AnswerMarkdownFormatter.CanFetchInlineImage(
            new Uri("images/chart.png", UriKind.Relative)));
    }

    [TestMethod]
    public void ExplicitlyApprovedRasterImageCanBeRenderedInlineAfterConsent()
    {
        const string address = "https://contoso.sharepoint.com/images/chart.png";
        const string data = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO2VgqUAAAAASUVORK5CYII=";
        var result = new AnswerMarkdownFormatter().Format($"![chart]({address})",
            new Dictionary<string, string> { [address] = data });
        StringAssert.Contains(result.Html, "<img");
        StringAssert.Contains(result.Html, "data:image/png;base64");
        Assert.HasCount(1, result.RemoteImages);
    }
}
