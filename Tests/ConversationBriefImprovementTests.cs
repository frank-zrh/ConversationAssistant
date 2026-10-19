using ConversationAssistant.Core.Context;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Persistence;
using ConversationAssistant.Core.WorkIQ;
using System.Xml.Linq;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class ConversationBriefImprovementTests
{
    [TestMethod]
    public void LanguageConfigurationDoesNotConsumeWorkspaceHeaderHeight()
    {
        var page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var header = page.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "PageHeader");
        var settings = page.Descendants().Single(element => (string?)element.Attribute(x + "Key") == "SettingsFlyout");
        Assert.IsFalse(header.Descendants().Any(element => element.Name.LocalName == "ComboBox"));
        foreach (var name in new[] { "LanguagePicker", "AnalysisLanguagePicker", "DisplayLanguagePicker" })
        {
            var picker = page.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
            Assert.IsTrue(picker.Ancestors().Contains(settings), name);
            Assert.IsNull(picker.Attribute("Header"), name);
            Assert.AreEqual("1", (string?)picker.Attribute("Grid.Column"), name);
        }
        Assert.IsTrue(header.Descendants().Any(element => (string?)element.Attribute("Click") == "Settings_Click"));
        var workspace = page.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "WorkspaceLayout");
        Assert.AreEqual("1", (string?)workspace.Attribute("Grid.Row"));
        var rows = header.Parent!.Elements().Single(element => element.Name.LocalName == "Grid.RowDefinitions");
        Assert.AreEqual("*", (string?)rows.Elements().ElementAt(1).Attribute("Height"));
    }

    [TestMethod]
    public void PageOffersThreeIndependentLanguagePickersAndNativeGuideTables()
    {
        var page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var speech = page.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "LanguagePicker");
        var analysis = page.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "AnalysisLanguagePicker");
        var display = page.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "DisplayLanguagePicker");
        StringAssert.Contains((string)speech.Attribute("SelectedIndex")!, "SpeechLanguageIndex");
        StringAssert.Contains((string)speech.Attribute("IsEnabled")!, "CanChangeSpeechLanguage");
        StringAssert.Contains((string)analysis.Attribute("SelectedIndex")!, "AnalysisLanguageIndex");
        StringAssert.Contains((string)display.Attribute("SelectedIndex")!, "DisplayLanguageIndex");
        Assert.IsNull(analysis.Attribute("IsEnabled"));
        Assert.IsNull(display.Attribute("IsEnabled"));
        Assert.IsTrue(page.Descendants().Any(element => element.Name.LocalName == "ProductGuideSection" &&
            ((string?)element.Attribute("Text"))?.Contains("Product.LicensingAndPricing", StringComparison.Ordinal) == true));
        Assert.IsFalse(page.ToString().Contains("{Binding SourcesText}", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AllThreeLanguagesDefaultToChineseAndRemainIndependent()
    {
        var settings = new ConversationSettings();
        Assert.AreEqual(ConversationLanguage.Chinese, settings.Language);
        Assert.AreEqual(ConversationLanguage.Chinese, settings.AnalysisLanguage);
        Assert.AreEqual(ConversationLanguage.Chinese, settings.DisplayLanguage);
        settings.Language = ConversationLanguage.English;
        Assert.AreEqual(ConversationLanguage.Chinese, settings.AnalysisLanguage);
        Assert.AreEqual(ConversationLanguage.Chinese, settings.DisplayLanguage);
        settings.AnalysisLanguage = ConversationLanguage.English;
        settings.DisplayLanguage = ConversationLanguage.English;
        settings.Language = ConversationLanguage.Chinese;
        Assert.AreEqual(ConversationLanguage.English, settings.AnalysisLanguage);
        Assert.AreEqual(ConversationLanguage.English, settings.DisplayLanguage);
        settings.DisplayLanguage = ConversationLanguage.Chinese;
        Assert.AreEqual(ConversationLanguage.English, settings.AnalysisLanguage);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => settings.AnalysisLanguage = ConversationLanguage.Auto);
    }

    [TestMethod]
    public void PromptLanguageDependsOnlyOnAnalysisLanguage()
    {
        foreach (var listening in new[] { ConversationLanguage.Chinese, ConversationLanguage.English })
        foreach (var display in new[] { ConversationLanguage.Chinese, ConversationLanguage.English })
        foreach (var analysis in new[] { ConversationLanguage.Chinese, ConversationLanguage.English })
        {
            var settings = new ConversationSettings
            {
                Language = listening, DisplayLanguage = display, AnalysisLanguage = analysis
            };
            var language = analysis == ConversationLanguage.Chinese ? "Chinese" : "English";
            StringAssert.Contains(new PromptBuilder().Build("A question", "A conversation", settings), $"Answer in {language}.");
            StringAssert.Contains(ConversationAnalysisPrompt.Build(Request(), settings),
                $"Write summaries, questions and reasons in {language}.");
        }
    }

    [TestMethod]
    public void IndependentLanguageSettingsRoundTripInArchive()
    {
        var session = new ConversationSession
        {
            Settings = new ConversationSettings
            {
                Language = ConversationLanguage.English,
                AnalysisLanguage = ConversationLanguage.Chinese,
                DisplayLanguage = ConversationLanguage.English
            }
        };
        var restored = ConversationArchive.Capture(session).Restore().Settings;
        Assert.AreEqual(ConversationLanguage.English, restored.Language);
        Assert.AreEqual(ConversationLanguage.Chinese, restored.AnalysisLanguage);
        Assert.AreEqual(ConversationLanguage.English, restored.DisplayLanguage);
        restored.AnswerLanguage = ConversationLanguage.Auto; // Legacy archives are still understood.
        Assert.AreEqual(ConversationLanguage.Chinese, restored.AnalysisLanguage);
    }

    [TestMethod]
    public void GuideTablesRenderCellsAndKeepEscapedPipesAndUnknownPrices()
    {
        var blocks = ProductGuidePresentation.Parse("""
            Familiar basics, briefly.

            | License | Rule | Price |
            | --- | --- | --- |
            | Base | A \| B | Unverified |
            | Add-on | **Required** for this scenario | USD / user / month |
            """);
        Assert.AreEqual("Familiar basics, briefly.", blocks[0].Text);
        var table = blocks.Single(block => block.IsTable);
        Assert.AreEqual(3, table.Rows.Count);
        Assert.AreEqual(3, table.Rows[1].Count);
        Assert.AreEqual("A | B", table.Rows[1][1]);
        Assert.AreEqual("Unverified", table.Rows[1][2]);
        Assert.AreEqual("Required for this scenario", table.Rows[2][1]);
    }

    [TestMethod]
    public void GuidePresentationHasNoActiveLinksImagesOrCitationAppendices()
    {
        var blocks = ProductGuidePresentation.Parse("""
            A [relevant rule](https://example.com/terms) [1].

            https://example.com/reference
            """);
        var text = string.Join("\n", blocks.Select(block => block.Text));
        StringAssert.Contains(text, "relevant rule");
        Assert.IsFalse(text.Contains("https://", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("[1]", StringComparison.Ordinal));
        Assert.IsEmpty(ProductGuidePresentation.Parse(""));
    }

    [TestMethod]
    public async Task InvalidAnalysisIsAutomaticallyRetriedBeforeReturningValidResult()
    {
        var client = new ScriptedClient((attempt, _) => Task.FromResult(new WorkIqAnswer(
            attempt == 1 ? "invalid JSON" : """{"intentSummary":"Valid","questions":[]}""",
            [], attempt == 1 ? "invalid-id" : "valid-id")));
        var result = await WorkIqPromptSender.AskAnalysisAsync(client, "Analyze", "original-id", CancellationToken.None);
        Assert.AreEqual(2, client.Calls.Count);
        Assert.AreEqual("Valid", result.Analysis.IntentSummary);
        Assert.AreEqual("valid-id", result.Answer.ConversationId);
        Assert.IsTrue(client.Calls.All(call => call.ConversationId == "original-id"));
        StringAssert.Contains(client.Calls[1].Prompt, "FORMAT RECOVERY");
    }

    [TestMethod]
    public async Task SchemaFailureRetriesAreBoundedAndFinalFailureIsLocalized()
    {
        var client = new ScriptedClient((_, _) => Task.FromResult(new WorkIqAnswer("""{"questions":null}""", [], null)));
        var error = await Assert.ThrowsExactlyAsync<WorkIqException>(async () =>
            await WorkIqPromptSender.AskAnalysisAsync(client, "Analyze", null, CancellationToken.None));
        Assert.AreEqual(WorkIqPromptSender.MaximumAnalysisAttempts, client.Calls.Count);
        Assert.AreEqual(UiText.For(ConversationLanguage.Chinese)["ErrorAnalysisRetryExhausted"],
            UiText.For(ConversationLanguage.Chinese).LocalizeDiagnostic(error.Message));
    }

    [TestMethod]
    public async Task CallerCancellationStopsAnalysisRecovery()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new ScriptedClient((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new WorkIqAnswer("broken", [], null));
        });
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await WorkIqPromptSender.AskAnalysisAsync(client, "Analyze", null, cancellation.Token));
        Assert.AreEqual(1, client.Calls.Count);
    }

    [TestMethod]
    public async Task AuthorizationOrProcessErrorsAreNotRetriedAsSchemaErrors()
    {
        var client = new ScriptedClient((_, _) => Task.FromException<WorkIqAnswer>(new WorkIqException("Permission denied")));
        await Assert.ThrowsExactlyAsync<WorkIqException>(async () =>
            await WorkIqPromptSender.AskAnalysisAsync(client, "Analyze", null, CancellationToken.None));
        Assert.AreEqual(1, client.Calls.Count);
    }

    private static ConversationAnalysisRequest Request() => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Transcript = [], ExistingQuestions = [], Trigger = ConversationAnalysisTrigger.Manual
    };

    private sealed class ScriptedClient(Func<int, CancellationToken, Task<WorkIqAnswer>> reply) : IWorkIqClient
    {
        public List<(string Prompt, string? ConversationId)> Calls { get; } = [];
        public Task<WorkIqAnswer> AskAsync(string prompt, string? conversationId, CancellationToken cancellationToken)
        {
            Calls.Add((prompt, conversationId));
            return reply(Calls.Count, cancellationToken);
        }
    }
}

