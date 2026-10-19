using System.Text.Json;
using System.Text.Json.Nodes;
using ConversationAssistant.Core.Context;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Persistence;
using ConversationAssistant.Core.WorkIQ;

namespace ConversationAssistant.Tests;

public sealed partial class PipelineTests
{
    private const string FeatureQuestion =
        "For the Example Suite external-user workflow, which documented base-plan/add-on combinations cover access, and which quota or deployment conditions change eligibility?";
    private const string FeatureReason =
        "Cross-check entitlement dependencies and service boundaries to avoid promising an unsupported workflow.";
    private const string PricingQuestion =
        "For Example Suite's variable monthly workload, how do eligible licensing options compare after allowances, overages and commitment terms, using applicable dated rates or a symbolic cost model?";
    private const string PricingReason =
        "Reconcile charging units and conditional costs that are easy to miss when preparing a customer estimate.";

    [TestMethod]
    [DataRow(-1, false)]
    [DataRow(0, false)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    [DataRow(3, true)]
    [DataRow(4, true)]
    [DataRow(5, true)]
    [DataRow(6, false)]
    public void ProductGuidanceOnlyReusesCompatibleKnowledgeVersions(int version, bool compatible)
    {
        Assert.AreEqual(compatible, ConversationAnalysisPrompt.CanReuseQuestionsFrom(version));
    }

    [TestMethod]
    [DataRow(ConversationLanguage.Chinese)]
    [DataRow(ConversationLanguage.English)]
    [DataRow(ConversationLanguage.Auto)]
    public void ProductAndPricingRepliesRequireExpertDetailVerifiedTermsAndTransparentCalculations(
        ConversationLanguage language)
    {
        var settings = new ConversationSettings { AnswerLanguage = language, AnswerStyle = AnswerStyle.Concise };
        var builder = new ContextBuilder(new PromptBuilder());
        foreach (var question in new[] { FeatureQuestion, PricingQuestion })
        {
            var prompt = builder.BuildSuggestedQuestion(question, "Example Suite discussion.", settings);
            StringAssert.Contains(prompt, question);
            StringAssert.Contains(prompt, "ADVANCED PRODUCT AND PRICING EVIDENCE:");
            StringAssert.Contains(prompt, "do not force pricing into unrelated knowledge or creative answers");
            StringAssert.Contains(prompt, "exact edition/SKU/version");
            StringAssert.Contains(prompt, "Distinguish licensed entitlement from technical service limits");
            StringAssert.Contains(prompt, "source and effective/as-of date, currency/market");
            StringAssert.Contains(prompt, "charging unit, billing cadence");
            StringAssert.Contains(prompt, "commitment term");
            StringAssert.Contains(prompt, "minimums, included allowances, overages, mandatory");
            StringAssert.Contains(prompt, "add-ons, taxes, discounts and exclusions");
            StringAssert.Contains(prompt, "Distinguish list prices, conditional estimates and negotiated quotes");
            StringAssert.Contains(prompt, "Do not present remembered or outdated prices as current");
            StringAssert.Contains(prompt, "amounts mentioned in speech as unverified inputs");
            StringAssert.Contains(prompt, "symbolic");
            StringAssert.Contains(prompt, "not an invented numeric quote or a zero-cost assumption");
            StringAssert.Contains(prompt, "quantities, units, the formula, allowance/overage or tier logic");
            StringAssert.Contains(prompt, "comparable time periods");
            StringAssert.Contains(prompt, "another customer's");
            StringAssert.Contains(prompt, "Do not issue binding quotes, promise discounts");
            StringAssert.Contains(prompt, "Even a concise reply must retain material eligibility and pricing caveats");
            StringAssert.Contains(prompt, "brief enough to say aloud");
            StringAssert.Contains(prompt, "Do not send messages or create, update or delete");
            Assert.IsLessThan(WorkIqPromptSender.MaxPartCharacters, prompt.Length);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProductQuestionsExtendVersionOneKnowledgeHistoryWithoutReplacingSavedReplies(bool groupScope)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"conversation-product-guidance-{Guid.NewGuid():N}");
        var store = new JsonConversationArchiveStore(directory);
        var fake = new Harness(store);
        fake.Work.AnalysisReply = KnowledgeDiscovery;
        try
        {
            fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
            fake.Speech.EmitFinal("Example Suite needs data-residency controls for external users and variable usage.",
                fake.Clock.GetUtcNow());
            var group = groupScope ? fake.Conversation.CreateTranscriptGroup() : null;
            if (group is not null)
                fake.Speech.EmitFinal("Unrelated lunch plans.", fake.Clock.GetUtcNow());
            ConversationAnalysisRequest Analyze() => group is null
                ? fake.Conversation.AnalyzeConversation() : fake.Conversation.AnalyzeGroup(group.Id);
            var first = Analyze();
            await WaitUntil(() => first.Status == QuestionStatus.Completed);
            var originalQuestion = first.Questions.Single();
            var originalAnswer = fake.Conversation.AskSuggestedQuestion(originalQuestion.Id);
            await WaitUntil(() => originalAnswer.Status == QuestionStatus.Completed);
            await fake.Conversation.EndConversationAsync();
            var source = JsonNode.Parse(await File.ReadAllTextAsync(fake.Conversation.RecordPath!))!;
            source["analyses"]![0]!["guidanceVersion"] = 1;
            var sourcePath = Path.Combine(directory, "knowledge-v1.json");
            await File.WriteAllTextAsync(sourcePath, source.ToJsonString());
            var originalBytes = await File.ReadAllBytesAsync(sourcePath);
            var calls = fake.Work.Calls.Count;
            await fake.Conversation.ImportConversationAsync(sourcePath);
            Assert.HasCount(calls, fake.Work.Calls);
            var restoredFirst = fake.Conversation.Analyses.Single();
            Assert.AreEqual(1, restoredFirst.GuidanceVersion);
            fake.Work.AnalysisReply = JsonSerializer.Serialize(new
            {
                questions = new[]
                {
                    new { question = KnowledgeQuestion, reason = KnowledgeReason },
                    new { question = FeatureQuestion, reason = FeatureReason },
                    new { question = PricingQuestion, reason = PricingReason }
                }
            });
            var current = Analyze();
            await WaitUntil(() => current.Status == QuestionStatus.Completed);
            Assert.AreEqual(ConversationAnalysisPrompt.CurrentGuidanceVersion, current.GuidanceVersion);
            Assert.AreEqual(KnowledgeQuestion, current.ExistingQuestions.Single());
            Assert.HasCount(3, current.Questions);
            Assert.AreEqual(2, current.AddedQuestionCount);
            Assert.AreSame(restoredFirst.Questions.Single(), current.Questions[0]);
            Assert.AreEqual(originalQuestion.Id, current.Questions[0].Id);
            Assert.AreEqual(FeatureQuestion, current.Questions[1].Question);
            Assert.AreEqual(PricingQuestion, current.Questions[2].Question);
            Assert.AreEqual(PricingReason, current.Questions[2].Reason);
            Assert.HasCount(1, restoredFirst.Questions);
            Assert.HasCount(1, fake.Conversation.AnswerHistory);
            Assert.AreEqual(originalAnswer.Answer, fake.Conversation.AnswerHistory.Single().Answer);
            Assert.HasCount(calls + 1, fake.Work.Calls);
            AssertAdvancedProductDiscoveryInstructions(fake.Work.Calls.Last().Prompt);
            if (group is not null)
                Assert.IsFalse(AnalysisTranscript(fake.Work.Calls.Last().Prompt).Contains("lunch", StringComparison.Ordinal));

            var pricingAnswer = fake.Conversation.AskSuggestedQuestion(current.Questions[2].Id);
            await WaitUntil(() => pricingAnswer.Status == QuestionStatus.Completed);
            StringAssert.Contains(fake.Work.Calls.Last().Prompt, "ADVANCED PRODUCT AND PRICING EVIDENCE:");
            StringAssert.Contains(fake.Work.Calls.Last().Prompt, PricingQuestion);
            Assert.AreEqual(current.Questions[2].ContextUsed, pricingAnswer.ContextUsed);
            Assert.AreSame(pricingAnswer, fake.Conversation.AskSuggestedQuestion(current.Questions[2].Id));
            Assert.HasCount(calls + 2, fake.Work.Calls);
            await fake.Conversation.SaveConversationAsync();
            var saved = (await store.LoadAsync(fake.Conversation.RecordPath!)).Restore();
            CollectionAssert.AreEqual(new[] { 1, ConversationAnalysisPrompt.CurrentGuidanceVersion },
                saved.Analyses.Select(item => item.GuidanceVersion).ToArray());
            Assert.AreEqual(originalQuestion.Id, saved.Analyses[1].Questions[0].Id);
            Assert.AreEqual(pricingAnswer.Id, saved.Answers.Last().Id);
            CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(sourcePath));
            Assert.IsFalse(fake.Conversation.IsListening);
            Assert.AreEqual(1, fake.Audio.StartCount);
        }
        finally
        {
            await fake.Conversation.EndConversationAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProductSuggestionTargetDoesNotPadEmptyOrShortQualifiedResults(bool hasQualifiedQuestion)
    {
        var fake = new Harness();
        fake.Work.AnalysisReply = JsonSerializer.Serialize(new
        {
            questions = hasQualifiedQuestion
                ? new[] { new { question = FeatureQuestion, reason = FeatureReason } }
                : []
        });
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            fake.Speech.EmitFinal(hasQualifiedQuestion ? "Example Suite must support the external-user workflow."
                : "Thanks, let's talk again next week.", fake.Clock.GetUtcNow());
            var analysis = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => analysis.Status == QuestionStatus.Completed);
            Assert.HasCount(hasQualifiedQuestion ? 1 : 0, analysis.Questions);
            Assert.HasCount(1, fake.Work.Calls);
            Assert.IsEmpty(fake.Conversation.AnswerHistory);
            AssertAdvancedProductDiscoveryInstructions(fake.Work.Calls.Single().Prompt);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    private static void AssertAdvancedProductDiscoveryInstructions(string prompt)
    {
        StringAssert.Contains(prompt, "ADDITIONAL ADVANCED PRODUCT AND PRICING QUESTIONS:");
        StringAssert.Contains(prompt, "Append 2-4 NEW");
        StringAssert.Contains(prompt, "SAME questions array");
        StringAssert.Contains(prompt, "Include distinct feature/capability and pricing/licensing gaps");
        StringAssert.Contains(prompt, "Preserve useful general/creative questions");
        StringAssert.Contains(prompt, "implicit cost implications qualify");
        StringAssert.Contains(prompt, "Every question needs a transcript-supported product/use case");
        StringAssert.Contains(prompt, "Never pad, duplicate");
        StringAssert.Contains(prompt, "this is not a quota");
        StringAssert.Contains(prompt, "DIFFICULTY MEANS EXPERT DETAIL, NOT DIFFICULT WORDING:");
        StringAssert.Contains(prompt, "authoritative reference, cross-condition reasoning");
        StringAssert.Contains(prompt, "multi-step comparison/calculation");
        StringAssert.Contains(prompt, "SKU/version differences");
        StringAssert.Contains(prompt, "entitlement versus technical limits");
        StringAssert.Contains(prompt, "charging units, allowances/overages");
        StringAssert.Contains(prompt, "total-cost/break-even comparisons");
        StringAssert.Contains(prompt, "Ask WHICH conditions apply; do not assume");
        StringAssert.Contains(prompt, "hard-to-remember");
        StringAssert.Contains(prompt, "risk of a wrong promise/quote");
        StringAssert.Contains(prompt, "never invented product facts");
        StringAssert.Contains(prompt, "Each question asks ONE hard thing in ONE short sentence");
        StringAssert.Contains(prompt, "not the clickable question");
        StringAssert.Contains(prompt, "licensingAndPricing MUST be a compact Markdown pipe table");
        StringAssert.Contains(prompt, "limitations MUST be a compact Markdown pipe table");
    }
}
