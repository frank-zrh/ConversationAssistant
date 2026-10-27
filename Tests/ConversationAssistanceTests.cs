using System.Text.Json.Nodes;
using System.Xml.Linq;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Persistence;
using ConversationAssistant.Core.WorkIQ;

namespace ConversationAssistant.Tests;

public sealed partial class PipelineTests
{
    private const string AssistanceReply = """
        {"assistance":{"conclusion":"Confirm the scope before committing.","suggestedWording":"Does that deadline include deployment?","keyPoints":["The deadline was stated; deployment scope was not."],"uncertainty":"Deployment scope is not confirmed.","sources":["Transcript turn 1"]},"questions":[{"question":"Which deployment options fit?","reason":"Compare the options after confirming scope."}]}
        """;

    [TestMethod]
    public void AssistanceParserAcceptsOptionalFieldsAndLegacyResponses()
    {
        var parsed = ConversationQuestionParser.ParseAnalysis(AssistanceReply);
        Assert.IsNotNull(parsed.Assistance);
        Assert.AreEqual("Confirm the scope before committing.", parsed.Assistance.Conclusion);
        Assert.AreEqual("Does that deadline include deployment?", parsed.Assistance.SuggestedWording);
        Assert.HasCount(1, parsed.Assistance.KeyPoints);
        Assert.AreEqual("Deployment scope is not confirmed.", parsed.Assistance.Uncertainty);
        Assert.AreEqual("Transcript turn 1", parsed.Assistance.Sources.Single());
        Assert.IsNull(ConversationQuestionParser.ParseAnalysis("""{"questions":[]}""").Assistance);
        Assert.IsNull(ConversationQuestionParser.ParseAnalysis("""{"assistance":null,"questions":[]}""").Assistance);
        var minimal = ConversationQuestionParser.ParseAnalysis(
            """{"assistance":{"conclusion":"  Confirm scope.  "},"questions":[]}""").Assistance!;
        Assert.AreEqual("Confirm scope.", minimal.Conclusion);
        Assert.AreEqual("", minimal.SuggestedWording);
        Assert.AreEqual("", minimal.Uncertainty);
        Assert.IsEmpty(minimal.KeyPoints);
        Assert.IsEmpty(minimal.Sources);
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("{}")]
    [DataRow("{\"conclusion\":null}")]
    [DataRow("{\"conclusion\":\" \"}")]
    [DataRow("{\"conclusion\":\"Scope\",\"suggestedWording\":false}")]
    [DataRow("{\"conclusion\":\"Scope\",\"uncertainty\":null}")]
    [DataRow("{\"conclusion\":\"Scope\",\"keyPoints\":null}")]
    [DataRow("{\"conclusion\":\"Scope\",\"keyPoints\":[\"\"]}")]
    [DataRow("{\"conclusion\":\"Scope\",\"sources\":[1]}")]
    [DataRow("{\"conclusion\":\"Scope\",\"sources\":\"reference\"}")]
    public void AssistanceParserRejectsMalformedPayloads(string assistance) =>
        Assert.ThrowsExactly<WorkIqException>(() => ConversationQuestionParser.ParseAnalysis(
            "{\"questions\":[],\"assistance\":" + assistance + "}"));

    [TestMethod]
    [DataRow("conclusion", ConversationAssistance.MaximumConclusionLength)]
    [DataRow("suggestedWording", ConversationAssistance.MaximumWordingLength)]
    [DataRow("uncertainty", ConversationAssistance.MaximumUncertaintyLength)]
    [DataRow("keyPoints", ConversationAssistance.MaximumPointLength)]
    [DataRow("sources", ConversationAssistance.MaximumSourceLength)]
    public void AssistanceParserEnforcesTextBounds(string field, int maximum)
    {
        var document = JsonNode.Parse(AssistanceReply)!;
        document["assistance"]![field] = field is "keyPoints" or "sources"
            ? new JsonArray(JsonValue.Create(new string('a', maximum)))
            : JsonValue.Create(new string('a', maximum));
        Assert.IsNotNull(ConversationQuestionParser.ParseAnalysis(document.ToJsonString()).Assistance);
        document["assistance"]![field] = field is "keyPoints" or "sources"
            ? new JsonArray(JsonValue.Create(new string('a', maximum + 1)))
            : JsonValue.Create(new string('a', maximum + 1));
        Assert.ThrowsExactly<WorkIqException>(() => ConversationQuestionParser.ParseAnalysis(document.ToJsonString()));
    }

