using System.Text.Json;
using System.Text.Json.Nodes;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Persistence;
using ConversationAssistant.Core.WorkIQ;
using ConversationAssistant_App.UI;

namespace ConversationAssistant.Tests;

public sealed partial class PipelineTests
{
    [TestMethod]
    public void ProductGuidesParseAllSectionsAndDeduplicateNamesAndAliases()
    {
        var parsed = ConversationQuestionParser.ParseAnalysis(ProductGuideReply(
            TestProductGuide("Example Suite", "ExampleSuite"), TestProductGuide(" examplesuite "),
            TestProductGuide("EXAMPLE   SUITE"), TestProductGuide("Other Product")));
        Assert.HasCount(2, parsed.Products);
        Assert.AreEqual("Example Suite", parsed.Products[0].Name);
        Assert.AreEqual("Licensing varies; current prices are not verified.", parsed.Products[0].LicensingAndPricing);
        Assert.IsNotEmpty(parsed.Products[0].Overview);
        Assert.IsNotEmpty(parsed.Products[0].SuitableScenarios);
        Assert.IsNotEmpty(parsed.Products[0].Limitations);
        Assert.IsEmpty(ConversationQuestionParser.ParseAnalysis("""{"questions":[]}""").Products);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("[null]")]
    [DataRow("[{\"name\":\"Example Suite\"}]")]
    public void MalformedProductGuidesAreNotAnEmptySuccess(string products)
    {
        Assert.ThrowsExactly<WorkIqException>(() => ConversationQuestionParser.ParseAnalysis(
            "{\"questions\":[],\"products\":" + products + "}"));
    }

    [TestMethod]
    public void ProductGuideContentAndMetadataAreBounded()
    {
        Assert.ThrowsExactly<WorkIqException>(() => ConversationQuestionParser.ParseAnalysis(ProductGuideReply(
            TestProductGuide("Example Suite") with { Overview = new string('x', ProductGuide.MaximumTextLength + 1) })));
        Assert.ThrowsExactly<WorkIqException>(() => ConversationQuestionParser.ParseAnalysis(ProductGuideReply(
            TestProductGuide("Example Suite") with { Aliases = Enumerable.Range(0, 21).Select(index => $"Alias {index}").ToArray() })));
    }

