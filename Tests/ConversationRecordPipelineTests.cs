using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Persistence;

namespace ConversationAssistant.Tests;

public sealed partial class PipelineTests
{
    [TestMethod]
    public async Task FinalTranscriptNotificationsExposeCommittedGroupableRecords()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        TranscriptGroup? group = null;
        fake.Conversation.TranscriptUpdated += partial =>
        {
            if (partial is null && group is null && fake.Conversation.TranscriptSegments.Count == 1)
                group = fake.Conversation.CreateTranscriptGroup();
        };
        try
        {
            EmitFinals(fake, 1);
            Assert.IsNotNull(group);
            Assert.AreEqual(fake.Conversation.TranscriptSegments.Single().Id, group.TranscriptIds.Single());
            Assert.AreEqual(group.Id, fake.Conversation.Groups.Single().Id);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task GroupsTakeOnlyUngroupedFinalTurnsAndKeepNamesAndCollapseState()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            EmitFinals(fake, 2);
            fake.Speech.EmitPartial("Not committed");
            var first = fake.Conversation.CreateTranscriptGroup();
            Assert.AreEqual(1, first.Number);
            Assert.IsNull(first.Name);
            Assert.HasCount(2, first.TranscriptIds);
            EmitFinals(fake, 1, 3);
            var second = fake.Conversation.CreateTranscriptGroup();
            Assert.AreEqual(2, second.Number);
            Assert.HasCount(1, second.TranscriptIds);
            Assert.IsFalse(first.TranscriptIds.Intersect(second.TranscriptIds).Any());
            Assert.ThrowsExactly<InvalidOperationException>(() => fake.Conversation.CreateTranscriptGroup());
            fake.Conversation.RenameTranscriptGroup(first.Id, "  Budget / 第一阶段  ");
            fake.Conversation.SetGroupExpanded(first.Id, false);
            Assert.AreEqual("Budget / 第一阶段", first.Name);
            Assert.IsFalse(first.IsExpanded);
            Assert.ThrowsExactly<InvalidOperationException>(() => fake.Conversation.RenameTranscriptGroup(first.Id, " "));
            Assert.ThrowsExactly<InvalidOperationException>(() => fake.Conversation.RenameTranscriptGroup(first.Id, new string('x', 121)));
            Assert.IsEmpty(fake.Work.Calls);
            Assert.HasCount(3, fake.Conversation.TranscriptSegments);
            Assert.IsTrue(fake.Conversation.IsListening);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task GroupAnalysisSendsOnlyItsMembersAndDoesNotConsumeTheFullChatBatch()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            EmitFinals(fake, 2);
            var group = fake.Conversation.CreateTranscriptGroup();
            fake.Conversation.RenameTranscriptGroup(group.Id, "Design");
            EmitFinals(fake, 4, 3);
            var analysis = fake.Conversation.AnalyzeGroup(group.Id);
            await WaitUntil(() => analysis.Status == QuestionStatus.Completed);
            Assert.HasCount(2, analysis.Transcript);
            Assert.AreEqual(group.Id, analysis.GroupId);
            Assert.AreEqual("Design", analysis.GroupName);
            Assert.AreEqual(ConversationAnalysisTrigger.Group, analysis.Trigger);
            var sent = AnalysisTranscript(fake.Work.Calls.Single().Prompt);
            StringAssert.Contains(sent, "Statement 1");
            StringAssert.Contains(sent, "Statement 2");
            Assert.IsFalse(sent.Contains("Statement 3", StringComparison.Ordinal));
            Assert.AreEqual(6, fake.Conversation.PendingAnalysisCount);
            Assert.HasCount(1, analysis.Questions);
            Assert.AreEqual(fake.Work.AnalysisReply, analysis.Response);
            EmitFinals(fake, 1, 7);
            await WaitUntil(() => fake.Conversation.Analyses.Count == 2 &&
                fake.Conversation.Analysis?.Status == QuestionStatus.Completed);
            Assert.HasCount(7, fake.Conversation.Analysis!.Transcript);
            Assert.IsNull(fake.Conversation.Analysis.GroupId);
            Assert.AreEqual(0, fake.Conversation.PendingAnalysisCount);
            Assert.HasCount(2, analysis.Transcript);
            Assert.HasCount(1, analysis.Questions);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task SeparateGroupAnalysesShareTheQueueButNotTheirContextOrQuestionLists()
    {
        var fake = new Harness();
        fake.Work.BlockFirst = true;
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            EmitFinals(fake, 1);
            var firstGroup = fake.Conversation.CreateTranscriptGroup();
            EmitFinals(fake, 1, 2);
            var secondGroup = fake.Conversation.CreateTranscriptGroup();
            var first = fake.Conversation.AnalyzeGroup(firstGroup.Id);
            await WaitUntil(() => fake.Work.Calls.Count == 1);
            var second = fake.Conversation.AnalyzeGroup(secondGroup.Id);
            Assert.AreSame(first, fake.Conversation.AnalyzeGroup(firstGroup.Id));
            Assert.HasCount(2, fake.Conversation.Analyses);
            Assert.HasCount(1, fake.Work.Calls);
            Assert.AreEqual(QuestionStatus.Pending, second.Status);
            EmitFinals(fake, 1, 3);
            fake.Work.ReleaseFirst(new WorkIqAnswer("""{"questions":[{"question":"First need?","reason":"First scope."}]}""", [], null));
            await WaitUntil(() => second.Status == QuestionStatus.Completed);
            Assert.AreEqual("First need?", first.Questions.Single().Question);
            Assert.AreNotEqual(first.Questions.Single().Id, second.Questions.Single().Id);
            var calls = fake.Work.Calls.ToArray();
            Assert.IsFalse(calls[1].Prompt.Contains("First need?", StringComparison.Ordinal));
            Assert.IsFalse(AnalysisTranscript(calls[1].Prompt).Contains("Statement 1", StringComparison.Ordinal));
            Assert.IsFalse(AnalysisTranscript(calls[1].Prompt).Contains("Statement 3", StringComparison.Ordinal));
            Assert.HasCount(3, fake.Conversation.TranscriptSegments);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task EveryAnalysisKeepsItsOwnSnapshotWhileExistingQuestionsRemainDeduplicated()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            EmitFinals(fake, 1);
            var first = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => first.Status == QuestionStatus.Completed);
            var original = first.Questions.Single();
            fake.Work.AnalysisReply = """{"questions":[{"question":"Who owns deployment?","reason":"New need."}]}""";
            EmitFinals(fake, 1, 2);
            var second = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => second.Status == QuestionStatus.Completed);
            Assert.HasCount(2, fake.Conversation.Analyses);
            Assert.AreSame(second, fake.Conversation.Analysis);
            Assert.HasCount(1, first.Transcript);
            Assert.HasCount(1, first.Questions);
            Assert.HasCount(2, second.Transcript);
            Assert.HasCount(2, second.Questions);
            Assert.AreSame(original, second.Questions[0]);
            fake.Conversation.ClearTranscript();
            Assert.IsNull(fake.Conversation.Analysis);
            Assert.HasCount(2, fake.Conversation.Analyses);
            Assert.HasCount(1, first.Questions);
            var answer = fake.Conversation.AskSuggestedQuestion(original.Id);
            await WaitUntil(() => answer.Status == QuestionStatus.Completed);
            Assert.AreEqual(original.ContextUsed, answer.ContextUsed);
            Assert.IsFalse(answer.ContextUsed.Contains("Statement 2", StringComparison.Ordinal));
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task EndingRetainsTheWorkspaceAndAllowsExplicitAnalysisWithoutStartingAudio()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        EmitFinals(fake, 2);
        var group = fake.Conversation.CreateTranscriptGroup();
        var first = fake.Conversation.AnalyzeGroup(group.Id);
        await WaitUntil(() => first.Status == QuestionStatus.Completed);
        var answer = fake.Conversation.AskSuggestedQuestion(first.Questions.Single().Id);
        await WaitUntil(() => answer.Status == QuestionStatus.Completed);
        var session = fake.Conversation.Session!;
        await fake.Conversation.EndConversationAsync();
        Assert.IsNull(fake.Conversation.Session);
        Assert.AreSame(session, fake.Conversation.CurrentConversation);
        Assert.HasCount(2, fake.Conversation.TranscriptSegments);
        Assert.HasCount(1, fake.Conversation.Groups);
        Assert.AreSame(first, fake.Conversation.Analysis);
        Assert.AreSame(answer, session.Answers.Single());
        var second = fake.Conversation.AnalyzeGroup(group.Id);
        await WaitUntil(() => second.Status == QuestionStatus.Completed);
        Assert.IsFalse(fake.Conversation.IsListening);
        Assert.AreEqual(1, fake.Audio.StartCount);
        Assert.AreEqual(1, fake.Speech.StartCount);
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            Assert.AreNotEqual(session.SessionId, fake.Conversation.Session!.SessionId);
            Assert.IsEmpty(fake.Conversation.Analyses);
            Assert.IsEmpty(fake.Conversation.Groups);
            Assert.IsEmpty(fake.Conversation.TranscriptSegments);
            Assert.HasCount(2, session.Analyses);
            Assert.HasCount(1, session.Answers);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task CancellingAQueuePreservesAllAnalysisAttemptsAndPreventsLateMutation()
    {
        var fake = new Harness();
        fake.Work.BlockFirst = true;
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        EmitFinals(fake, 1);
        var group = fake.Conversation.CreateTranscriptGroup();
        var first = fake.Conversation.AnalyzeGroup(group.Id);
        await WaitUntil(() => fake.Work.Calls.Count == 1);
        var second = fake.Conversation.AnalyzeConversation();
        var answer = fake.Conversation.AskManually("Pending question");
        await fake.Conversation.EndConversationAsync();
        Assert.AreEqual(QuestionStatus.Cancelled, first.Status);
        Assert.AreEqual(QuestionStatus.Cancelled, second.Status);
        Assert.AreEqual(QuestionStatus.Cancelled, answer.Status);
        Assert.HasCount(2, fake.Conversation.Analyses);
        fake.Work.ReleaseFirst(new WorkIqAnswer(fake.Work.AnalysisReply, [], null));
        Assert.IsEmpty(first.Questions);
        Assert.IsEmpty(second.Questions);
        Assert.HasCount(1, fake.Work.Calls);
    }