public sealed partial class PipelineTests
{
    [TestMethod]
    public async Task AcceptedRetryRemainsSuccessfulWhenWorkerCompletesBeforeReturn()
    {
        var fake = new Harness();
        fake.Auth.IsSignedIn = false;
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        using var completed = new ManualResetEventSlim();
        try
        {
            var request = fake.Conversation.AskManually("Check licensing prerequisites.");
            await WaitUntil(() => request.Status == QuestionStatus.Failed);
            fake.Auth.IsSignedIn = true;
            var retryThread = Environment.CurrentManagedThreadId;
            fake.Conversation.AnswerUpdated += updated =>
            {
                if (updated.Id != request.Id) return;
                if (updated.Status == QuestionStatus.Completed) completed.Set();
                else if (Environment.CurrentManagedThreadId == retryThread)
                    Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(5)));
            };
            Assert.IsTrue(fake.Conversation.Retry(request.Id));
            Assert.AreEqual(QuestionStatus.Completed, request.Status);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task WorkspaceDisplayPreferencePersistsWithoutChangingOtherLanguages()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            fake.Conversation.UpdateWorkspacePreferences(new ConversationSettings
            {
                DisplayLanguage = ConversationLanguage.English,
                Language = ConversationLanguage.Chinese,
                AnalysisLanguage = ConversationLanguage.Chinese
            });
            var settings = ConversationArchive.Capture(fake.Conversation.Session!).Restore().Settings;
            Assert.AreEqual(ConversationLanguage.English, settings.DisplayLanguage);
            Assert.AreEqual(ConversationLanguage.Chinese, settings.Language);
            Assert.AreEqual(ConversationLanguage.Chinese, settings.AnalysisLanguage);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task AnalysisWorkerRecoversWithoutPublishingIntermediateFailure()
    {
        var fake = new Harness();
        fake.Work.BlockFirst = true;
        fake.Work.AnalysisReply = """{"intentSummary":"Recovered","questions":[]}""";
        var errors = new List<string>();
        fake.Conversation.ErrorOccurred += errors.Add;
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            fake.Speech.EmitFinal("Discuss product licensing.", fake.Clock.GetUtcNow());
            var request = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => fake.Work.Calls.Count == 1);
            fake.Work.ReleaseFirst(new WorkIqAnswer("malformed", [], "bad-analysis"));
            await WaitUntil(() => request.Status is QuestionStatus.Completed or QuestionStatus.Failed);
            Assert.AreEqual(QuestionStatus.Completed, request.Status);
            Assert.AreEqual("Recovered", request.IntentSummary);
            Assert.HasCount(2, fake.Work.Calls);
            Assert.IsEmpty(errors);
            Assert.IsNull(request.Error);
            Assert.HasCount(1, fake.Conversation.Analyses);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }
}