    [TestMethod]
    [DataRow("keyPoints", ConversationAssistance.MaximumKeyPoints)]
    [DataRow("sources", ConversationAssistance.MaximumSources)]
    public void AssistanceParserEnforcesListBounds(string field, int maximum)
    {
        var document = JsonNode.Parse(AssistanceReply)!;
        document["assistance"]![field] = new JsonArray(Enumerable.Range(0, maximum + 1)
            .Select(index => (JsonNode?)JsonValue.Create($"Item {index}")).ToArray());
        Assert.ThrowsExactly<WorkIqException>(() => ConversationQuestionParser.ParseAnalysis(document.ToJsonString()));
    }

    [TestMethod]
    public async Task AssistanceUsesExistingAutomaticAnalysisWithoutSubmittingQuestions()
    {
        var fake = new Harness();
        fake.Work.AnalysisReply = AssistanceReply;
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            EmitFinals(fake, 7);
            await WaitUntil(() => fake.Conversation.Analysis?.Status == QuestionStatus.Completed);
            var analysis = fake.Conversation.CurrentAssistanceAnalysis;
            Assert.AreSame(fake.Conversation.Analysis, analysis);
            Assert.IsNotNull(analysis?.Assistance);
            Assert.HasCount(1, fake.Work.Calls);
            Assert.IsEmpty(fake.Conversation.AnswerHistory);
            Assert.HasCount(1, analysis.Questions);
            StringAssert.Contains(fake.Work.Calls.Single().Prompt, "No new lookup for assistance");
            StringAssert.Contains(fake.Work.Calls.Single().Prompt, "Return null if nothing useful is supported");
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task AssistanceStaysStableAcrossNewSpeechGroupAnalysisAndManualQuestions()
    {
        var fake = new Harness();
        fake.Work.AnalysisReply = AssistanceReply;
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            EmitFinals(fake, 1);
            var first = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => first.Status == QuestionStatus.Completed);
            EmitFinals(fake, 1, 2);
            Assert.AreSame(first, fake.Conversation.CurrentAssistanceAnalysis);
            Assert.HasCount(1, first.Transcript);
            Assert.HasCount(2, fake.Conversation.TranscriptSegments);
            var group = fake.Conversation.CreateTranscriptGroup();
            var grouped = fake.Conversation.AnalyzeGroup(group.Id);
            await WaitUntil(() => grouped.Status == QuestionStatus.Completed);
            Assert.AreSame(grouped, fake.Conversation.Analysis);
            Assert.AreSame(first, fake.Conversation.CurrentAssistanceAnalysis);
            var answer = fake.Conversation.AskManually("What should we verify?");
            await WaitUntil(() => answer.Status == QuestionStatus.Completed);
            Assert.AreSame(first, fake.Conversation.CurrentAssistanceAnalysis);
            Assert.HasCount(1, fake.Conversation.AnswerHistory);
            Assert.AreEqual(fake.Work.Reply, answer.Answer);
            Assert.HasCount(3, fake.Work.Calls);
            var second = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => second.Status == QuestionStatus.Completed);
            Assert.AreSame(second, fake.Conversation.CurrentAssistanceAnalysis);
            Assert.HasCount(2, second.Transcript);
            Assert.AreEqual("Confirm the scope before committing.", first.Assistance!.Conclusion);
            await fake.Conversation.EndConversationAsync();
            Assert.AreSame(second, fake.Conversation.CurrentAssistanceAnalysis);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task EmptyNewBriefRetiresPreviousBriefWithoutErasingHistory()
    {
        var fake = new Harness();
        fake.Work.AnalysisReply = AssistanceReply;
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            EmitFinals(fake, 1);
            var first = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => first.Status == QuestionStatus.Completed);
            fake.Work.AnalysisReply = """{"assistance":null,"questions":[]}""";
            EmitFinals(fake, 1, 2);
            var second = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => second.Status == QuestionStatus.Completed);
            Assert.AreSame(second, fake.Conversation.CurrentAssistanceAnalysis);
            Assert.IsNull(second.Assistance);
            Assert.IsNotNull(first.Assistance);
            Assert.HasCount(2, fake.Conversation.Analyses);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task DelayedBriefReportsOnlyItsSnapshotWhileNewSpeechRemainsUnanalyzed()
    {
        var fake = new Harness();
        fake.Work.BlockFirst = true;
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            EmitFinals(fake, 1);
            var first = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => fake.Work.Calls.Count == 1);
            EmitFinals(fake, 1, 2);
            Assert.IsNull(fake.Conversation.CurrentAssistanceAnalysis);
            fake.Work.ReleaseFirst(new WorkIqAnswer(AssistanceReply, [], null));
            await WaitUntil(() => first.Status == QuestionStatus.Completed);
            Assert.AreSame(first, fake.Conversation.CurrentAssistanceAnalysis);
            Assert.HasCount(1, first.Transcript);
            Assert.AreEqual(1, fake.Conversation.PendingAnalysisCount);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task FailedAnalysisKeepsPreviousBriefWithoutCreatingAnswers()
    {
        var fake = new Harness();
        fake.Work.AnalysisReply = AssistanceReply;
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            EmitFinals(fake, 1);
            var first = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => first.Status == QuestionStatus.Completed);
            fake.Work.Failures.Enqueue(new WorkIqException("Simulated analysis failure."));
            EmitFinals(fake, 1, 2);
            var failed = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => failed.Status == QuestionStatus.Failed);
            Assert.AreSame(first, fake.Conversation.CurrentAssistanceAnalysis);
            Assert.IsNull(failed.Assistance);
            Assert.IsEmpty(fake.Conversation.AnswerHistory);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task ClearCancelsDelayedBriefAndDoesNotResurrectPreviousContent()
    {
        var fake = new Harness();
        fake.Work.BlockFirst = true;
        fake.Work.AnalysisReply = AssistanceReply;
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            EmitFinals(fake, 1);
            var cancelled = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => fake.Work.Calls.Count == 1);
            fake.Conversation.ClearTranscript();
            fake.Work.ReleaseFirst(new WorkIqAnswer(AssistanceReply, [], null));
            Assert.IsNull(fake.Conversation.CurrentAssistanceAnalysis);
            EmitFinals(fake, 1, 2);
            Assert.IsNull(fake.Conversation.CurrentAssistanceAnalysis);
            var current = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => current.Status == QuestionStatus.Completed);
            Assert.AreEqual(QuestionStatus.Cancelled, cancelled.Status);
            Assert.IsNull(cancelled.Assistance);
            Assert.AreSame(current, fake.Conversation.CurrentAssistanceAnalysis);
            fake.Conversation.ClearTranscript();
            Assert.IsNull(fake.Conversation.CurrentAssistanceAnalysis);
            EmitFinals(fake, 1, 3);
            Assert.IsNull(fake.Conversation.CurrentAssistanceAnalysis);
            Assert.IsNotNull(current.Assistance);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task EndingAndReplacingConversationCannotPublishDelayedBrief()
    {
        var fake = new Harness();
        fake.Work.BlockFirst = true;
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            EmitFinals(fake, 1);
            var cancelled = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => fake.Work.Calls.Count == 1);
            await fake.Conversation.EndConversationAsync();
            fake.Work.ReleaseFirst(new WorkIqAnswer(AssistanceReply, [], null));
            Assert.AreEqual(QuestionStatus.Cancelled, cancelled.Status);
            Assert.IsNull(fake.Conversation.CurrentAssistanceAnalysis);
            fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
            EmitFinals(fake, 1);
            Assert.IsNull(fake.Conversation.CurrentAssistanceAnalysis);
            Assert.IsEmpty(fake.Conversation.Analyses);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task AssistanceArchiveRoundTripIsDetachedAndImportDoesNotSubmitRequests()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"conversation-assistance-{Guid.NewGuid():N}");
        var store = new JsonConversationArchiveStore(directory);
        var fake = new Harness(store);
        fake.Work.AnalysisReply = AssistanceReply;
        try
        {
            fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
            EmitFinals(fake, 1);
            var analysis = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => analysis.Status == QuestionStatus.Completed);
            await fake.Conversation.EndConversationAsync();
            var path = fake.Conversation.RecordPath!;
            var archive = await store.LoadAsync(path);
            var first = archive.Restore().Analyses.Single().Assistance!;
            var forked = archive.Fork().Restore().Analyses.Single().Assistance!;
            CollectionAssert.AreEqual(first.KeyPoints.ToArray(), forked.KeyPoints.ToArray());
            CollectionAssert.AreEqual(first.Sources.ToArray(), forked.Sources.ToArray());
            ((string[])first.KeyPoints)[0] = "Changed outside archive";
            ((string[])first.Sources)[0] = "Changed outside archive";
            Assert.AreNotEqual(first.KeyPoints[0], forked.KeyPoints[0]);
            Assert.AreNotEqual(first.Sources[0], archive.Restore().Analyses.Single().Assistance!.Sources[0]);
            var calls = fake.Work.Calls.Count;
            await fake.Conversation.ImportConversationAsync(path);
            Assert.HasCount(calls, fake.Work.Calls);
            Assert.IsNull(fake.Conversation.Session);
            Assert.IsFalse(fake.Conversation.IsListening);
            Assert.AreEqual(analysis.Assistance!.Conclusion,
                fake.Conversation.CurrentAssistanceAnalysis!.Assistance!.Conclusion);
            Assert.AreEqual(analysis.Assistance.SuggestedWording, forked.SuggestedWording);
            Assert.AreEqual(analysis.Assistance.Uncertainty, forked.Uncertainty);
        }
        finally
        {
            await fake.Conversation.EndConversationAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    [DataRow("legacy")]
    [DataRow("oversized")]
    [DataRow("nullPoints")]
    [DataRow("emptyConclusion")]
    public async Task AssistanceArchivesAcceptLegacyFieldsAndRejectInvalidPayloads(string variation)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"conversation-assistance-validation-{Guid.NewGuid():N}");
        var store = new JsonConversationArchiveStore(directory);
        var fake = new Harness(store);
        fake.Work.AnalysisReply = AssistanceReply;
        try
        {
            fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
            EmitFinals(fake, 1);
            var analysis = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => analysis.Status == QuestionStatus.Completed);
            await fake.Conversation.EndConversationAsync();
            var path = fake.Conversation.RecordPath!;
            var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var saved = document["analyses"]![0]!;
            switch (variation)
            {
                case "legacy": saved.AsObject().Remove("assistance"); break;
                case "oversized": saved["assistance"]!["conclusion"] = new string('x', ConversationAssistance.MaximumConclusionLength + 1); break;
                case "nullPoints": saved["assistance"]!["keyPoints"] = null; break;
                case "emptyConclusion": saved["assistance"]!["conclusion"] = " "; break;
            }
            await File.WriteAllTextAsync(path, document.ToJsonString());
            if (variation == "legacy")
                Assert.IsNull((await store.LoadAsync(path)).Restore().Analyses.Single().Assistance);
            else
                await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(path));
        }
        finally
        {
            await fake.Conversation.EndConversationAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}