    [TestMethod]
    public async Task JsonFileExistsBeforeCaptureAndEachNewConversationGetsASeparateRecord()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"conversation-pipeline-{Guid.NewGuid():N}");
        var store = new JsonConversationArchiveStore(directory);
        var fake = new Harness(store);
        var existedAtCapture = false;
        fake.Audio.AudioStarted += () => existedAtCapture = File.Exists(fake.Conversation.RecordPath);
        try
        {
            fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
            Assert.IsTrue(existedAtCapture);
            EmitFinals(fake, 2);
            var group = fake.Conversation.CreateTranscriptGroup();
            fake.Conversation.RenameTranscriptGroup(group.Id, "需求确认");
            fake.Conversation.SetGroupExpanded(group.Id, false);
            var analysis = fake.Conversation.AnalyzeGroup(group.Id);
            await WaitUntil(() => analysis.Status == QuestionStatus.Completed);
            var answer = fake.Conversation.AskSuggestedQuestion(analysis.Questions.Single().Id);
            await WaitUntil(() => answer.Status == QuestionStatus.Completed);
            await fake.Conversation.EndConversationAsync();
            var firstPath = fake.Conversation.RecordPath!;
            var savedBytes = await File.ReadAllBytesAsync(firstPath);
            var saved = (await store.LoadAsync(firstPath)).Restore();
            Assert.HasCount(2, saved.TranscriptSegments);
            Assert.AreEqual("需求确认", saved.Groups.Single().Name);
            Assert.IsFalse(saved.Groups.Single().IsExpanded);
            Assert.AreEqual(analysis.Id, saved.Analyses.Single().Id);
            Assert.AreEqual(answer.Answer, saved.Answers.Single().Answer);
            fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
            Assert.AreNotEqual(firstPath, fake.Conversation.RecordPath);
            Assert.HasCount(2, Directory.GetFiles(directory, "*.json"));
            CollectionAssert.AreEqual(savedBytes, await File.ReadAllBytesAsync(firstPath));
        }
        finally
        {
            await fake.Conversation.EndConversationAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task ImportRestoresGroupsAnalysisAndAnswersAsACopyWithoutRecordingOrSubmitting()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"conversation-import-{Guid.NewGuid():N}");
        var store = new JsonConversationArchiveStore(directory);
        var fake = new Harness(store);
        try
        {
            fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
            EmitFinals(fake, 2);
            var group = fake.Conversation.CreateTranscriptGroup();
            var analysis = fake.Conversation.AnalyzeGroup(group.Id);
            await WaitUntil(() => analysis.Status == QuestionStatus.Completed);
            var answer = fake.Conversation.AskSuggestedQuestion(analysis.Questions.Single().Id);
            await WaitUntil(() => answer.Status == QuestionStatus.Completed);
            var source = fake.Conversation.RecordPath!;
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fake.Conversation.ImportConversationAsync(source));
            await fake.Conversation.EndConversationAsync();
            var originalId = fake.Conversation.CurrentConversation!.SessionId;
            var originalBytes = await File.ReadAllBytesAsync(source);
            var calls = fake.Work.Calls.Count;
            await fake.Conversation.ImportConversationAsync(source);
            Assert.IsNull(fake.Conversation.Session);
            Assert.IsFalse(fake.Conversation.IsListening);
            Assert.AreNotEqual(originalId, fake.Conversation.CurrentConversation!.SessionId);
            Assert.AreNotEqual(source, fake.Conversation.RecordPath);
            Assert.IsFalse(fake.Conversation.CurrentConversation.Settings.AutomaticAnalysis);
            Assert.AreEqual(group.Id, fake.Conversation.Groups.Single().Id);
            Assert.AreEqual(analysis.Id, fake.Conversation.Analyses.Single().Id);
            Assert.AreEqual(answer.Id, fake.Conversation.CurrentConversation.Answers.Single().Id);
            Assert.HasCount(calls, fake.Work.Calls);
            Assert.AreEqual(1, fake.Audio.StartCount);
            CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(source));
            Assert.AreEqual(answer.Answer, fake.Conversation.AskSuggestedQuestion(analysis.Questions.Single().Id).Answer);
            Assert.HasCount(calls, fake.Work.Calls);
        }
        finally
        {
            await fake.Conversation.EndConversationAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task AFailedAutosaveRetainsRecordsAndBlocksReplacementUntilSaveSucceeds()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"conversation-save-error-{Guid.NewGuid():N}");
        var store = new SwitchableArchiveStore(new JsonConversationArchiveStore(directory));
        var fake = new Harness(store);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        fake.Conversation.ErrorOccurred += errors.Enqueue;
        try
        {
            fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
            var id = fake.Conversation.Session!.SessionId;
            store.FailWrites = true;
            EmitFinals(fake, 1);
            await Assert.ThrowsAsync<IOException>(fake.Conversation.FlushArchiveAsync);
            await Assert.ThrowsAsync<IOException>(fake.Conversation.EndConversationAsync);
            Assert.IsTrue(fake.Conversation.HasSaveError);
            Assert.HasCount(1, fake.Conversation.TranscriptSegments);
            Assert.IsFalse(fake.Conversation.IsListening);
            Assert.IsTrue(errors.Any(message => message.Contains("could not be saved", StringComparison.Ordinal)));
            Assert.Throws<IOException>(() => fake.Conversation.StartConversation(new ConversationSettings()));
            Assert.AreEqual(id, fake.Conversation.CurrentConversation!.SessionId);
            Assert.HasCount(1, fake.Conversation.TranscriptSegments);
            store.FailWrites = false;
            await fake.Conversation.SaveConversationAsync();
            Assert.IsFalse(fake.Conversation.HasSaveError);
            var restored = (await store.LoadAsync(fake.Conversation.RecordPath!)).Restore();
            Assert.HasCount(1, restored.TranscriptSegments);
            fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
            Assert.AreNotEqual(id, fake.Conversation.Session!.SessionId);
        }
        finally
        {
            store.FailWrites = false;
            await fake.Conversation.EndConversationAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task SnapshotValidationFailureDoesNotEscapeSpeechOrOverwriteTheLastValidFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"conversation-snapshot-error-{Guid.NewGuid():N}");
        var store = new JsonConversationArchiveStore(directory);
        var fake = new Harness(store);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        fake.Conversation.ErrorOccurred += errors.Enqueue;
        try
        {
            fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
            var id = fake.Conversation.Session!.SessionId;
            var path = fake.Conversation.RecordPath!;
            var savedBytes = await File.ReadAllBytesAsync(path);
            var published = false;
            fake.Conversation.TranscriptUpdated += partial =>
                published |= partial is null && fake.Conversation.TranscriptSegments.Count == 1;
            fake.Speech.EmitFinal(new string('x', ConversationArchiveLimits.MaximumTextLength + 1),
                DateTimeOffset.UtcNow);
            Assert.IsTrue(published);
            Assert.IsTrue(fake.Conversation.IsListening);
            Assert.IsTrue(fake.Conversation.HasSaveError);
            Assert.IsTrue(errors.Any(message => message.Contains("length limit", StringComparison.Ordinal)));
            await Assert.ThrowsAsync<InvalidDataException>(fake.Conversation.FlushArchiveAsync);
            CollectionAssert.AreEqual(savedBytes, await File.ReadAllBytesAsync(path));
            await Assert.ThrowsAsync<InvalidDataException>(fake.Conversation.EndConversationAsync);
            Assert.HasCount(1, fake.Conversation.TranscriptSegments);
            Assert.Throws<InvalidDataException>(() => fake.Conversation.StartConversation(new ConversationSettings()));
            Assert.AreEqual(id, fake.Conversation.CurrentConversation!.SessionId);
            fake.Conversation.ClearTranscript();
            await fake.Conversation.SaveConversationAsync();
            Assert.IsFalse(fake.Conversation.HasSaveError);
            Assert.IsEmpty((await store.LoadAsync(path)).Restore().TranscriptSegments);
        }
        finally
        {
            fake.Conversation.ClearTranscript();
            await fake.Conversation.EndConversationAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task FailedStartupRetainsCancelledQuestionsRatherThanStrandingPendingRecords()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"conversation-startup-error-{Guid.NewGuid():N}");
        var store = new JsonConversationArchiveStore(directory);
        var fake = new Harness(store);
        QuestionRequest? request = null;
        var publishedCancellation = false;
        fake.Conversation.AnswerUpdated += updated =>
            publishedCancellation |= updated.Status == QuestionStatus.Cancelled;
        fake.Speech.BeforeStart = () => request = fake.Conversation.AskManually("A question queued during startup.");
        fake.Speech.StartError = new InvalidOperationException("Simulated speech initialization failure.");
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false }));
            Assert.IsNotNull(request);
            Assert.AreEqual(QuestionStatus.Cancelled, request.Status);
            Assert.IsTrue(publishedCancellation);
            Assert.IsFalse(fake.Conversation.HasPendingWork);
            Assert.IsNull(fake.Conversation.Session);
            Assert.IsNotNull(fake.Conversation.CurrentConversation!.EndTime);
            Assert.IsEmpty(fake.Work.Calls);
            await fake.Conversation.FlushArchiveAsync();
            var restored = (await store.LoadAsync(fake.Conversation.RecordPath!)).Restore();
            Assert.AreEqual(request.Id, restored.Answers.Single().Id);
            Assert.AreEqual(QuestionStatus.Cancelled, restored.Answers.Single().Status);
        }
        finally
        {
            await fake.Conversation.EndConversationAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class SwitchableArchiveStore(IConversationArchiveStore inner) : IConversationArchiveStore
    {
        public bool FailWrites { get; set; }
        public string GetPath(Guid sessionId) => inner.GetPath(sessionId);
        public Task<string> SaveAsync(ConversationArchive archive, CancellationToken cancellationToken = default) =>
            FailWrites ? Task.FromException<string>(new IOException("Simulated disk failure.")) :
                inner.SaveAsync(archive, cancellationToken);
        public Task<ConversationArchive> LoadAsync(string path, CancellationToken cancellationToken = default) =>
            inner.LoadAsync(path, cancellationToken);
    }
}
