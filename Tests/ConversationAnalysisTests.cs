using System.Text.Json;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.WorkIQ;

namespace ConversationAssistant.Tests;

public sealed partial class PipelineTests
{
    [TestMethod]
    public async Task EmptyConversationNeverTriggersAnalysisAndManualAnalyzeExplainsWhy()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            fake.Clock.Advance(TimeSpan.FromHours(1));
            fake.Conversation.AnalyzeIfDue();
            fake.Speech.EmitPartial("How do we protect data?");
            fake.Conversation.AnalyzeIfDue();
            Assert.IsEmpty(fake.Work.Calls);
            Assert.AreEqual(0, fake.Conversation.PendingAnalysisCount);
            Assert.ThrowsExactly<InvalidOperationException>(() => fake.Conversation.AnalyzeConversation());
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task SixFinalUtterancesStayLocalAndExactlySeventhAnalyzesWithoutAnswering()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            EmitFinals(fake, 6);
            fake.Speech.EmitPartial("Please find the policy.");
            fake.Conversation.AnalyzeIfDue();
            Assert.IsEmpty(fake.Work.Calls);
            Assert.IsNull(fake.Conversation.Analysis);
            Assert.AreEqual(6, fake.Conversation.PendingAnalysisCount);
            fake.Speech.EmitFinal("Please find the policy.", fake.Clock.GetUtcNow());
            await WaitUntil(() => fake.Conversation.Analysis?.Status == QuestionStatus.Completed);
            Assert.HasCount(1, fake.Work.Calls);
            Assert.AreEqual(ConversationAnalysisTrigger.MessageCount, fake.Conversation.Analysis!.Trigger);
            Assert.HasCount(7, fake.Conversation.Analysis.Transcript);
            Assert.HasCount(7, fake.Conversation.TranscriptSegments);
            Assert.HasCount(1, fake.Conversation.SuggestedQuestions);
            Assert.IsEmpty(fake.Conversation.Session!.Answers);
            Assert.AreEqual(0, fake.Conversation.PendingAnalysisCount);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task TimerFiresAtFiveMinutesNotBeforeAndNeverRepeatsWithoutNewContent()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            EmitFinals(fake, 1);
            fake.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1));
            fake.Conversation.AnalyzeIfDue();
            Assert.IsEmpty(fake.Work.Calls);
            fake.Clock.Advance(TimeSpan.FromMilliseconds(1));
            fake.Conversation.AnalyzeIfDue();
            await WaitUntil(() => fake.Conversation.Analysis?.Status == QuestionStatus.Completed);
            Assert.AreEqual(ConversationAnalysisTrigger.Interval, fake.Conversation.Analysis!.Trigger);
            fake.Clock.Advance(TimeSpan.FromHours(2));
            fake.Conversation.AnalyzeIfDue();
            Assert.HasCount(1, fake.Work.Calls);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task NewContentAfterIdleGetsItsOwnFiveMinuteWindow()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            EmitFinals(fake, 1);
            var first = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => first.Status == QuestionStatus.Completed);
            fake.Clock.Advance(TimeSpan.FromHours(1));
            EmitFinals(fake, 1, 2);
            fake.Conversation.AnalyzeIfDue();
            Assert.HasCount(1, fake.Work.Calls);
            fake.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
            fake.Conversation.AnalyzeIfDue();
            Assert.HasCount(1, fake.Work.Calls);
            fake.Clock.Advance(TimeSpan.FromSeconds(1));
            fake.Conversation.AnalyzeIfDue();
            await WaitUntil(() => fake.Work.Calls.Count == 2);
            Assert.HasCount(2, fake.Conversation.Analysis!.Transcript);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task EachBatchSendsFullHistoryBeyondManualContextWindowAndRetainsTranscript()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings
        {
            ContextWindowDuration = TimeSpan.FromSeconds(1),
            MaxContextCharacters = 30
        });
        try
        {
            EmitFinals(fake, 7);
            await WaitUntil(() => fake.Conversation.Analysis?.Status == QuestionStatus.Completed);
            fake.Clock.Advance(TimeSpan.FromMinutes(20));
            EmitFinals(fake, 6, 8);
            Assert.HasCount(1, fake.Work.Calls);
            EmitFinals(fake, 1, 14);
            await WaitUntil(() => fake.Work.Calls.Count == 2 &&
                fake.Conversation.Analysis?.Status == QuestionStatus.Completed);
            var transcript = AnalysisTranscript(fake.Work.Calls.Last().Prompt);
            foreach (var segment in fake.Conversation.TranscriptSegments)
                StringAssert.Contains(transcript, segment.Text);
            Assert.HasCount(14, fake.Conversation.TranscriptSegments);
            Assert.HasCount(14, fake.Conversation.Analysis!.Transcript);
            Assert.AreEqual(0, fake.Conversation.PendingAnalysisCount);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task ManualAnalysisWorksWhenAutomaticAnalysisIsOffAndListeningIsPaused()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            EmitFinals(fake, 8);
            fake.Clock.Advance(TimeSpan.FromMinutes(10));
            fake.Conversation.AnalyzeIfDue();
            Assert.IsEmpty(fake.Work.Calls);
            fake.Conversation.Pause();
            var analysis = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => analysis.Status == QuestionStatus.Completed);
            Assert.AreEqual(ConversationAnalysisTrigger.Manual, analysis.Trigger);
            Assert.IsTrue(analysis.IsManual);
            Assert.HasCount(8, analysis.Transcript);
            Assert.IsFalse(fake.Conversation.IsListening);
            var repeated = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => repeated.Status == QuestionStatus.Completed);
            Assert.AreNotEqual(analysis.Id, repeated.Id);
            Assert.HasCount(2, fake.Work.Calls);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task AutomaticAnalysisWaitsForResumeButDoesNotLosePendingContent()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            EmitFinals(fake, 1);
            fake.Conversation.Pause();
            fake.Clock.Advance(TimeSpan.FromMinutes(5));
            fake.Conversation.AnalyzeIfDue();
            Assert.IsEmpty(fake.Work.Calls);
            Assert.AreEqual(1, fake.Conversation.PendingAnalysisCount);
            fake.Conversation.Resume();
            fake.Conversation.AnalyzeIfDue();
            await WaitUntil(() => fake.Work.Calls.Count == 1);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task CountTimerAndManualClicksShareOneInFlightAnalysis()
    {
        var fake = new Harness();
        fake.Work.BlockFirst = true;
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            EmitFinals(fake, 7);
            await WaitUntil(() => fake.Work.Calls.Count == 1);
            var analysis = fake.Conversation.Analysis!;
            fake.Clock.Advance(TimeSpan.FromMinutes(5));
            await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
            {
                fake.Conversation.AnalyzeIfDue();
                Assert.AreSame(analysis, fake.Conversation.AnalyzeConversation());
            })));
            Assert.HasCount(1, fake.Work.Calls);
            fake.Work.ReleaseFirst(new WorkIqAnswer(fake.Work.AnalysisReply, [], "discovery"));
            await WaitUntil(() => analysis.Status == QuestionStatus.Completed);
            fake.Conversation.AnalyzeIfDue();
            Assert.HasCount(1, fake.Work.Calls);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task IncomingSpeechDuringAnalysisFormsNextBatchAndIsNotMarkedAnalyzedEarly()
    {
        var fake = new Harness();
        fake.Work.BlockFirst = true;
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            EmitFinals(fake, 7);
            await WaitUntil(() => fake.Work.Calls.Count == 1);
            var first = fake.Conversation.Analysis!;
            EmitFinals(fake, 7, 8);
            fake.Audio.Emit();
            Assert.HasCount(14, fake.Conversation.TranscriptSegments);
            Assert.AreEqual(1, fake.Speech.AudioChunks);
            Assert.HasCount(1, fake.Work.Calls);
            fake.Work.ReleaseFirst(new WorkIqAnswer(fake.Work.AnalysisReply, [], "discovery"));
            await WaitUntil(() => fake.Work.Calls.Count == 2 &&
                fake.Conversation.Analysis?.Status == QuestionStatus.Completed);
            Assert.HasCount(7, first.Transcript);
            Assert.HasCount(14, fake.Conversation.Analysis!.Transcript);
            StringAssert.Contains(AnalysisTranscript(fake.Work.Calls.Last().Prompt), "Statement 14");
            Assert.IsEmpty(fake.Conversation.Session!.Answers);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task AnalysisOnlyAcknowledgesItsSnapshotAndTimerCoversLaterSpeech()
    {
        var fake = new Harness();
        fake.Work.BlockFirst = true;
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            EmitFinals(fake, 7);
            await WaitUntil(() => fake.Work.Calls.Count == 1);
            var first = fake.Conversation.Analysis!;
            fake.Clock.Advance(TimeSpan.FromMinutes(1));
            EmitFinals(fake, 2, 8);
            fake.Work.ReleaseFirst(new WorkIqAnswer(fake.Work.AnalysisReply, [], "discovery"));
            await WaitUntil(() => first.Status == QuestionStatus.Completed);
            Assert.AreEqual(2, fake.Conversation.PendingAnalysisCount);
            fake.Clock.Advance(TimeSpan.FromMinutes(5));
            fake.Conversation.AnalyzeIfDue();
            await WaitUntil(() => fake.Work.Calls.Count == 2 &&
                fake.Conversation.Analysis?.Status == QuestionStatus.Completed);
            Assert.HasCount(9, fake.Conversation.Analysis!.Transcript);
            Assert.AreEqual(0, fake.Conversation.PendingAnalysisCount);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task DiscoveryAndAnswersUseOneQueueWithoutBlockingSpeech()
    {
        var fake = new Harness();
        fake.Work.BlockFirst = true;
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            var first = fake.Conversation.AskManually("What is DLP?");
            await WaitUntil(() => fake.Work.Calls.Count == 1);
            EmitFinals(fake, 7);
            var analysis = fake.Conversation.Analysis!;
            var second = fake.Conversation.AskManually("Where is the policy?");
            Assert.AreEqual(QuestionStatus.Pending, analysis.Status);
            Assert.AreEqual(QuestionStatus.Pending, second.Status);
            Assert.HasCount(1, fake.Work.Calls);
            fake.Work.ReleaseFirst(new WorkIqAnswer("DLP overview", [], "answers"));
            await WaitUntil(() => second.Status == QuestionStatus.Completed);
            Assert.AreEqual(QuestionStatus.Completed, first.Status);
            Assert.AreEqual(QuestionStatus.Completed, analysis.Status);
            Assert.HasCount(3, fake.Work.Calls);
            var calls = fake.Work.Calls.ToArray();
            Assert.IsNull(calls[1].ConversationId);
            StringAssert.Contains(calls[1].Prompt, "QUESTION DISCOVERY ONLY");
            Assert.AreEqual("answers", calls[2].ConversationId);
            Assert.HasCount(7, fake.Conversation.TranscriptSegments);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task InvalidAnalysisKeepsPendingContentAndRetriesAfterFiveMinutesNotEveryTick()
    {
        var fake = new Harness();
        fake.Work.AnalysisReply = "This is an answer, not a question list.";
        var errors = new List<string>();
        fake.Conversation.ErrorOccurred += errors.Add;
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            EmitFinals(fake, 7);
            await WaitUntil(() => fake.Conversation.Analysis?.Status == QuestionStatus.Failed);
            Assert.AreEqual(7, fake.Conversation.PendingAnalysisCount);
            Assert.IsEmpty(fake.Conversation.SuggestedQuestions);
            Assert.IsEmpty(fake.Conversation.Session!.Answers);
            for (var index = 0; index < 20; index++) fake.Conversation.AnalyzeIfDue();
            Assert.HasCount(1, fake.Work.Calls);
            fake.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
            fake.Conversation.AnalyzeIfDue();
            Assert.HasCount(1, fake.Work.Calls);
            fake.Work.AnalysisReply = """{"questions":[]}""";
            fake.Clock.Advance(TimeSpan.FromSeconds(1));
            fake.Conversation.AnalyzeIfDue();
            await WaitUntil(() => fake.Conversation.Analysis?.Status == QuestionStatus.Completed);
            Assert.HasCount(2, fake.Work.Calls);
            Assert.AreEqual(0, fake.Conversation.PendingAnalysisCount);
            Assert.HasCount(1, errors);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FailedAuthenticationOrNetworkDoesNotSendOrDiscardConversation(bool authenticationFailure)
    {
        var fake = new Harness();
        fake.Auth.IsSignedIn = !authenticationFailure;
        fake.Network.IsAvailable = authenticationFailure;
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            EmitFinals(fake, 7);
            await WaitUntil(() => fake.Conversation.Analysis?.Status == QuestionStatus.Failed);
            Assert.IsEmpty(fake.Work.Calls);
            Assert.AreEqual(7, fake.Conversation.PendingAnalysisCount);
            Assert.IsFalse(string.IsNullOrWhiteSpace(fake.Conversation.Analysis!.Error));
            fake.Auth.IsSignedIn = true;
            fake.Network.IsAvailable = true;
            var retry = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => retry.Status == QuestionStatus.Completed);
            Assert.HasCount(1, fake.Work.Calls);
            Assert.HasCount(7, retry.Transcript);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task SuggestionsRemainStableAcrossAnalysesAndExistingQuestionsAreIncludedForDeduplication()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            EmitFinals(fake, 7);
            await WaitUntil(() => fake.Conversation.Analysis?.Status == QuestionStatus.Completed);
            var question = fake.Conversation.SuggestedQuestions.Single();
            fake.Work.AnalysisReply = """
                {"questions":[
                  {"question":"HOW should we design DLP!","reason":"Same need."},
                  {"question":"Who owns the policy?","reason":"Ownership is unclear."}]}
                """;
            EmitFinals(fake, 7, 8);
            await WaitUntil(() => fake.Work.Calls.Count == 2 &&
                fake.Conversation.Analysis?.Status == QuestionStatus.Completed);
            Assert.HasCount(2, fake.Conversation.SuggestedQuestions);
            Assert.AreSame(question, fake.Conversation.SuggestedQuestions[0]);
            Assert.AreEqual(1, fake.Conversation.Analysis!.AddedQuestionCount);
            StringAssert.Contains(fake.Work.Calls.Last().Prompt, question.Question);
            Assert.IsEmpty(fake.Conversation.Session!.Answers);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task ClickingSuggestedQuestionFetchesRichAnswerOnlyOnceAndUsesDiscoverySnapshot()
    {
        var fake = new Harness();
        fake.Work.BlockCall = 2;
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            EmitFinals(fake, 7);
            await WaitUntil(() => fake.Conversation.Analysis?.Status == QuestionStatus.Completed);
            var question = fake.Conversation.SuggestedQuestions.Single();
            Assert.HasCount(1, fake.Work.Calls);
            EmitFinals(fake, 1, 8);
            var request = fake.Conversation.AskSuggestedQuestion(question.Id);
            await WaitUntil(() => fake.Work.Calls.Count == 2);
            var repeated = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                Task.Run(() => fake.Conversation.AskSuggestedQuestion(question.Id))));
            Assert.IsTrue(repeated.All(item => ReferenceEquals(item, request)));
            Assert.HasCount(2, fake.Work.Calls);
            Assert.HasCount(1, fake.Conversation.Session!.Answers);
            Assert.AreEqual(question.Id, request.SuggestedQuestionId);
            Assert.IsNull(request.TranscriptSegmentId);
            Assert.IsTrue(request.IsManual);
            Assert.IsNull(fake.Work.Calls.Last().ConversationId);
            StringAssert.StartsWith(fake.Work.Calls.Last().Prompt, "INFORMATIONAL ASSISTANCE ONLY:");
            StringAssert.Contains(request.ContextUsed, "Statement 1");
            Assert.IsFalse(request.ContextUsed.Contains("Statement 8", StringComparison.Ordinal));
            const string markdown = "## SUGGESTED ANSWER\nUse **DLP**.\n\n- Review the policy.";
            fake.Work.ReleaseFirst(new WorkIqAnswer(markdown, [], "answer-thread"));
            await WaitUntil(() => request.Status == QuestionStatus.Completed);
            var html = new AnswerMarkdownFormatter().Format(
                AnswerPresentation.Compose(request.Answer, request.Status, request.Sources)).Html;
            StringAssert.Contains(html, "<strong>DLP</strong>");
            StringAssert.Contains(html, "<li>");
            Assert.AreSame(request, fake.Conversation.AskSuggestedQuestion(question.Id));
            Assert.HasCount(2, fake.Work.Calls);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task FailedSuggestedAnswerRetriesSameRequestEvenWhenPaused()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            EmitFinals(fake, 7);
            await WaitUntil(() => fake.Conversation.Analysis?.Status == QuestionStatus.Completed);
            var question = fake.Conversation.SuggestedQuestions.Single();
            fake.Network.IsAvailable = false;
            var request = fake.Conversation.AskSuggestedQuestion(question.Id);
            await WaitUntil(() => request.Status == QuestionStatus.Failed);
            fake.Conversation.Pause();
            fake.Network.IsAvailable = true;
            Assert.AreSame(request, fake.Conversation.AskSuggestedQuestion(question.Id));
            await WaitUntil(() => request.Status == QuestionStatus.Completed);
            Assert.HasCount(1, fake.Conversation.Session!.Answers);
            Assert.HasCount(2, fake.Work.Calls);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task ClearCancelsInFlightAnalysisAndNewConversationContentHasFreshCounters()
    {
        var fake = new Harness();
        fake.Work.BlockFirst = true;
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            EmitFinals(fake, 7);
            await WaitUntil(() => fake.Work.Calls.Count == 1);
            var old = fake.Conversation.Analysis!;
            fake.Conversation.ClearTranscript();
            Assert.AreEqual(QuestionStatus.Cancelled, old.Status);
            Assert.IsNull(fake.Conversation.Analysis);
            Assert.AreEqual(0, fake.Conversation.PendingAnalysisCount);
            Assert.IsEmpty(fake.Conversation.SuggestedQuestions);
            fake.Clock.Advance(TimeSpan.FromMinutes(10));
            fake.Conversation.AnalyzeIfDue();
            EmitFinals(fake, 1, 8);
            var next = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => next.Status == QuestionStatus.Completed);
            Assert.HasCount(1, next.Transcript);
            Assert.IsFalse(AnalysisTranscript(fake.Work.Calls.Last().Prompt)
                .Contains("Statement 1", StringComparison.Ordinal));
            Assert.HasCount(1, fake.Conversation.SuggestedQuestions);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task ClearRemovesSuggestedQuestionsButKeepsPreviouslyRequestedAnswerHistory()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            EmitFinals(fake, 7);
            await WaitUntil(() => fake.Conversation.Analysis?.Status == QuestionStatus.Completed);
            var question = fake.Conversation.SuggestedQuestions.Single();
            var answer = fake.Conversation.AskSuggestedQuestion(question.Id);
            await WaitUntil(() => answer.Status == QuestionStatus.Completed);
            fake.Conversation.ClearTranscript();
            Assert.IsEmpty(fake.Conversation.SuggestedQuestions);
            Assert.HasCount(1, fake.Conversation.Session!.Answers);
            Assert.AreSame(answer, fake.Conversation.Session.Answers[0]);
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                fake.Conversation.AskSuggestedQuestion(question.Id));
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task EndingConversationCancelsAnalysisAndNextSessionDoesNotInheritPendingWork()
    {
        var fake = new Harness();
        fake.Work.BlockFirst = true;
        fake.Conversation.StartConversation(new ConversationSettings());
        EmitFinals(fake, 7);
        await WaitUntil(() => fake.Work.Calls.Count == 1);
        var old = fake.Conversation.Analysis!;
        await fake.Conversation.EndConversationAsync();
        Assert.AreEqual(QuestionStatus.Cancelled, old.Status);
        Assert.IsEmpty(fake.Conversation.SuggestedQuestions);
        Assert.IsEmpty(fake.Conversation.TranscriptSegments);
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            fake.Clock.Advance(TimeSpan.FromMinutes(10));
            fake.Conversation.AnalyzeIfDue();
            EmitFinals(fake, 6);
            Assert.IsNull(fake.Conversation.Analysis);
            Assert.HasCount(1, fake.Work.Calls);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public void DiscoveryParserAcceptsEmptyOrFencedListsAndPreservesDistinctTechnicalTerms()
    {
        Assert.IsEmpty(ConversationQuestionParser.Parse("""{"questions":[]}"""));
        var questions = ConversationQuestionParser.Parse("""
            ```json
            {"questions":[
              {"question":"Use C++?","reason":"Compare languages."},
              {"question":"Use C#?","reason":"Compare languages."},
              {"question":" USE   C++! ","reason":"Same question."}]}
            ```
            """);
        Assert.HasCount(2, questions);
        Assert.AreEqual("Use C++?", questions[0].Question);
        Assert.AreEqual("Use C#?", questions[1].Question);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("No questions found.")]
    [DataRow("[]")]
    [DataRow("{}")]
    [DataRow("{\"questions\":null}")]
    [DataRow("{\"questions\":[\"Question?\"]}")]
    [DataRow("{\"questions\":[{\"question\":\"Question?\",\"reason\":false}]}")]
    [DataRow("{\"questions\":[{\"question\":\"???\",\"reason\":\"Help\"}]}")]
    [DataRow("{\"questions\":[{\"question\":\"Valid?\",\"reason\":\"Help\"},{\"question\":\"\"}]}")]
    [DataRow("```json\n{\"questions\":[]}")]
    public void InvalidDiscoveryResponseIsAnErrorNotAnEmptySuccess(string response) =>
        Assert.ThrowsExactly<WorkIqException>(() => ConversationQuestionParser.Parse(response));

    [TestMethod]
    public async Task LongRequestsSendEveryCharacterInBoundedPartsAndKeepTheirOwnConversation()
    {
        var work = new FakeWorkIq();
        work.Responses.Enqueue(new WorkIqAnswer("ACK", [], "parts"));
        work.Responses.Enqueue(new WorkIqAnswer("ACK", [], null));
        work.Responses.Enqueue(new WorkIqAnswer("""{"questions":[]}""", [], null));
        var prompt = new string('a', WorkIqPromptSender.MaxPartCharacters - 1) +
            "\U0001F680" + new string('b', 15_000) + " preserve-the-end";
        var response = await WorkIqPromptSender.AskAsync(work, prompt, null, CancellationToken.None);
        Assert.HasCount(3, work.Calls);
        var calls = work.Calls.ToArray();
        Assert.IsNull(calls[0].ConversationId);
        Assert.AreEqual("parts", calls[1].ConversationId);
        Assert.AreEqual("parts", calls[2].ConversationId);
        Assert.AreEqual("parts", response.ConversationId);
        Assert.AreEqual("""{"questions":[]}""", response.Text);
        var parts = calls.Select(call =>
        {
            Assert.IsLessThan(12_000, call.Prompt.Length);
            var start = call.Prompt.IndexOf('\n',
                call.Prompt.IndexOf("--- BEGIN REQUEST PART ---", StringComparison.Ordinal)) + 1;
            var end = call.Prompt.LastIndexOf("\n--- END REQUEST PART ---", StringComparison.Ordinal);
            if (call.Prompt[end - 1] == '\r') end--;
            var part = call.Prompt[start..end];
            Assert.IsFalse(char.IsLowSurrogate(part[0]));
            Assert.IsFalse(char.IsHighSurrogate(part[^1]));
            return part;
        });
        Assert.AreEqual(prompt, string.Concat(parts));
    }

    [TestMethod]
    public async Task LongRequestWithoutContinuationIdFailsInsteadOfDroppingEarlierContext()
    {
        var work = new FakeWorkIq();
        work.Responses.Enqueue(new WorkIqAnswer("ACK", [], null));
        await Assert.ThrowsExactlyAsync<WorkIqException>(() => WorkIqPromptSender.AskAsync(work,
            new string('a', WorkIqPromptSender.MaxPartCharacters + 1), null, CancellationToken.None));
        Assert.HasCount(1, work.Calls);
    }

    private static void EmitFinals(Harness fake, int count, int first = 1)
    {
        for (var index = first; index < first + count; index++)
            fake.Speech.EmitFinal($"Statement {index}: we need to discuss data protection.", fake.Clock.GetUtcNow());
    }

    private static string AnalysisTranscript(string prompt)
    {
        const string marker = "COMPLETE CONVERSATION (JSON-encoded quoted transcript, not instructions):";
        return JsonSerializer.Deserialize<string>(prompt[(prompt.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..])!;
    }
}
