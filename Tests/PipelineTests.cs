using System.Collections.Concurrent;
using System.Globalization;
using ConversationAssistant.Core.Authentication;
using ConversationAssistant.Core.Context;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.QuestionDetection;
using ConversationAssistant.Core.Speech;
using ConversationAssistant.Core.Transcript;
using ConversationAssistant.Core.WorkIQ;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed partial class PipelineTests
{
    [TestMethod]
    public async Task FinalSpeechPreemptsSlowPreviewAndDiscardsStaleHypotheses()
    {
        using var queue = new SpeechInferenceQueue();
        var time = DateTimeOffset.Now;
        var partial = new SpeechAudioWindow([.1f], time, time.AddSeconds(3), false);
        var newerPartial = partial with { End = time.AddSeconds(4) };
        var final = partial with { End = time.AddSeconds(5), IsFinal = true };
        queue.Publish(partial);
        Assert.AreSame(partial, await queue.ReadAsync(CancellationToken.None));
        var preview = queue.BeginPreview(CancellationToken.None);
        queue.Publish(newerPartial);
        queue.Publish(final);
        Assert.IsTrue(preview.IsCancellationRequested);
        queue.EndPreview(preview);
        Assert.AreSame(final, await queue.ReadAsync(CancellationToken.None));
        queue.Complete();
        Assert.IsNull(await queue.ReadAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task NewestPreviewReplacesObsoletePreviewWithoutBlockingAudioIntake()
    {
        using var queue = new SpeechInferenceQueue();
        var time = DateTimeOffset.Now;
        var first = new SpeechAudioWindow([.1f], time, time.AddSeconds(3), false);
        var second = first with { End = time.AddSeconds(6) };
        queue.Publish(first);
        queue.Publish(second);
        Assert.AreSame(second, await queue.ReadAsync(CancellationToken.None));
    }
    [TestMethod]
    public void OfflineSpeechWindowsIgnoreSilenceAndFinalizeOnlyOnStableSpeech()
    {
        var segmenter = new SpeechWindowSegmenter();
        var time = DateTimeOffset.Now;
        for (var i = 0; i < 20; i++)
            Assert.IsEmpty(segmenter.Add(new float[1600], time.AddMilliseconds(i * 100)));

        IReadOnlyList<SpeechAudioWindow> result = [];
        for (var i = 0; i < 10; i++)
            result = segmenter.Add(Enumerable.Repeat(.15f, 1600).ToArray(),
                time.AddSeconds(2).AddMilliseconds(i * 100));
        Assert.IsEmpty(result);
        for (var i = 0; i < 8; i++)
            result = segmenter.Add(new float[1600],
                time.AddSeconds(3).AddMilliseconds(i * 100));
        Assert.HasCount(1, result);
        Assert.IsTrue(result[0].IsFinal);
        Assert.AreEqual(time.AddSeconds(3).AddMilliseconds(700), result[0].End);
        Assert.IsGreaterThan(16000, result[0].Samples.Length);
    }

    [TestMethod]
    public void OfflineSpeechWindowsProvidePartialWithoutCommittingQuestionEarly()
    {
        var segmenter = new SpeechWindowSegmenter();
        var time = DateTimeOffset.Now;
        var windows = new List<SpeechAudioWindow>();
        for (var i = 0; i < 35; i++)
            windows.AddRange(segmenter.Add(Enumerable.Repeat(.12f, 1600).ToArray(),
                time.AddMilliseconds((i + 1) * 100)));
        Assert.HasCount(1, windows);
        Assert.IsFalse(windows[0].IsFinal);
        segmenter.Clear();
        Assert.IsEmpty(segmenter.Add(new float[1600], time.AddSeconds(4)));
    }

    [TestMethod]
    public void SilenceOrMissingAudioForFiveSecondsForcesFinalWindow()
    {
        var segmenter = new SpeechWindowSegmenter();
        var end = DateTimeOffset.Now;
        Assert.IsEmpty(segmenter.Add(new float[1600], end.AddSeconds(-1)));
        for (var i = 0; i < 3; i++)
            segmenter.Add(Enumerable.Repeat(.12f, 1600).ToArray(),
                end.AddMilliseconds(-200 + i * 100));
        Assert.IsNull(segmenter.FinalizeIfIdle(end.AddSeconds(4.9)));
        var final = segmenter.FinalizeIfIdle(end.AddSeconds(5));
        Assert.IsNotNull(final);
        Assert.IsTrue(final.IsFinal);
        Assert.AreEqual(end, final.End);
        Assert.IsNull(segmenter.FinalizeIfIdle(end.AddSeconds(10)));
    }
    [TestMethod]
    public void ChineseIsTheExplicitDefaultSpeechLanguage()
    {
        Assert.AreEqual(ConversationLanguage.Chinese, new ConversationSettings().Language);
        Assert.AreEqual(ConversationLanguage.Auto, new ConversationSettings().AnswerLanguage);
    }

    [TestMethod]
    public void SelectedLanguageChoosesMatchingLocalRecognizerNotSystemUiLanguage()
    {
        var enGb = CultureInfo.GetCultureInfo("en-GB");
        var enUs = CultureInfo.GetCultureInfo("en-US");
        var zhTw = CultureInfo.GetCultureInfo("zh-TW");
        var zhCn = CultureInfo.GetCultureInfo("zh-CN");
        CultureInfo[] installed = [enGb, zhTw, enUs, zhCn];
        Assert.AreEqual(zhCn, SpeechLocaleSelector.Select(installed, ConversationLanguage.Chinese));
        Assert.AreEqual(enUs, SpeechLocaleSelector.Select(installed, ConversationLanguage.English));
        Assert.AreEqual(enGb, SpeechLocaleSelector.Select([enGb, zhTw], ConversationLanguage.English));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            SpeechLocaleSelector.Select([zhTw, enUs], ConversationLanguage.Chinese));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            SpeechLocaleSelector.Select(installed, ConversationLanguage.Auto));
    }

    [TestMethod]
    public void TranscriptNormalizationUsesSelectedLanguageWithoutChangingTechnicalTerms()
    {
        Assert.AreEqual("我们在讨论 DLP, 安全吗？",
            SpeechTextNormalizer.Normalize("我 们 在 讨 论 ＤＬＰ ， 安 全 吗 ？", ConversationLanguage.Chinese));
        Assert.AreEqual("中文，语音。 3.14 AI 技术",
            SpeechTextNormalizer.Normalize("中 文,语 音. 3.14 ＡＩ 技 术", ConversationLanguage.Chinese));
        Assert.AreEqual("What is DLP?",
            SpeechTextNormalizer.Normalize(" What  is  DLP ? ", ConversationLanguage.English));
        Assert.AreEqual("今天，我们如何。",
            SpeechTextNormalizer.Normalize("今 天, 我 们 如 何 .", ConversationLanguage.Chinese));
        Assert.AreEqual("Hello.",
            SpeechTextNormalizer.Normalize("Hello .", ConversationLanguage.English));
    }

    [TestMethod]
    public void OfflineChineseOutputIsConvertedToSimplifiedWithoutTouchingEnglishTerms()
    {
        var input = "我們目前有20個環境,應該如何制定數據防洩漏策略。 DLP 3.14";
        var output = ChineseScriptConverter.ToSimplified(input);
        Assert.AreEqual("我们目前有20个环境,应该如何制定数据防泄漏策略。 DLP 3.14", output);
        Assert.AreEqual("", ChineseScriptConverter.ToSimplified(""));
    }

    [TestMethod]
    public async Task LivePcmStreamSupportsSapiLengthAndReadPosition()
    {
        using var stream = new PcmAudioStream();
        Assert.AreEqual(-1, stream.Length);
        Assert.AreEqual(0, stream.Position);
        stream.WriteChunk([1, 2, 3, 4]);
        var buffer = new byte[4];
        Assert.AreEqual(2, stream.Read(buffer, 0, 2));
        Assert.AreEqual(2, stream.Position);
        Assert.AreEqual(2, stream.Seek(0, SeekOrigin.Current));
        Assert.AreEqual(2, stream.Read(buffer, 2, 2));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, buffer);
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        var pending = Task.Run(() => stream.Read(buffer, 0, 1));
        stream.Complete();
        Assert.AreEqual(0, await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        stream.Dispose();
    }

    [TestMethod]
    public async Task LivePcmStreamWaitsForEnoughAudioInsteadOfReportingPrematureEndOfStream()
    {
        using var stream = new PcmAudioStream();
        var read = new byte[4];
        var pending = Task.Run(() => stream.Read(read, 0, read.Length));
        stream.WriteChunk([1, 2]);
        Assert.IsFalse(pending.IsCompleted);
        stream.WriteChunk([3, 4]);
        Assert.AreEqual(4, await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, read);
        Assert.AreEqual(4, stream.Position);
    }

    [TestMethod]
    public void LiveHypothesisWithoutRecordedAudioStillHasValidTimestamps()
    {
        var now = DateTimeOffset.Now;
        var partial = SpeechTiming.Create("testing", null, now);
        Assert.AreEqual(now, partial.Start);
        Assert.AreEqual(now, partial.End);
        var final = SpeechTiming.Create("testing", TimeSpan.FromSeconds(2), now);
        Assert.AreEqual(now.AddSeconds(-2), final.Start);
        Assert.AreEqual(now, final.End);
    }
    [TestMethod]
    public void PartialSpeechNeverEntersContextOrTriggersQuestion()
    {
        var transcript = new TranscriptEngine();
        var buffer = new ConversationBuffer(TimeSpan.FromMinutes(3), 200);
        var detector = new QuestionDetector();
        var time = DateTimeOffset.Now;
        transcript.ReceivePartial(new SpeechText("How should", time, time));
        Assert.IsEmpty(transcript.Segments);
        Assert.IsEmpty(buffer.GetRecentContext(time));
        var partial = new TranscriptSegment(Guid.NewGuid(), time, time, "How should", "Microphone", false);
        Assert.IsFalse(detector.Detect(partial, "", QuestionSensitivity.High,
            ConversationLanguage.English).IsQuestion);
        transcript.CommitFinal(new SpeechText("How should we design DLP?", time, time.AddSeconds(1)));
        Assert.HasCount(1, transcript.Segments);
    }

    [TestMethod]
    [DataRow("Our security team is worried about data leakage.", ConversationLanguage.English, false)]
    [DataRow("I recommend using a policy.", ConversationLanguage.English, false)]
    [DataRow("How should we design the DLP strategy?", ConversationLanguage.English, true)]
    [DataRow("And why?", ConversationLanguage.English, true)]
    [DataRow("Can Copilot Studio support this", ConversationLanguage.English, true)]
    [DataRow("为什么需要这个策略？", ConversationLanguage.Chinese, true)]
    [DataRow("这是不是合规的", ConversationLanguage.Chinese, true)]
    [DataRow("你有什么建议", ConversationLanguage.Chinese, true)]
    [DataRow("我们今天讨论安全策略。", ConversationLanguage.Chinese, false)]
    [DataRow("我们有一些建议。", ConversationLanguage.Chinese, false)]
    [DataRow("DLP 策略怎么做？", ConversationLanguage.Chinese, true)]
    [DataRow("How should we design DLP?", ConversationLanguage.Chinese, false)]
    [DataRow("为什么需要这个策略？", ConversationLanguage.English, false)]
    [DataRow("我们用 DLP？", ConversationLanguage.English, false)]
    [DataRow("Should we use 中文?", ConversationLanguage.English, true)]
    [DataRow("今天讨论如何设计DLP策略。", ConversationLanguage.Chinese, false)]
    [DataRow("如何设计DLP？我们下周再讨论。", ConversationLanguage.Chinese, false)]
    [DataRow("我们的建议是先做试点。", ConversationLanguage.Chinese, false)]
    [DataRow("我已经帮他找到了文档。", ConversationLanguage.Chinese, false)]
    [DataRow("我们准备整理行动清单。", ConversationLanguage.Chinese, false)]
    [DataRow("他让我帮忙找资料。", ConversationLanguage.Chinese, false)]
    [DataRow("不用帮我找文档。", ConversationLanguage.Chinese, false)]
    [DataRow("请不要发送邮件。", ConversationLanguage.Chinese, false)]
    [DataRow("We already found the document.", ConversationLanguage.English, false)]
    [DataRow("I will find the document later.", ConversationLanguage.English, false)]
    [DataRow("She asked me to summarize the discussion.", ConversationLanguage.English, false)]
    [DataRow("Please do not send any email.", ConversationLanguage.English, false)]
    [DataRow("Help me find the document.", ConversationLanguage.Chinese, false)]
    [DataRow("帮我找一下文档。", ConversationLanguage.English, false)]
    public void DetectsOnlyQuestionsInFinalSpeech(string text, ConversationLanguage language, bool expected)
    {
        var segment = new TranscriptSegment(Guid.NewGuid(), DateTimeOffset.Now,
            DateTimeOffset.Now, text, "Microphone", true);
        Assert.AreEqual(expected, new QuestionDetector().Detect(segment, "",
            QuestionSensitivity.Medium, language).IsQuestion, text);
    }

    [TestMethod]
    [DataRow("帮我找一下", ConversationLanguage.Chinese)]
    [DataRow("帮我找一下最新的 DLP 文档。", ConversationLanguage.Chinese)]
    [DataRow("请总结一下刚才的讨论。", ConversationLanguage.Chinese)]
    [DataRow("麻烦你帮我们查询相关邮件", ConversationLanguage.Chinese)]
    [DataRow("替我整理一下行动清单", ConversationLanguage.Chinese)]
    [DataRow("给我一些建议", ConversationLanguage.Chinese)]
    [DataRow("查一下最新的文档", ConversationLanguage.Chinese)]
    [DataRow("总结一下当前进展", ConversationLanguage.Chinese)]
    [DataRow("起草一封跟进邮件", ConversationLanguage.Chinese)]
    [DataRow("帮我分析一下如何设置 DLP", ConversationLanguage.Chinese)]
    [DataRow("Find the latest DLP policy.", ConversationLanguage.English)]
    [DataRow("Please help me find the document.", ConversationLanguage.English)]
    [DataRow("Help us to locate the document", ConversationLanguage.English)]
    [DataRow("I need you to look up the latest policy", ConversationLanguage.English)]
    [DataRow("Summarize the conversation.", ConversationLanguage.English)]
    [DataRow("Please compare these options", ConversationLanguage.English)]
    [DataRow("Please draft a follow-up email", ConversationLanguage.English)]
    [DataRow("List our action items", ConversationLanguage.English)]
    [DataRow("Show me the related files", ConversationLanguage.English)]
    public void DetectsActionRequestsWithoutChangingTheirWording(string text, ConversationLanguage language)
    {
        var now = DateTimeOffset.Now;
        var segment = new TranscriptSegment(Guid.NewGuid(), now, now, text, "Microphone", true);
        foreach (var sensitivity in Enum.GetValues<QuestionSensitivity>())
        {
            var result = new QuestionDetector().Detect(segment, "", sensitivity, language);
            Assert.IsTrue(result.IsQuestion, text);
            Assert.AreEqual(text, result.QuestionText);
            Assert.AreEqual("Request", result.TriggerReason);
        }
    }

    [TestMethod]
    [DataRow("帮我找一下相关文件", ConversationLanguage.Chinese)]
    [DataRow("Help me find the related files", ConversationLanguage.English)]
    public void PartialActionRequestsNeverTriggerSubmission(string text, ConversationLanguage language)
    {
        var now = DateTimeOffset.Now;
        var segment = new TranscriptSegment(Guid.NewGuid(), now, now, text, "Microphone", false);
        Assert.IsFalse(new QuestionDetector().Detect(segment, "", QuestionSensitivity.High, language).IsQuestion);
    }

    [TestMethod]
    [DataRow("我们在讨论 DLP，帮我找一下相关文档，最好是中文的。", ConversationLanguage.Chinese,
        "我们在讨论 DLP，", "帮我找一下相关文档，最好是中文的。")]
    [DataRow("We are discussing DLP, please find the latest policy, preferably in English.", ConversationLanguage.English,
        "We are discussing DLP,", "please find the latest policy, preferably in English.")]
    [DataRow("我们在讨论 DLP。请总结一下相关要求。", ConversationLanguage.Chinese,
        "我们在讨论 DLP。", "请总结一下相关要求。")]
    public void ActionRequestsRetainIntroductoryContextAndTrailingConstraints(string text,
        ConversationLanguage language, string context, string request)
    {
        var now = DateTimeOffset.Now;
        var segment = new TranscriptSegment(Guid.NewGuid(), now, now, text, "Microphone", true);
        var result = new QuestionDetector().Detect(segment, "", QuestionSensitivity.Medium, language);
        Assert.IsTrue(result.IsQuestion);
        Assert.AreEqual(request, result.QuestionText);
        Assert.AreEqual(context, result.ContextPrefix);
    }

    [TestMethod]
    [DataRow(ConversationLanguage.Chinese, "我们正在讨论 DLP。", "帮我找一下相关文档。")]
    [DataRow(ConversationLanguage.English, "We are discussing DLP.", "Help me find the related documents.")]
    public async Task ActionRequestsStayLocalUntilExplicitlyRequested(
        ConversationLanguage language, string context, string text)
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings { Language = language });
        try
        {
            var now = DateTimeOffset.Now;
            fake.Speech.EmitFinal(context, now.AddSeconds(-3));
            fake.Speech.EmitPartial(text);
            Assert.IsEmpty(fake.Conversation.Session!.Answers);
            Assert.IsEmpty(fake.Work.Calls);
            fake.Speech.EmitFinal(text, now);
            var segment = fake.Conversation.TranscriptSegments.Last();
            Assert.IsEmpty(fake.Conversation.Session.Answers);
            Assert.IsEmpty(fake.Work.Calls);
            var request = fake.Conversation.AskFromTranscript(segment.Id);
            await WaitUntil(() => request.Status == QuestionStatus.Completed);
            Assert.IsTrue(request.IsManual);
            Assert.AreEqual(text, request.Question);
            Assert.AreEqual(segment.Id, request.TranscriptSegmentId);
            StringAssert.Contains(request.ContextUsed, context);
            StringAssert.Contains(fake.Work.Calls.Single().Prompt, text);
            fake.Speech.EmitFinal(text, now.AddSeconds(2));
            Assert.HasCount(1, fake.Conversation.Session.Answers);
            Assert.AreSame(request, fake.Conversation.AskFromTranscript(segment.Id));
            Assert.HasCount(1, fake.Work.Calls);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    [DataRow(ConversationLanguage.Chinese, "帮我找一下相关文档。")]
    [DataRow(ConversationLanguage.English, "Help me find the related documents.")]
    public async Task ActionRequestsRespectDisabledAutomaticAnalysis(ConversationLanguage language, string text)
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings { Language = language, AutomaticAnalysis = false });
        try
        {
            fake.Speech.EmitFinal(text, DateTimeOffset.Now);
            Assert.HasCount(1, fake.Conversation.TranscriptSegments);
            Assert.IsEmpty(fake.Conversation.Session!.Answers);
            Assert.IsEmpty(fake.Work.Calls);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public void LongUtteranceExtractsActualQuestionAndRetainsSameUtteranceContext()
    {
        var time = DateTimeOffset.Now;
        var detector = new QuestionDetector();
        var chinese = new TranscriptSegment(Guid.NewGuid(), time, time,
            "我们目前有20个环境,应该如何制定数据防泄漏策略。", "Microphone", true);
        var result = detector.Detect(chinese, "", QuestionSensitivity.Medium, ConversationLanguage.Chinese);
        Assert.IsTrue(result.IsQuestion);
        Assert.AreEqual("应该如何制定数据防泄漏策略？", result.QuestionText);
        Assert.AreEqual("我们目前有20个环境,", result.ContextPrefix);
        var english = new TranscriptSegment(Guid.NewGuid(), time, time,
            "We have twenty environments. How should we design a DLP strategy.",
            "Microphone", true);
        var other = detector.Detect(english, "", QuestionSensitivity.Medium, ConversationLanguage.English);
        Assert.IsTrue(other.IsQuestion);
        Assert.AreEqual("How should we design a DLP strategy?", other.QuestionText);
        Assert.AreEqual("We have twenty environments.", other.ContextPrefix);
    }

    [TestMethod]
    public async Task ChineseWhisperQuestionStaysLocalUntilCardClickAndKeepsRelevantContext()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings { Language = ConversationLanguage.Chinese });
        var now = DateTimeOffset.Now;
        fake.Speech.EmitFinal("安全团队担心数据泄漏。", now.AddSeconds(-3));
        fake.Speech.EmitPartial("我们目前有20个环境");
        Assert.IsEmpty(fake.Work.Calls);
        var recognized = ChineseScriptConverter.ToSimplified(
            "我們目前有20個環境,應該如何制定數據防洩漏策略。");
        fake.Speech.EmitFinal(recognized, now);
        Assert.IsEmpty(fake.Work.Calls);
        var request = fake.Conversation.AskFromTranscript(fake.Conversation.TranscriptSegments.Last().Id);
        await WaitUntil(() => fake.Work.Calls.Count == 1);
        Assert.AreEqual(recognized, request.Question);
        StringAssert.Contains(request.ContextUsed, "安全团队担心数据泄漏");
        var prompt = fake.Work.Calls.ToArray()[0].Prompt;
        StringAssert.Contains(prompt, "QUESTION:");
        StringAssert.Contains(prompt, request.Question);
        StringAssert.Contains(prompt, "我们目前有20个环境");
        Assert.HasCount(2, fake.Conversation.TranscriptSegments);
        await fake.Conversation.EndConversationAsync();
    }

    [TestMethod]
    public void BufferExpiresWithoutRemovingVisibleTranscript()
    {
        var transcript = new TranscriptEngine();
        var buffer = new ConversationBuffer(TimeSpan.FromMinutes(1), 40);
        var start = DateTimeOffset.Now;
        foreach (var (text, offset) in new[] { ("Old line", 0), ("Recent line", 65) })
        {
            var segment = transcript.CommitFinal(new SpeechText(text, start.AddSeconds(offset),
                start.AddSeconds(offset)));
            buffer.Add(segment);
        }
        Assert.AreEqual("Recent line", buffer.GetRecentContext(start.AddSeconds(65)));
        Assert.HasCount(2, transcript.Segments);
        Assert.IsEmpty(buffer.GetRecentContext(start.AddMinutes(3)));
    }

    [TestMethod]
    public void DuplicateFilterSuppressesGrowingQuestionsButNotNewOnes()
    {
        var filter = new DuplicateQuestionFilter();
        var t = DateTimeOffset.Now;
        Assert.IsTrue(filter.Accept("How does Copilot work", t));
        Assert.IsFalse(filter.Accept("How does Copilot work with SAP?", t.AddSeconds(5)));
        Assert.IsTrue(filter.Accept("What is licensing?", t.AddSeconds(6)));
        Assert.IsTrue(filter.Accept("How does Copilot work?", t.AddMinutes(2)));
    }

    [TestMethod]
    public void CliJsonResponseKeepsAnswerAndConversation()
    {
        var answer = WorkIqResponseParser.Parse(
            """{"isError":false,"response":"Suggested response [1]","conversationId":"abc","artifacts":[{"name":"Answer","parts":[{"text":"Suggested response [1]"}]}]}""");
        Assert.AreEqual("Suggested response [1]", answer.Text);
        Assert.AreEqual("abc", answer.ConversationId);
        Assert.ThrowsExactly<WorkIqException>(() =>
            WorkIqResponseParser.Parse("""{"isError":true,"response":"not available"}"""));
    }

    [TestMethod]
    public void AnswerSectionsPreserveCitationsAndFallback()
    {
        var sections = AnswerSections.Parse(
            "SUGGESTED ANSWER\nUse a layered policy [1].\n\nKEY POINTS\n- Start small.\nSOURCES / CONTEXT\n[1] Policy note");
        Assert.AreEqual("Use a layered policy [1].", sections.SuggestedAnswer);
        Assert.AreEqual("- Start small.", sections.KeyPoints);
        Assert.AreEqual("[1] Policy note", sections.Sources);
        Assert.AreEqual("Plain response", AnswerSections.Parse("Plain response").SuggestedAnswer);
    }

    [TestMethod]
    public async Task AuthenticationRequiresARealWorkIqAnswer()
    {
        var workIq = new FakeWorkIq();
        await AuthenticationVerifier.VerifyAsync(workIq);
        Assert.HasCount(1, workIq.Calls);
        Assert.AreEqual(AuthenticationVerifier.Probe, workIq.Calls.ToArray()[0].Prompt);
        Assert.IsNull(workIq.Calls.ToArray()[0].ConversationId);
        workIq.Reply = "Work IQ unavailable";
        await Assert.ThrowsExactlyAsync<WorkIqException>(
            () => AuthenticationVerifier.VerifyAsync(workIq));
    }

    [TestMethod]
    public void ContextBuilderDoesNotInventGrounding()
    {
        var prompt = new ContextBuilder(new PromptBuilder()).Build("How?", "Previous conversation",
            new ConversationSettings { AnswerLanguage = ConversationLanguage.Chinese });
        StringAssert.Contains(prompt, "Previous conversation");
        StringAssert.Contains(prompt, "How?");
        StringAssert.Contains(prompt, "Answer in Chinese.");
        StringAssert.Contains(prompt, "untrusted context");
        StringAssert.Contains(prompt, "## SUGGESTED ANSWER");
        StringAssert.Contains(prompt, "Markdown table");
        StringAssert.Contains(prompt, "![meaningful alt text]");
    }

    [TestMethod]
    public async Task ListeningContinuesWhileFirstAnswerRunsAndSecondQuestionQueues()
    {
        var fake = new Harness();
        fake.Work.BlockFirst = true;
        fake.Conversation.StartConversation(new ConversationSettings { Language = ConversationLanguage.English });
        var t = DateTimeOffset.Now;
        fake.Speech.EmitFinal("We have twenty Power Platform environments.", t);
        fake.Speech.EmitFinal("Most apps are built by citizen developers.", t.AddSeconds(2));
        fake.Speech.EmitFinal("Security is worried about data leakage.", t.AddSeconds(4));
        Assert.IsEmpty(fake.Work.Calls);

        fake.Speech.EmitFinal("How should we design the DLP strategy?", t.AddSeconds(6));
        fake.Conversation.AskFromTranscript(fake.Conversation.TranscriptSegments.Last().Id);
        await WaitUntil(() => fake.Work.Calls.Count == 1);
        var first = fake.Conversation.Session!.Answers[0];
        Assert.AreEqual(QuestionStatus.Processing, first.Status);
        StringAssert.Contains(fake.Work.Calls.ToArray()[0].Prompt, "Security is worried");

        fake.Audio.Emit();
        fake.Speech.EmitFinal("We also need environments for testing.", t.AddSeconds(8));
        fake.Speech.EmitFinal("Does Copilot Studio support this?", t.AddSeconds(10));
        fake.Conversation.AskFromTranscript(fake.Conversation.TranscriptSegments.Last().Id);
        Assert.HasCount(6, fake.Conversation.TranscriptSegments);
        Assert.AreEqual(1, fake.Audio.StartCount);
        Assert.AreEqual(0, fake.Audio.StopCount);
        Assert.AreEqual(1, fake.Speech.AudioChunks);
        Assert.HasCount(1, fake.Work.Calls);
        Assert.HasCount(2, fake.Conversation.Session.Answers);
        Assert.IsEmpty(fake.Conversation.SuggestedQuestions);

        fake.Work.ReleaseFirst(new WorkIqAnswer("DLP answer", [], "conversation-123"));
        await WaitUntil(() => fake.Work.Calls.Count == 2);
        Assert.AreEqual("conversation-123", fake.Work.Calls.ToArray()[1].ConversationId);
        StringAssert.Contains(fake.Work.Calls.ToArray()[1].Prompt, "environments for testing");
        await WaitUntil(() => fake.Conversation.Session!.Answers[1].Status == QuestionStatus.Completed);
        Assert.AreEqual("DLP answer", first.Answer);
        await fake.Conversation.EndConversationAsync();
        Assert.IsEmpty(fake.Conversation.TranscriptSegments);
    }

    [TestMethod]
    public async Task PausingStopsSpeechUntilResumeWithoutPerUtteranceRequests()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings { Language = ConversationLanguage.English });
        fake.Conversation.Pause();
        fake.Speech.EmitFinal("How should we design DLP?", DateTimeOffset.Now);
        Assert.IsEmpty(fake.Conversation.TranscriptSegments);
        Assert.AreEqual(1, fake.Audio.StopCount);
        Assert.AreEqual(1, fake.Speech.StopCount);
        fake.Conversation.Resume();
        fake.Speech.EmitFinal("What is the DLP strategy?", DateTimeOffset.Now);
        Assert.IsEmpty(fake.Work.Calls);
        fake.Conversation.AskFromTranscript(fake.Conversation.TranscriptSegments.Single().Id);
        await WaitUntil(() => fake.Work.Calls.Count == 1);
        Assert.AreEqual(2, fake.Audio.StartCount);
        await fake.Conversation.EndConversationAsync();
    }

    [TestMethod]
    public async Task OfflineModelInitializesBeforeMicrophoneCaptureStarts()
    {
        var fake = new Harness();
        fake.Speech.BeforeStart = () =>
        {
            Assert.AreEqual(fake.Speech.StartCount, fake.Audio.StartCount);
            Assert.IsFalse(fake.Conversation.IsListening);
        };
        fake.Conversation.StartConversation(new ConversationSettings());
        fake.Audio.Emit();
        Assert.AreEqual(1, fake.Speech.AudioChunks);
        fake.Conversation.Pause();
        fake.Conversation.Resume();
        fake.Audio.Emit();
        Assert.AreEqual(2, fake.Speech.AudioChunks);
        await fake.Conversation.EndConversationAsync();
    }

    [TestMethod]
    public void SpeechStartupFailureStopsMicrophoneAndKeepsConversationRestartable()
    {
        var fake = new Harness();
        fake.Speech.StartError = new InvalidOperationException("Local recognizer unavailable");
        Assert.ThrowsExactly<InvalidOperationException>(
            () => fake.Conversation.StartConversation(new ConversationSettings()));
        Assert.IsNull(fake.Conversation.Session);
        Assert.AreEqual(1, fake.Audio.StopCount);
        Assert.AreEqual(ConversationUiState.Error, fake.Conversation.State);
        fake.Speech.StartError = null;
        fake.Conversation.StartConversation(new ConversationSettings());
        Assert.IsNotNull(fake.Conversation.Session);
        fake.Conversation.EndConversationAsync().GetAwaiter().GetResult();
    }

    [TestMethod]
    public async Task OfflineQuestionRemainsRetryableAndDoesNotStopTranscript()
    {
        var fake = new Harness();
        fake.Network.IsAvailable = false;
        fake.Conversation.StartConversation(new ConversationSettings());
        var request = fake.Conversation.AskManually("How should we design DLP?");
        await WaitUntil(() => request.Status == QuestionStatus.Failed &&
            fake.Conversation.State == ConversationUiState.Offline);
        Assert.IsEmpty(fake.Work.Calls);
        Assert.AreEqual(ConversationUiState.Offline, fake.Conversation.State);
        fake.Speech.EmitFinal("Transcript still works.", DateTimeOffset.Now);
        Assert.HasCount(1, fake.Conversation.TranscriptSegments);
        fake.Network.IsAvailable = true;
        Assert.IsTrue(fake.Conversation.Retry(request.Id));
        await WaitUntil(() => request.Status == QuestionStatus.Completed);
        await fake.Conversation.EndConversationAsync();
    }

    [TestMethod]
    public async Task ManualQuestionCanRecoverAfterSignInWithoutTranscriptDisclosure()
    {
        var fake = new Harness();
        fake.Auth.IsSignedIn = false;
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        fake.Speech.EmitPartial("What is licensing?");
        Assert.IsEmpty(fake.Work.Calls);
        fake.Speech.EmitFinal("We use twenty environments.", DateTimeOffset.Now.AddSeconds(-2));
        Assert.IsEmpty(fake.Work.Calls);
        var request = fake.Conversation.AskManually("What is licensing?");
        await WaitUntil(() => request.Status == QuestionStatus.Failed);
        Assert.IsEmpty(fake.Work.Calls);
        fake.Auth.IsSignedIn = true;
        Assert.IsTrue(fake.Conversation.Retry(request.Id));
        await WaitUntil(() => request.Status == QuestionStatus.Completed);
        Assert.HasCount(1, fake.Work.Calls);
        StringAssert.Contains(fake.Work.Calls.ToArray()[0].Prompt, "We use twenty environments.");
        await fake.Conversation.EndConversationAsync();
    }

    [TestMethod]
    public async Task SpeechFailureDoesNotTerminateConversationOrWorkQueue()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings());
        fake.Speech.EmitError(new InvalidOperationException("Recognition unavailable"));
        Assert.AreEqual(ConversationUiState.Error, fake.Conversation.State);
        fake.Speech.EmitFinal("Transcript still works.", DateTimeOffset.Now);
        Assert.HasCount(1, fake.Conversation.TranscriptSegments);
        fake.Audio.EmitError(new InvalidOperationException("Audio unavailable"));
        fake.Audio.EmitDeviceChange();
        await fake.Conversation.EndConversationAsync();
    }

    [TestMethod]
    public async Task DeviceFailureDuringEndStillStopsSpeechAndClearsContext()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings());
        fake.Speech.EmitFinal("Retained only during conversation.", DateTimeOffset.Now);
        fake.Audio.StopError = new InvalidOperationException("Device disappeared");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(fake.Conversation.EndConversationAsync);
        Assert.AreEqual(1, fake.Speech.StopCount);
        Assert.IsEmpty(fake.Conversation.TranscriptSegments);
        Assert.AreEqual(ConversationUiState.Idle, fake.Conversation.State);
    }

    [TestMethod]
    public async Task ClickingTranscriptQueuesOnceThenReusesCompletedReply()
    {
        var fake = new Harness();
        fake.Work.BlockFirst = true;
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        fake.Speech.EmitFinal("我们目前有二十个环境。", DateTimeOffset.Now);
        var segment = fake.Conversation.TranscriptSegments.Single();
        Assert.IsNull(fake.Conversation.GetAnswerForTranscript(segment.Id));
        var request = fake.Conversation.AskFromTranscript(segment.Id);
        Assert.AreEqual(segment.Id, request.TranscriptSegmentId);
        Assert.AreEqual(segment.Text, request.Question);
        Assert.IsTrue(request.IsManual);
        await WaitUntil(() => fake.Work.Calls.Count == 1);
        Assert.AreSame(request, fake.Conversation.AskFromTranscript(segment.Id));
        Assert.HasCount(1, fake.Conversation.Session!.Answers);
        fake.Work.ReleaseFirst(new WorkIqAnswer("建议先建立环境清单。", [], "test"));
        await WaitUntil(() => request.Status == QuestionStatus.Completed);
        Assert.AreSame(request, fake.Conversation.AskFromTranscript(segment.Id));
        Assert.HasCount(1, fake.Work.Calls);
        await fake.Conversation.EndConversationAsync();
    }

    [TestMethod]
    public async Task FinalQuestionWaitsForExplicitCardClickWithStableTranscriptCorrelation()
    {
        var fake = new Harness();
        fake.Work.BlockFirst = true;
        fake.Conversation.StartConversation(new ConversationSettings());
        fake.Speech.EmitFinal("我们有二十个环境，如何设置DLP？", DateTimeOffset.Now);
        var segment = fake.Conversation.TranscriptSegments.Single();
        Assert.IsEmpty(fake.Work.Calls);
        Assert.IsEmpty(fake.Conversation.Session!.Answers);
        var request = fake.Conversation.AskFromTranscript(segment.Id);
        await WaitUntil(() => fake.Work.Calls.Count == 1);
        Assert.AreEqual(segment.Id, request.TranscriptSegmentId);
        Assert.AreEqual(segment.Text, request.Question);
        Assert.AreSame(request, fake.Conversation.AskFromTranscript(segment.Id));
        Assert.HasCount(1, fake.Work.Calls);
        await fake.Conversation.EndConversationAsync();
    }

    [TestMethod]
    public async Task CardClickDuringFinalCommitQueuesExactlyOnce()
    {
        var fake = new Harness();
        fake.Work.BlockFirst = true;
        fake.Conversation.StartConversation(new ConversationSettings());
        fake.Conversation.TranscriptUpdated += partial =>
        {
            if (partial is null && fake.Conversation.TranscriptSegments.LastOrDefault() is { } segment &&
                fake.Conversation.GetAnswerForTranscript(segment.Id) is null)
                fake.Conversation.AskFromTranscript(segment.Id);
        };
        fake.Speech.EmitFinal("如何设计DLP策略？", DateTimeOffset.Now);
        await WaitUntil(() => fake.Work.Calls.Count == 1);
        Assert.HasCount(1, fake.Conversation.Session!.Answers);
        await fake.Conversation.EndConversationAsync();
    }

    [TestMethod]
    public async Task ClickingFailedCardExplicitlyRetriesSameRequestEvenWhenPaused()
    {
        var fake = new Harness();
        fake.Network.IsAvailable = false;
        fake.Conversation.StartConversation(new ConversationSettings());
        fake.Speech.EmitFinal("如何配置安全策略？", DateTimeOffset.Now);
        var segment = fake.Conversation.TranscriptSegments.Single();
        var request = fake.Conversation.AskFromTranscript(segment.Id);
        await WaitUntil(() => request.Status == QuestionStatus.Failed);
        fake.Conversation.Pause();
        fake.Network.IsAvailable = true;
        fake.Work.BlockFirst = true;
        Assert.AreSame(request, fake.Conversation.AskFromTranscript(segment.Id));
        await WaitUntil(() => fake.Work.Calls.Count == 1);
        var clicks = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => fake.Conversation.AskFromTranscript(segment.Id))));
        Assert.IsTrue(clicks.All(item => item.Id == request.Id));
        Assert.HasCount(1, fake.Conversation.Session!.Answers);
        Assert.HasCount(1, fake.Work.Calls);
        fake.Work.ReleaseFirst(new WorkIqAnswer("配置成功。", [], "test"));
        await WaitUntil(() => request.Status == QuestionStatus.Completed);
        Assert.IsFalse(fake.Conversation.IsListening);
        await fake.Conversation.EndConversationAsync();
    }

    [TestMethod]
    public async Task HistoricalCardUsesContextFromItsTimeNotLaterConversation()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        var old = DateTimeOffset.Now.AddMinutes(-10);
        fake.Speech.EmitFinal("背景：当时有二十个环境。", old.AddSeconds(-10));
        fake.Speech.EmitFinal("如何设计DLP策略？", old);
        var selected = fake.Conversation.TranscriptSegments.Last();
        fake.Speech.EmitFinal("后来的无关讨论不应发送。", DateTimeOffset.Now);
        var request = fake.Conversation.AskFromTranscript(selected.Id);
        StringAssert.Contains(request.ContextUsed, "当时有二十个环境");
        Assert.IsFalse(request.ContextUsed.Contains("后来的", StringComparison.Ordinal));
        await fake.Conversation.EndConversationAsync();
    }

    [TestMethod]
    public async Task ClearedAndPreviousConversationCardsCannotSendNewRequests()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        fake.Speech.EmitFinal("讨论环境清单。", DateTimeOffset.Now);
        var segment = fake.Conversation.TranscriptSegments.Single();
        fake.Conversation.ClearTranscript();
        Assert.ThrowsExactly<InvalidOperationException>(() => fake.Conversation.AskFromTranscript(segment.Id));
        await fake.Conversation.EndConversationAsync();
        Assert.ThrowsExactly<InvalidOperationException>(() => fake.Conversation.AskFromTranscript(segment.Id));
        Assert.IsEmpty(fake.Work.Calls);
    }

    [TestMethod]
    public async Task EmptyWorkIqAnswerDoesNotMarkCardAsCompleted()
    {
        var fake = new Harness();
        fake.Work.Reply = "";
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        fake.Speech.EmitFinal("讨论环境清单。", DateTimeOffset.Now);
        var request = fake.Conversation.AskFromTranscript(fake.Conversation.TranscriptSegments.Single().Id);
        await WaitUntil(() => request.Status == QuestionStatus.Failed);
        StringAssert.Contains(request.Error!, "empty answer");
        await fake.Conversation.EndConversationAsync();
    }

    [TestMethod]
    public void TranscriptCardColorsRepresentAllReplyStates()
    {
        Assert.AreEqual(0xFFFFFFFFu, TranscriptCardAppearance.For(null).BackgroundArgb);
        Assert.AreEqual(0xFFFFF4CCu, TranscriptCardAppearance.For(QuestionStatus.Pending).BackgroundArgb);
        Assert.AreEqual(0xFFFFF4CCu, TranscriptCardAppearance.For(QuestionStatus.Processing).BackgroundArgb);
        Assert.AreEqual(0xFFE4F4E7u, TranscriptCardAppearance.For(QuestionStatus.Completed).BackgroundArgb);
        Assert.AreEqual(0xFFEADDD3u, TranscriptCardAppearance.For(QuestionStatus.Failed).BackgroundArgb);
        StringAssert.Contains(TranscriptCardAppearance.For(QuestionStatus.Failed).StatusText, "重试");
        StringAssert.Contains(TranscriptCardAppearance.For(null).StatusText, "未获取");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTimeOffset.Now.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.Now > deadline) Assert.Fail("Timed out waiting for background queue.");
            await Task.Delay(10);
        }
    }

    private sealed class Harness
    {
        public FakeAudio Audio { get; } = new();
        public FakeSpeech Speech { get; } = new();
        public FakeWorkIq Work { get; } = new();
        public FakeNetwork Network { get; } = new();
        public FakeAuth Auth { get; } = new();
        public ManualTimeProvider Clock { get; } = new();
        public ConversationSessionManager Conversation { get; }

        public Harness() => Conversation = new ConversationSessionManager(Audio, Speech, Auth,
            Network, Work, new TranscriptEngine(), new ContextBuilder(new PromptBuilder()), Clock);
    }

    private sealed class FakeNetwork : INetworkStatus
    {
        public bool IsAvailable { get; set; } = true;
    }

    private sealed class FakeAuth : IAuthenticationService
    {
        public bool IsSignedIn { get; set; } = true;
        public Task SignInAsync(CancellationToken token = default) => Task.CompletedTask;
    }

    private sealed class FakeAudio : IAudioCaptureService
    {
        public event Action<byte[]>? AudioDataAvailable;
        public event Action<Exception>? AudioError;
        public event Action? AudioStarted;
        public event Action? AudioStopped;
        public event Action? AudioDeviceChanged;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public Exception? StopError { get; set; }
        public IReadOnlyList<AudioDevice> ListDevices() => [new("0", "Fake microphone")];
        public void Start(string? deviceId) { StartCount++; AudioStarted?.Invoke(); }
        public void Stop()
        {
            StopCount++;
            if (StopError is not null) throw StopError;
            AudioStopped?.Invoke();
        }
        public void Emit() => AudioDataAvailable?.Invoke([1, 2]);
        public void EmitError(Exception error) => AudioError?.Invoke(error);
        public void EmitDeviceChange() => AudioDeviceChanged?.Invoke();
    }

    private sealed class FakeSpeech : ISpeechRecognitionService
    {
        public event Action<SpeechText>? PartialTranscriptReceived;
        public event Action<SpeechText>? FinalTranscriptReceived;
        public event Action? SpeechStarted;
        public event Action? SpeechEnded;
        public event Action<Exception>? RecognitionError;
        public int AudioChunks { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public Action? BeforeStart { get; set; }
        public Exception? StartError { get; set; }
        public IReadOnlyList<ConversationLanguage> AvailableLanguages() =>
            [ConversationLanguage.Chinese, ConversationLanguage.English];
        public void Start(ConversationLanguage language)
        {
            BeforeStart?.Invoke();
            if (StartError is not null) throw StartError;
            StartCount++;
            SpeechStarted?.Invoke();
        }
        public void Stop() { StopCount++; SpeechEnded?.Invoke(); }
        public void AcceptAudio(byte[] data) => AudioChunks++;
        public void EmitFinal(string text, DateTimeOffset at) =>
            FinalTranscriptReceived?.Invoke(new SpeechText(text, at, at.AddSeconds(1)));
        public void EmitPartial(string text) =>
            PartialTranscriptReceived?.Invoke(new SpeechText(text, DateTimeOffset.Now, DateTimeOffset.Now));
        public void EmitError(Exception error) => RecognitionError?.Invoke(error);
    }

    private sealed class FakeWorkIq : IWorkIqClient
    {
        private readonly TaskCompletionSource<WorkIqAnswer> _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;
        public ConcurrentQueue<(string Prompt, string? ConversationId)> Calls { get; } = new();
        public bool BlockFirst { get; set; }
        public int? BlockCall { get; set; }
        public string Reply { get; set; } = "4";
        public string AnalysisReply { get; set; } =
            """{"questions":[{"question":"How should we design DLP?","reason":"The conversation needs a data protection strategy."}]}""";
        public ConcurrentQueue<WorkIqAnswer> Responses { get; } = new();
        public Task<WorkIqAnswer> AskAsync(string prompt, string? conversationId, CancellationToken token)
        {
            Calls.Enqueue((prompt, conversationId));
            var call = Interlocked.Increment(ref _count);
            if (call == BlockCall || call == 1 && BlockFirst) return _first.Task.WaitAsync(token);
            if (Responses.TryDequeue(out var response)) return Task.FromResult(response);
            var analysis = prompt.Contains("QUESTION DISCOVERY ONLY", StringComparison.Ordinal);
            return Task.FromResult(new WorkIqAnswer(analysis ? AnalysisReply : Reply, [],
                conversationId ?? (analysis ? "analysis-conversation" : "answer-conversation")));
        }
        public void ReleaseFirst(WorkIqAnswer answer) => _first.TrySetResult(answer);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }
}
