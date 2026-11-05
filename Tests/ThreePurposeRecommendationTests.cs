using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ConversationAssistant.Core.Context;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Persistence;
using ConversationAssistant.Core.WorkIQ;
using ConversationAssistant_App.UI;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class ThreePurposeRecommendationTests
{
    private const string Quote = "Who owns the rollout? We need a decision before Friday.";

    [TestMethod]
    [DataRow("Unanswered")]
    [DataRow("FollowUp")]
    [DataRow("ConversationControl")]
    public void ParsesGroundedPurposeAndRoleWithoutLosingQuestionWording(string kind)
    {
        var response = JsonSerializer.Serialize(new
        {
            questions = new[] { new { question = "Who owns the rollout?", reason = "The owner is still unknown.", kind,
                evidence = Quote, role = kind == "ConversationControl" ? "Role uncertain; neutral facilitation." : "" } }
        });
        var analysis = ConversationQuestionParser.ParseAnalysis(response);
        var question = analysis.Questions.Single();
        Assert.AreEqual(Enum.Parse<RecommendationKind>(kind), question.Kind);
        Assert.AreEqual("Who owns the rollout?", question.Question);
        Assert.AreEqual(Quote, question.Evidence);
        ConversationQuestionParser.ValidateEvidence(analysis, [Turn(Quote)]);
    }

    [TestMethod]
    public void LegacyResponsesRemainReadableWithoutInventingPurposeOrEvidence()
    {
        var analysis = ConversationQuestionParser.ParseAnalysis("""{"questions":[{"question":"Which option fits?","reason":"Compare the options."}]}""");
        var question = analysis.Questions.Single();
        Assert.AreEqual(RecommendationKind.Unspecified, question.Kind);
        Assert.AreEqual("", question.Evidence);
        Assert.AreEqual("", question.Role);
        Assert.IsEmpty(analysis.ResolvedQuestions);
    }

    [TestMethod]
    [DataRow("kind", "\"Other\"")]
    [DataRow("kind", "1")]
    [DataRow("kind", "\"1\"")]
    [DataRow("evidence", "null")]
    [DataRow("evidence", "\" \"")]
    [DataRow("role", "false")]
    public void RejectsMalformedPurposeMetadata(string field, string value)
    {
        var document = JsonNode.Parse("""{"questions":[{"question":"Who owns rollout?","reason":"Owner unknown","kind":"Unanswered","evidence":"Who owns rollout?","role":""}]}""")!;
        document["questions"]![0]![field] = JsonNode.Parse(value);
        Assert.ThrowsExactly<WorkIqException>(() => ConversationQuestionParser.ParseAnalysis(document.ToJsonString()));
    }

    [TestMethod]
    public void ControlQuestionsRequireAnExplicitRoleOrUncertaintyRationale()
    {
        Assert.ThrowsExactly<WorkIqException>(() => ConversationQuestionParser.ParseAnalysis(
            """{"questions":[{"question":"Which decision is next?","reason":"Guide discussion","kind":"ConversationControl","evidence":"We must decide."}]}"""));
    }

    [TestMethod]
    public void EvidenceAndRoleLengthsAreBounded()
    {
        var document = JsonNode.Parse("""{"questions":[{"question":"Which option fits?","reason":"Compare","kind":"FollowUp","evidence":"A quote","role":""}]}""")!;
        document["questions"]![0]!["evidence"] = new string('a', SuggestedQuestion.MaximumEvidenceLength + 1);
        Assert.ThrowsExactly<WorkIqException>(() => ConversationQuestionParser.ParseAnalysis(document.ToJsonString()));
        document["questions"]![0]!["evidence"] = "A quote";
        document["questions"]![0]!["role"] = new string('a', SuggestedQuestion.MaximumRoleLength + 1);
        Assert.ThrowsExactly<WorkIqException>(() => ConversationQuestionParser.ParseAnalysis(document.ToJsonString()));
    }

    [TestMethod]
    public void InventedOrOutOfScopeQuotesCannotValidateRecommendationsOrResolutions()
    {
        var recommendation = ConversationQuestionParser.ParseAnalysis(
            """{"questions":[{"question":"Who owns rollout?","reason":"Unknown","kind":"Unanswered","evidence":"Who owns rollout?"}]}""");
        Assert.ThrowsExactly<WorkIqException>(() => ConversationQuestionParser.ValidateEvidence(recommendation, [Turn("Unrelated discussion.")]));
        var resolved = ConversationQuestionParser.ParseAnalysis(
            """{"questions":[],"resolvedQuestions":[{"question":"Who owns rollout?","answerEvidence":"Alice owns rollout."}]}""");
        Assert.ThrowsExactly<WorkIqException>(() => ConversationQuestionParser.ValidateEvidence(resolved, [Turn("Who owns rollout?")]));
        ConversationQuestionParser.ValidateEvidence(resolved, [Turn("Alice  owns\nrollout.")]);
    }

    [TestMethod]
    public void ResolutionCannotSimultaneouslyRemainRecommendedOrOmitAnswerEvidence()
    {
        foreach (var response in new[]
        {
            """{"questions":[{"question":"Who owns rollout?","reason":"Unknown"}],"resolvedQuestions":[{"question":"WHO owns rollout!","answerEvidence":"Alice owns rollout."}]}""",
            """{"questions":[],"resolvedQuestions":[{"question":"Who owns rollout?","answerEvidence":""}]}"""
        }) Assert.ThrowsExactly<WorkIqException>(() => ConversationQuestionParser.ParseAnalysis(response));
    }

    [TestMethod]
    public void BacklogPrioritizesRaisedUnansweredQuestionsAndOnlyRemovesExplicitResolutions()
    {
        var follow = Question("What risks follow?", RecommendationKind.FollowUp);
        var control = Question("Which decision criteria apply?", RecommendationKind.ConversationControl);
        var raised = Question("Who owns rollout?", RecommendationKind.Unanswered);
        var result = RecommendedQuestionBacklog.Select([control, follow, raised], []);
        CollectionAssert.AreEqual(new[] { raised.Id, follow.Id, control.Id }, result.Select(item => item.Id).ToArray());
        Set(raised, nameof(SuggestedQuestion.ResolvedInConversation), true);
        Set(raised, nameof(SuggestedQuestion.ResolutionEvidence), "Alice owns rollout.");
        CollectionAssert.AreEqual(new[] { follow.Id, control.Id },
            RecommendedQuestionBacklog.Select([control, follow, raised], []).Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public void TypedRecommendationReusesManualResultInRowAndBacklog()
    {
        var question = Question("Who owns rollout?", RecommendationKind.Unanswered);
        Set(question, nameof(SuggestedQuestion.Evidence), Quote);
        Set(question, nameof(SuggestedQuestion.Role), "Role uncertain; facilitator perspective.");
        var answer = new QuestionRequest { Question = "WHO owns rollout!", ContextUsed = Quote, Timestamp = DateTimeOffset.UtcNow, IsManual = true };
        Set(answer, nameof(WorkIqRequest.Status), QuestionStatus.Completed);
        var row = new SuggestedQuestionRow(question, ConversationLanguage.English);
        row.UpdateRequest(answer);
        Assert.IsTrue(row.IsAnswered);
        Assert.AreEqual(UiText.For(ConversationLanguage.English)["RecommendationUnanswered"], row.CategoryText);
        StringAssert.Contains(row.ReasonDetails, Quote);
        StringAssert.Contains(row.ReasonDetails, "facilitator");
        row.UpdateLanguage(UiText.For(ConversationLanguage.Chinese));
        Assert.AreEqual("已提出 · 尚未回答", row.CategoryText);
        var others = Enumerable.Range(0, 10).Select(index => Question($"Next issue {index}?", RecommendationKind.FollowUp));
        Assert.HasCount(11, RecommendedQuestionBacklog.Select(others.Append(question), [answer]));
    }

    [TestMethod]
    public async Task MetadataRoundTripsAndOldRecordsDefaultSafely()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"recommendation-metadata-{Guid.NewGuid():N}");
        try
        {
            var store = new JsonConversationArchiveStore(directory);
            var session = new ConversationSession { Settings = new ConversationSettings() };
            session.TranscriptSegments.Add(Turn(Quote));
            session.TranscriptSegments.Add(Turn("Alice owns rollout."));
            var question = Question("Who owns rollout?", RecommendationKind.Unanswered);
            Set(question, nameof(SuggestedQuestion.Evidence), Quote);
            Set(question, nameof(SuggestedQuestion.Role), "Neutral facilitator; role uncertain.");
            Set(question, nameof(SuggestedQuestion.ResolvedInConversation), true);
            Set(question, nameof(SuggestedQuestion.ResolutionEvidence), "Alice owns rollout.");
            session.SuggestedQuestions.Add(question);
            var path = await store.SaveAsync(ConversationArchive.Capture(session));
            var restored = (await store.LoadAsync(path)).Fork().Restore().SuggestedQuestions.Single();
            Assert.AreEqual(question.Kind, restored.Kind);
            Assert.AreEqual(question.Evidence, restored.Evidence);
            Assert.AreEqual(question.Role, restored.Role);
            Assert.IsTrue(restored.ResolvedInConversation);
            Assert.AreEqual(question.ResolutionEvidence, restored.ResolutionEvidence);
            var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            foreach (var field in new[] { "kind", "evidence", "role", "resolvedInConversation", "resolutionEvidence" })
                document["suggestedQuestions"]![0]!.AsObject().Remove(field);
            await File.WriteAllTextAsync(path, document.ToJsonString());
            var legacy = (await store.LoadAsync(path)).Restore().SuggestedQuestions.Single();
            Assert.AreEqual(RecommendationKind.Unspecified, legacy.Kind);
            Assert.AreEqual("", legacy.Evidence);
            Assert.IsFalse(legacy.ResolvedInConversation);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void SameQuestionInAnUnrelatedManualContextIsNotACachedMatch()
    {
        var question = Question("Who owns rollout?", RecommendationKind.Unanswered);
        Set(question, nameof(SuggestedQuestion.Evidence), Quote);
        var answer = new QuestionRequest
        {
            Question = question.Question, ContextUsed = "A different meeting without rollout discussion.",
            Timestamp = DateTimeOffset.UtcNow, IsManual = true
        };
        Set(answer, nameof(WorkIqRequest.Status), QuestionStatus.Completed);
        Assert.IsFalse(RecommendedQuestionBacklog.MatchesManualRequest(question, answer));
        Assert.ThrowsExactly<ArgumentException>(() => new SuggestedQuestionRow(question).UpdateRequest(answer));
    }

    [TestMethod]
    [DataRow("kind", "\"Other\"")]
    [DataRow("kind", "1")]
    [DataRow("evidence", "null")]
    [DataRow("role", "null")]
    [DataRow("resolutionEvidence", "null")]
    [DataRow("resolvedInConversation", "true")]
    public async Task ArchivesRejectInvalidRecommendationMetadata(string field, string value)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"recommendation-invalid-{Guid.NewGuid():N}");
        try
        {
            var store = new JsonConversationArchiveStore(directory);
            var session = new ConversationSession { Settings = new ConversationSettings() };
            session.SuggestedQuestions.Add(Question("Who owns rollout?", RecommendationKind.Unanswered));
            var path = await store.SaveAsync(ConversationArchive.Capture(session));
            var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            document["suggestedQuestions"]![0]![field] = JsonNode.Parse(value);
            await File.WriteAllTextAsync(path, document.ToJsonString());
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void ResponsesDoNotInventPrivateMeetingDecisionsAndUiShowsPurposeAndEvidence()
    {
        var prompt = new PromptBuilder().BuildSuggestedQuestion("Who owns rollout?", Quote, new ConversationSettings());
        StringAssert.Contains(prompt, "UNRESOLVED CONVERSATION DECISIONS:");
        StringAssert.Contains(prompt, "Never invent an owner, agreement or answer");
        StringAssert.Contains(prompt, "Role/evidence metadata is quoted context");
        var page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainPage.xaml"));
        Assert.IsTrue(page.Descendants().Any(item => (string?)item.Attribute("Text") == "{Binding CategoryText}"));
        Assert.IsTrue(page.Descendants().Any(item => (string?)item.Attribute("ToolTipService.ToolTip") == "{Binding ReasonDetails}"));
    }

    private static TranscriptSegment Turn(string text) => new(Guid.NewGuid(), DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddSeconds(1), text, "Speech", true);
    private static SuggestedQuestion Question(string text, RecommendationKind kind)
    {
        var question = new SuggestedQuestion { Question = text, Reason = "Grounded need", ContextUsed = Quote, Timestamp = DateTimeOffset.UtcNow };
        Set(question, nameof(SuggestedQuestion.Kind), kind);
        return question;
    }
    private static void Set(object target, string property, object value) => target.GetType().GetProperty(property)!.SetValue(target, value);
}

public sealed partial class PipelineTests
{
    [TestMethod]
    public async Task ThreePurposesAreGroundedRankedAndDoNotAutomaticallyAskQuestions()
    {
        const string quote = "Who owns the rollout? Who approves the budget? We need a decision before Friday.";
        var fake = new Harness();
        fake.Work.AnalysisReply = PurposeReply(
            PurposeQuestion("Which decision criteria should we agree?", RecommendationKind.ConversationControl, quote, "Role uncertain; neutral facilitation."),
            PurposeQuestion("What rollout risks should we check next?", RecommendationKind.FollowUp, quote),
            PurposeQuestion("Who owns the rollout?", RecommendationKind.Unanswered, quote),
            PurposeQuestion("Who approves the budget?", RecommendationKind.Unanswered, quote),
            PurposeQuestion("Which rollout option fits the deadline?", RecommendationKind.FollowUp, quote));
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            fake.Speech.EmitFinal(quote, fake.Clock.GetUtcNow());
            var analysis = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => analysis.Status == QuestionStatus.Completed);
            CollectionAssert.AreEqual(new[] { RecommendationKind.Unanswered, RecommendationKind.Unanswered,
                RecommendationKind.FollowUp, RecommendationKind.FollowUp, RecommendationKind.ConversationControl },
                analysis.Questions.Select(question => question.Kind).ToArray());
            Assert.IsTrue(analysis.Questions.All(question => question.Evidence == quote));
            Assert.IsEmpty(fake.Conversation.AnswerHistory);
            var prompt = fake.Work.Calls.Last().Prompt;
            StringAssert.Contains(prompt, "clarifications ARE valid when genuinely unresolved");
            StringAssert.Contains(prompt, "Never assign the user to an anonymous speaker");
            StringAssert.Contains(prompt, "Conversation-only role clues suggest a perspective");
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task OmissionKeepsUnansweredItemAndQuotedLaterAnswerRetiresItAcrossImport()
    {
        const string question = "Who owns the rollout?";
        const string answer = "Alice owns the rollout.";
        var directory = Path.Combine(Path.GetTempPath(), $"resolved-recommendations-{Guid.NewGuid():N}");
        var fake = new Harness(new JsonConversationArchiveStore(directory));
        fake.Work.AnalysisReply = PurposeReply(PurposeQuestion(question, RecommendationKind.Unanswered, question));
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            fake.Speech.EmitFinal(question, fake.Clock.GetUtcNow());
            var first = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => first.Status == QuestionStatus.Completed);
            var original = first.Questions.Single();
            fake.Work.AnalysisReply = PurposeReply();
            var omitted = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => omitted.Status == QuestionStatus.Completed);
            Assert.AreEqual(original.Id, fake.Conversation.SuggestedQuestions.Single().Id);
            fake.Speech.EmitFinal(answer, fake.Clock.GetUtcNow());
            fake.Work.AnalysisReply = ResolutionReply(question, answer);
            var resolved = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => resolved.Status == QuestionStatus.Completed);
            Assert.IsTrue(resolved.ExistingQuestions.Contains(question));
            Assert.IsTrue(original.ResolvedInConversation);
            Assert.AreEqual(answer, original.ResolutionEvidence);
            Assert.IsEmpty(fake.Conversation.SuggestedQuestions);
            Assert.IsEmpty(fake.Conversation.AnswerHistory);
            await fake.Conversation.EndConversationAsync();
            var calls = fake.Work.Calls.Count;
            await fake.Conversation.ImportConversationAsync(fake.Conversation.RecordPath!);
            Assert.IsEmpty(fake.Conversation.SuggestedQuestions);
            Assert.IsTrue(fake.Conversation.CurrentConversation!.SuggestedQuestions.Single().ResolvedInConversation);
            Assert.HasCount(calls, fake.Work.Calls);
        }
        finally
        {
            await fake.Conversation.EndConversationAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task GroupCannotResolveAQuestionUsingAnAnswerOutsideItsTranscript()
    {
        const string question = "Who owns the rollout?";
        var fake = new Harness();
        fake.Work.AnalysisReply = PurposeReply(PurposeQuestion(question, RecommendationKind.Unanswered, question));
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            fake.Speech.EmitFinal(question, fake.Clock.GetUtcNow());
            var group = fake.Conversation.CreateTranscriptGroup();
            var first = fake.Conversation.AnalyzeGroup(group.Id);
            await WaitUntil(() => first.Status == QuestionStatus.Completed);
            fake.Speech.EmitFinal("Alice owns the rollout.", fake.Clock.GetUtcNow());
            fake.Work.AnalysisReply = ResolutionReply(question, "Alice owns the rollout.");
            var invalid = fake.Conversation.AnalyzeGroup(group.Id);
            await WaitUntil(() => invalid.Status == QuestionStatus.Failed);
            Assert.IsFalse(first.Questions.Single().ResolvedInConversation);
            Assert.AreEqual(first.Questions.Single().Id, fake.Conversation.GetSuggestedQuestionsForAnalysis(first.Id).Single().Id);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task TypedRecommendationReusesAnExistingPendingAndCompletedManualRequest()
    {
        const string question = "Who owns the rollout?";
        var fake = new Harness();
        fake.Work.AnalysisReply = PurposeReply(PurposeQuestion(question, RecommendationKind.Unanswered, question));
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            fake.Speech.EmitFinal(question, DateTimeOffset.UtcNow.AddSeconds(-2));
            var analysis = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => analysis.Status == QuestionStatus.Completed);
            var suggestion = analysis.Questions.Single();
            fake.Work.BlockCall = fake.Work.Calls.Count + 1;
            var manual = fake.Conversation.AskManually(question);
            StringAssert.Contains(manual.ContextUsed, question);
            await WaitUntil(() => manual.Status == QuestionStatus.Processing);
            var calls = fake.Work.Calls.Count;
            Assert.AreSame(manual, fake.Conversation.AskSuggestedQuestion(suggestion.Id));
            Assert.HasCount(1, fake.Conversation.AnswerHistory);
            Assert.HasCount(calls, fake.Work.Calls);
            fake.Work.ReleaseFirst(new WorkIqAnswer("The owner is not established; ask the participants to confirm.", [], "manual"));
            await WaitUntil(() => manual.Status == QuestionStatus.Completed);
            Assert.AreSame(manual, fake.Conversation.AskSuggestedQuestion(suggestion.Id));
            Assert.HasCount(calls, fake.Work.Calls);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task ReassessmentRefreshesPurposeAndReasonWithoutLosingIdentityOrNewContext()
    {
        const string initial = "We need a rollout plan.";
        const string question = "Who owns the rollout?";
        var fake = new Harness();
        fake.Work.AnalysisReply = PurposeReply(PurposeQuestion(question, RecommendationKind.FollowUp, initial));
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            fake.Speech.EmitFinal(initial, fake.Clock.GetUtcNow());
            var first = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => first.Status == QuestionStatus.Completed);
            var original = first.Questions.Single();
            fake.Speech.EmitFinal(question, fake.Clock.GetUtcNow());
            fake.Work.AnalysisReply = PurposeReply(PurposeQuestion(question, RecommendationKind.Unanswered, question,
                reason: "The question has now been explicitly raised without an answer."));
            var next = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => next.Status == QuestionStatus.Completed);
            Assert.AreSame(original, next.Questions.Single());
            Assert.AreEqual(RecommendationKind.Unanswered, original.Kind);
            StringAssert.Contains(original.Reason, "explicitly raised");
            var reply = fake.Conversation.AskSuggestedQuestion(original.Id);
            await WaitUntil(() => reply.Status == QuestionStatus.Completed);
            StringAssert.Contains(reply.ContextUsed, initial);
            StringAssert.Contains(reply.ContextUsed, question);
            StringAssert.Contains(reply.ContextUsed, "Unanswered");
            StringAssert.Contains(fake.Work.Calls.Last().Prompt, "UNRESOLVED CONVERSATION DECISIONS");
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task TypedRecommendationRetriesTheSameFailedManualRequestExplicitly()
    {
        const string question = "Who owns the rollout?";
        var fake = new Harness();
        fake.Work.AnalysisReply = PurposeReply(PurposeQuestion(question, RecommendationKind.Unanswered, question));
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            fake.Speech.EmitFinal(question, DateTimeOffset.UtcNow.AddSeconds(-2));
            var analysis = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => analysis.Status == QuestionStatus.Completed);
            fake.Work.Failures.Enqueue(new WorkIqException("Temporary failure"));
            var manual = fake.Conversation.AskManually(question);
            StringAssert.Contains(manual.ContextUsed, question);
            await WaitUntil(() => manual.Status == QuestionStatus.Failed);
            var calls = fake.Work.Calls.Count;
            Assert.AreSame(manual, fake.Conversation.AskSuggestedQuestion(analysis.Questions.Single().Id));
            await WaitUntil(() => manual.Status == QuestionStatus.Completed);
            Assert.HasCount(1, fake.Conversation.AnswerHistory);
            Assert.HasCount(calls + 1, fake.Work.Calls);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    private static object PurposeQuestion(string question, RecommendationKind kind, string evidence, string role = "", string reason = "A grounded conversational need.") =>
        new { question, reason, kind = kind.ToString(), evidence, role };
    private static string PurposeReply(params object[] questions) => JsonSerializer.Serialize(new { questions });
    private static string ResolutionReply(string question, string answerEvidence) => JsonSerializer.Serialize(new
    {
        questions = Array.Empty<object>(), resolvedQuestions = new[] { new { question, answerEvidence } }
    });
}