    [TestMethod]
    public async Task RepeatedProductsReuseCachedDetailsAndCardExpansionDoesNotCallWorkIq()
    {
        var fake = new Harness();
        fake.Work.AnalysisReply = ProductGuideReply(TestProductGuide("Example Suite", "ExampleSuite"));
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            fake.Speech.EmitFinal("Discuss Example Suite licensing and deployment.", fake.Clock.GetUtcNow());
            var first = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => first.Status == QuestionStatus.Completed);
            var cached = fake.Conversation.ProductGuides.Single();
            var row = new ProductGuideRow(cached, UiText.For(ConversationLanguage.English));
            row.IsExpanded = true;
            row.IsExpanded = false;
            row.IsExpanded = true;
            Assert.AreSame(cached, row.Product);
            Assert.HasCount(1, fake.Work.Calls);
            fake.Work.AnalysisReply = ProductGuideReply(TestProductGuide("EXAMPLESUITE") with { Overview = "Do not overwrite the cached description." });
            var repeated = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => repeated.Status == QuestionStatus.Completed);
            Assert.HasCount(1, fake.Conversation.ProductGuides);
            Assert.AreSame(cached, fake.Conversation.ProductGuides.Single());
            CollectionAssert.Contains(repeated.ExistingProductNames.ToArray(), "Example Suite");
            CollectionAssert.Contains(repeated.ExistingProductNames.ToArray(), "ExampleSuite");
            StringAssert.Contains(fake.Work.Calls.Last().Prompt, "DO NOT analyze again");
            Assert.HasCount(2, fake.Work.Calls);
            Assert.IsEmpty(fake.Conversation.AnswerHistory);
            fake.Speech.EmitFinal("Also consider Other Product.", fake.Clock.GetUtcNow());
            fake.Work.AnalysisReply = ProductGuideReply(TestProductGuide("Other Product"));
            var next = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => next.Status == QuestionStatus.Completed);
            Assert.HasCount(2, fake.Conversation.ProductGuides);
            Assert.AreSame(cached, fake.Conversation.ProductGuides[0]);
            Assert.HasCount(3, fake.Work.Calls);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task QueuedGroupAnalysisSeesProductsFromTheEarlierAnalysis()
    {
        var fake = new Harness();
        fake.Work.BlockFirst = true;
        fake.Work.AnalysisReply = ProductGuideReply();
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            fake.Speech.EmitFinal("Discuss Example Suite architecture.", fake.Clock.GetUtcNow());
            var first = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => fake.Work.Calls.Count == 1);
            var group = fake.Conversation.CreateTranscriptGroup();
            var queued = fake.Conversation.AnalyzeGroup(group.Id);
            Assert.AreEqual(QuestionStatus.Pending, queued.Status);
            fake.Work.ReleaseFirst(new WorkIqAnswer(ProductGuideReply(TestProductGuide("Example Suite", "ExampleSuite")), [], "product-analysis"));
            await WaitUntil(() => queued.Status == QuestionStatus.Completed);
            Assert.AreEqual(QuestionStatus.Completed, first.Status);
            CollectionAssert.Contains(queued.ExistingProductNames.ToArray(), "Example Suite");
            StringAssert.Contains(fake.Work.Calls.Last().Prompt, JsonSerializer.Serialize(queued.ExistingProductNames));
            Assert.HasCount(1, fake.Conversation.ProductGuides);
            Assert.HasCount(2, fake.Work.Calls);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task ImportedProductCacheIsReusedAndNewConversationStartsEmpty()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"product-guide-tests-{Guid.NewGuid():N}");
        var fake = new Harness(new JsonConversationArchiveStore(directory));
        fake.Work.AnalysisReply = ProductGuideReply(TestProductGuide("Example Suite", "ExampleSuite"));
        try
        {
            fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
            fake.Speech.EmitFinal("Discuss Example Suite.", fake.Clock.GetUtcNow());
            var analysis = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => analysis.Status == QuestionStatus.Completed);
            await fake.Conversation.EndConversationAsync();
            var calls = fake.Work.Calls.Count;
            await fake.Conversation.ImportConversationAsync(fake.Conversation.RecordPath!);
            Assert.HasCount(calls, fake.Work.Calls);
            Assert.HasCount(1, fake.Conversation.ProductGuides);
            fake.Work.AnalysisReply = ProductGuideReply();
            var repeated = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => repeated.Status == QuestionStatus.Completed);
            CollectionAssert.Contains(repeated.ExistingProductNames.ToArray(), "Example Suite");
            fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
            Assert.IsEmpty(fake.Conversation.ProductGuides);
        }
        finally
        {
            await fake.Conversation.EndConversationAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void ProductArchiveSnapshotsAndForksOwnTheirMetadata()
    {
        var aliases = new[] { "ExampleSuite" };
        var sources = new[] { "Verified document title" };
        var session = new ConversationSession { Settings = new ConversationSettings() };
        session.ProductGuides.Add(TestProductGuide("Example Suite") with { Aliases = aliases, Sources = sources });
        var archive = ConversationArchive.Capture(session);
        aliases[0] = "changed";
        sources[0] = "changed";
        var restored = archive.Restore();
        Assert.AreEqual("ExampleSuite", restored.ProductGuides[0].Aliases[0]);
        Assert.AreEqual("Verified document title", restored.ProductGuides[0].Sources[0]);
        ((string[])restored.ProductGuides[0].Aliases)[0] = "mutated restore";
        Assert.AreEqual("ExampleSuite", archive.Fork().Restore().ProductGuides[0].Aliases[0]);
    }

    [TestMethod]
    public async Task LegacyProductlessArchivesLoadAndDuplicateProductIdentitiesAreRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"product-guide-archive-{Guid.NewGuid():N}");
        try
        {
            var store = new JsonConversationArchiveStore(directory);
            var session = new ConversationSession { Settings = new ConversationSettings() };
            session.ProductGuides.Add(TestProductGuide("Example Suite", "ExampleSuite"));
            var path = await store.SaveAsync(ConversationArchive.Capture(session));
            var restored = (await store.LoadAsync(path)).Restore();
            Assert.AreEqual(session.ProductGuides[0].LicensingAndPricing, restored.ProductGuides[0].LicensingAndPricing);
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var guide = json["productGuides"]![0]!.DeepClone();
            json["productGuides"]!.AsArray().Add(guide);
            await File.WriteAllTextAsync(path, json.ToJsonString());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.LoadAsync(path));
            json.AsObject().Remove("productGuides");
            await File.WriteAllTextAsync(path, json.ToJsonString());
            Assert.IsEmpty((await store.LoadAsync(path)).Restore().ProductGuides);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static ProductGuide TestProductGuide(string name, params string[] aliases) => new()
    {
        Name = name,
        Aliases = aliases,
        Overview = $"{name} capabilities and integration options.",
        LicensingAndPricing = "Licensing varies; current prices are not verified.",
        SuitableScenarios = "Enterprise integration under documented deployment conditions.",
        Limitations = "Validate region, edition and quota restrictions.",
        AdditionalNotes = "Reference material, not a binding quote.",
        Sources = ["Reference document"]
    };

    private static string ProductGuideReply(params ProductGuide[] products) => JsonSerializer.Serialize(new
    {
        intentSummary = "Assess product fit.",
        stageSummary = "Discussed product requirements.",
        questions = Array.Empty<object>(),
        products
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}