[TestClass]
public sealed class ConversationAssistancePresentationTests
{
    [TestMethod]
    public void AssistanceAndIntentShareFirstTabWithDirectAnalysisNavigation()
    {
        var page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainPage.xaml"));
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => page.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        var card = Named("IntentDocument");
        var tabs = Named("AnalysisTabs");
        var first = tabs.Elements(ui + "TabViewItem").First();
        Assert.AreEqual("{Binding Texts[IntentTab]}", (string?)first.Attribute("Header"));
        Assert.IsTrue(card.Ancestors().Contains(first));
        Assert.AreEqual("SelectableAnalysisView", card.Name.LocalName);
        Assert.AreEqual("{Binding IntentDocumentSections}", (string?)card.Attribute("Sections"));
        Assert.IsFalse(tabs.Ancestors().Any(element => element.Name.LocalName is "Expander" or "ScrollViewer"));
        Assert.IsNull(tabs.Attribute("Height"));
        Assert.AreEqual("2", (string?)tabs.Attribute("Grid.Row"));
        Assert.AreEqual(tabs.Parent, Named("AnalysisHistoryPicker").Parent);
        Assert.IsFalse(Named("AnalysisHistoryPicker").Ancestors().Any(element => element.Name.LocalName == "Expander"));
        Assert.IsFalse(Named("AnswerHistoryPicker").Ancestors().Contains(tabs));
        Assert.IsFalse(page.Descendants().Any(element => (string?)element.Attribute(x + "Name") == "AnalysisDetails"));
        Assert.IsFalse(card.DescendantsAndSelf().Attributes().Any(attribute =>
            attribute.Name.LocalName is "Click" or "Tapped" or "Loaded"));
        Assert.HasCount(1, first.Descendants().Where(element => element.Name.LocalName == "SelectableAnalysisView"));
        Assert.AreEqual("{Binding StageDocumentSections}", (string?)Named("StageSummaryDocument").Attribute("Sections"));
    }

    [TestMethod]
    public void AnalysisTabHeadersUseNativeLocalizedContentAndCompactClickTargets()
    {
        var page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainPage.xaml"));
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var tabs = page.Descendants(ui + "TabView").Single(element => (string?)element.Attribute(x + "Name") == "AnalysisTabs");
        var resources = tabs.Element(ui + "TabView.Resources")!;
        var style = resources.Elements(ui + "Style").Single(element => (string?)element.Attribute("TargetType") == "TabViewItem");
        Assert.AreEqual("36", (string?)style.Elements(ui + "Setter").Single(element =>
            (string?)element.Attribute("Property") == "MinHeight").Attribute("Value"));
        Assert.AreEqual("36", resources.Elements(x + "Double").Single(element =>
            (string?)element.Attribute(x + "Key") == "TabViewItemMinHeight").Value);
        Assert.AreEqual("13", resources.Elements(x + "Double").Single(element =>
            (string?)element.Attribute(x + "Key") == "TabViewItemHeaderFontSize").Value);
        Assert.AreEqual("SemiBold", (string?)style.Elements(ui + "Setter").Single(element =>
            (string?)element.Attribute("Property") == "FontWeight").Attribute("Value"));
        Assert.IsFalse(style.Elements(ui + "Setter").Any(element =>
            (string?)element.Attribute("Property") == "HeaderTemplate"));
        Assert.IsFalse(tabs.Elements(ui + "TabViewItem").Any(tab =>
            tab.Attribute("HeaderTemplate") is not null || tab.Element(ui + "TabViewItem.HeaderTemplate") is not null));
        foreach (var key in new[] { "TabViewItemHeaderPadding", "TabViewSelectedItemHeaderPadding", "TabViewItemHeaderPaddingWithoutCloseButton" })
            Assert.AreEqual("10,6,10,6", resources.Elements(ui + "Thickness").Single(element =>
                (string?)element.Attribute(x + "Key") == key).Value);
        Assert.AreEqual("SizeToContent", (string?)tabs.Attribute("TabWidthMode"));
        Assert.AreEqual("0", (string?)tabs.Attribute("SelectedIndex"));
        var headerKeys = new[] { "IntentTab", "StageSummariesTab", "RecommendedQuestionsTab", "ProductGuideTab" };
        CollectionAssert.AreEqual(headerKeys.Select(key => $"{{Binding Texts[{key}]}}").ToArray(),
            tabs.Elements(ui + "TabViewItem").Select(tab => (string?)tab.Attribute("Header")).ToArray());
        foreach (var language in new[] { ConversationLanguage.English, ConversationLanguage.Chinese })
        {
            var texts = UiText.For(language);
            foreach (var key in headerKeys) Assert.IsFalse(string.IsNullOrWhiteSpace(texts[key]));
            StringAssert.Contains(texts["IntentTab"], language == ConversationLanguage.English ? "Useful now" : "此刻有用");
            StringAssert.Contains(texts["IntentTab"], language == ConversationLanguage.English ? "Intent" : "意图");
            Assert.IsTrue(texts.ContainsKey("IntentSummaryHeader"));
        }
    }

    [TestMethod]
    public void AssistanceCoverageIsLocalizedWithoutClaimingNewSpeechWasAnalyzed()
    {
        foreach (var language in new[] { ConversationLanguage.English, ConversationLanguage.Chinese })
        {
            var texts = UiText.For(language);
            foreach (var key in new[] { "AssistanceCurrentCoverage", "AssistancePendingCoverage", "AssistanceSavedCoverage" })
            {
                var coverage = texts.Format(key, "09:30:00", 2, 3);
                StringAssert.Contains(coverage, "09:30:00");
                StringAssert.Contains(coverage, "2/3");
                Assert.IsFalse(coverage.Contains("{0}", StringComparison.Ordinal));
            }
            Assert.AreNotEqual(texts["AssistanceCurrentCoverage"], texts["AssistancePendingCoverage"]);
            Assert.AreNotEqual(texts["AssistanceCurrentCoverage"], texts["AssistanceSavedCoverage"]);
        }
    }
}
