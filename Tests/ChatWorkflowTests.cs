using System.Xml.Linq;
using ConversationAssistant.Core.Authentication;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Persistence;
using ConversationAssistant_App.UI;

namespace ConversationAssistant.Tests;

public sealed partial class PipelineTests
{
    [TestMethod]
    [DataRow("active")]
    [DataRow("paused")]
    [DataRow("ended")]
    public async Task NewChatSavesPreviousWorkspaceBeforeStartingFreshCapture(string state)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"new-chat-{Guid.NewGuid():N}");
        var store = new JsonConversationArchiveStore(directory);
        var fake = new Harness(store);
        try
        {
            fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
            EmitFinals(fake, 2);
            var group = fake.Conversation.CreateTranscriptGroup();
            var analysis = fake.Conversation.AnalyzeGroup(group.Id);
            await WaitUntil(() => analysis.Status == QuestionStatus.Completed);
            var answer = fake.Conversation.AskManually("What should we confirm?");
            await WaitUntil(() => answer.Status == QuestionStatus.Completed);
            var oldId = fake.Conversation.CurrentConversation!.SessionId;
            var oldPath = fake.Conversation.RecordPath!;
            var calls = fake.Work.Calls.Count;
            if (state == "paused") fake.Conversation.Pause();
            if (state == "ended") await fake.Conversation.EndConversationAsync();
            var savedBeforeCapture = false;
            fake.Audio.AudioStarted += () =>
            {
                var previous = store.LoadAsync(oldPath).GetAwaiter().GetResult().Restore();
                savedBeforeCapture = previous.EndTime is not null && previous.TranscriptSegments.Count == 2 &&
                    previous.Groups.Single().Id == group.Id && previous.Answers.Single().Id == answer.Id;
            };
            await fake.Conversation.StartNewConversationAsync(new ConversationSettings { AutomaticAnalysis = false });
            Assert.IsTrue(savedBeforeCapture);
            Assert.IsTrue(fake.Conversation.IsListening);
            Assert.AreNotEqual(oldId, fake.Conversation.Session!.SessionId);
            Assert.AreNotEqual(oldPath, fake.Conversation.RecordPath);
            Assert.IsEmpty(fake.Conversation.TranscriptSegments);
            Assert.IsEmpty(fake.Conversation.Groups);
            Assert.IsEmpty(fake.Conversation.Analyses);
            Assert.IsEmpty(fake.Conversation.AnswerHistory);
            Assert.IsEmpty(fake.Conversation.SuggestedQuestions);
            Assert.IsEmpty(fake.Conversation.ProductGuides);
            Assert.IsNull(fake.Conversation.CurrentAssistanceAnalysis);
            Assert.HasCount(calls, fake.Work.Calls);
            Assert.AreEqual(2, fake.Audio.StartCount);
        }
        finally
        {
            await fake.Conversation.EndConversationAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task NewChatSaveFailureRetainsOldWorkspaceAndDoesNotStartCapture()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"new-chat-failure-{Guid.NewGuid():N}");
        var store = new SwitchableArchiveStore(new JsonConversationArchiveStore(directory));
        var fake = new Harness(store);
        try
        {
            fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
            EmitFinals(fake, 1);
            var oldId = fake.Conversation.Session!.SessionId;
            store.FailWrites = true;
            await Assert.ThrowsAsync<IOException>(() => fake.Conversation.StartNewConversationAsync(new ConversationSettings()));
            Assert.AreEqual(oldId, fake.Conversation.CurrentConversation!.SessionId);
            Assert.HasCount(1, fake.Conversation.TranscriptSegments);
            Assert.AreEqual(1, fake.Audio.StartCount);
            Assert.IsFalse(fake.Conversation.IsListening);
            store.FailWrites = false;
            await fake.Conversation.SaveConversationAsync();
            await fake.Conversation.StartNewConversationAsync(new ConversationSettings { AutomaticAnalysis = false });
            Assert.AreNotEqual(oldId, fake.Conversation.Session!.SessionId);
        }
        finally
        {
            store.FailWrites = false;
            await fake.Conversation.EndConversationAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task NewChatCancelsOldRequestsWithoutPublishingIntoTheNewWorkspace()
    {
        var fake = new Harness();
        fake.Work.BlockFirst = true;
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            EmitFinals(fake, 1);
            var oldAnswer = fake.Conversation.AskManually("Old question");
            await WaitUntil(() => oldAnswer.Status == QuestionStatus.Processing);
            await fake.Conversation.StartNewConversationAsync(new ConversationSettings { AutomaticAnalysis = false });
            Assert.AreEqual(QuestionStatus.Cancelled, oldAnswer.Status);
            fake.Work.ReleaseFirst(new WorkIqAnswer("Late old reply", [], "old-context"));
            Assert.IsEmpty(fake.Conversation.AnswerHistory);
            Assert.IsEmpty(fake.Conversation.TranscriptSegments);
            Assert.IsNull(fake.Conversation.Session!.WorkIqConversationId);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }
}

[TestClass]
public sealed class ChatWorkspaceBehaviorTests
{
    [TestMethod]
    public async Task StartupSignInDoesNotBlockTheCallerAndRunsOnlyOnce()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = new DelegateAuth(_ =>
        {
            entered.TrySetResult();
            release.Wait();
            return Task.CompletedTask;
        });
        var runner = new StartupSignIn(auth);
        Assert.AreEqual(0, auth.Calls);
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => returned.TrySetResult(runner.StartAsync()));
        Task? attempt = null;
        try
        {
            attempt = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(attempt.IsCompleted);
            Assert.AreSame(attempt, runner.StartAsync());
            Assert.AreEqual(1, auth.Calls);
        }
        finally { release.Set(); }
        await attempt!;
    }

    [TestMethod]
    public async Task StartupSignInPropagatesCancellationWithoutRetrying()
    {
        using var cancellation = new CancellationTokenSource();
        var auth = new DelegateAuth(token => Task.Delay(Timeout.Infinite, token));
        var runner = new StartupSignIn(auth);
        var attempt = runner.StartAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => attempt);
        Assert.AreSame(attempt, runner.StartAsync());
        Assert.IsTrue(auth.Calls <= 1);
    }

    [TestMethod]
    public void ExplicitCollapseWorksFromEveryModeAndLeftClickStillRestores()
    {
        foreach (var cycles in new[] { 0, 1, 2 })
        {
            var layout = new AnswerPanelLayout();
            for (var index = 0; index < cycles; index++) layout.Cycle();
            layout.Collapse();
            layout.Collapse();
            Assert.AreEqual(AnswerPanelMode.Collapsed, layout.Mode);
            Assert.AreEqual(0d, layout.AnswerWeight);
            layout.Cycle();
            Assert.AreEqual(AnswerPanelMode.Normal, layout.Mode);
        }
    }

    [TestMethod]
    public void IntentDocumentPreservesAllVisibleAssistanceAndAnalysisText()
    {
        var assistance = new ConversationAssistance
        {
            Conclusion = "Confirm deployment scope", SuggestedWording = "Does the deadline include rollout?",
            KeyPoints = ["Deadline stated", "Rollout unclear"], Uncertainty = "Owner not confirmed",
            Sources = ["Transcript turn 1", "Saved source"]
        };
        var sections = AnalysisDocument.CreateIntent(UiText.For(ConversationLanguage.Chinese), assistance,
            "Current status", "Coverage details", "Overall intent", "Selected historical assistance");
        var text = string.Join("\n", sections.Select(section => section.Heading + "\n" + section.Text));
        foreach (var expected in new[] { assistance.Conclusion, assistance.SuggestedWording, "Deadline stated", "Rollout unclear",
            assistance.Uncertainty, "Transcript turn 1", "Saved source", "Current status", "Coverage details", "Overall intent",
            "Selected historical assistance" }) StringAssert.Contains(text, expected);
    }

    [TestMethod]
    public void StageDocumentContainsEverySummaryInOrderAndLocalizesEmptyState()
    {
        var texts = UiText.For(ConversationLanguage.English);
        var sections = AnalysisDocument.CreateStages(texts, [("Stage 1", "First summary"), ("Stage 2", "Second summary")]);
        CollectionAssert.AreEqual(new[] { "First summary", "Second summary" }, sections.Select(section => section.Text).ToArray());
        CollectionAssert.AreEqual(new[] { "Stage 1", "Stage 2" }, sections.Select(section => section.Heading).ToArray());
        Assert.AreEqual(texts["StageSummariesEmpty"], AnalysisDocument.CreateStages(texts, []).Single().Text);
    }

    [TestMethod]
    public void TranscriptsArePassiveAndAnalysisTabsHaveOneWholeDocumentViewEach()
    {
        var page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var transcript = page.Descendants().Single(element => (string?)element.Attribute(x + "Key") == "TranscriptTemplate");
        Assert.AreEqual("Border", transcript.Elements().Single().Name.LocalName);
        Assert.IsFalse(transcript.DescendantsAndSelf().Attributes().Any(attribute =>
            attribute.Name.LocalName is "Click" or "RightTapped" or "Tapped" or "AutomationProperties.HelpText"));
        Assert.IsFalse(transcript.ToString().Contains("TranscriptCardHint", StringComparison.Ordinal));
        Assert.IsTrue(transcript.Descendants().Any(element => (string?)element.Attribute("IsTextSelectionEnabled") == "True"));
        foreach (var name in new[] { "IntentDocument", "StageSummaryDocument" })
        {
            var document = page.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
            Assert.AreEqual("SelectableAnalysisView", document.Name.LocalName);
            Assert.HasCount(1, document.Ancestors().First(element => element.Name.LocalName == "TabViewItem")
                .Descendants().Where(element => element.Name.LocalName == "SelectableAnalysisView"));
        }
        var toggle = page.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "AnswerPanelToggleButton");
        Assert.AreEqual("AnswerPanelToggle_RightTapped", (string?)toggle.Attribute("RightTapped"));
        Assert.AreEqual("ToggleAnswerPanel_Click", (string?)toggle.Attribute("Click"));
        Assert.IsNull(toggle.Attribute("Visibility"));
    }

    [TestMethod]
    public void StartupSignInIsScheduledAfterPageLoadAndObsoleteTranscriptHandlersAreGone()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainPageCode.txt"));
        StringAssert.Contains(source, "Loaded += ScheduleStartupSignIn;");
        StringAssert.Contains(source, "DispatcherQueuePriority.Low");
        StringAssert.Contains(source, "SignInAtStartupAsync(_pageCancellation.Token)");
        Assert.IsFalse(source.Contains("TranscriptCard_Click", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("TranscriptCard_RightTapped", StringComparison.Ordinal));
        foreach (var language in new[] { ConversationLanguage.English, ConversationLanguage.Chinese })
            Assert.IsFalse(UiText.For(language).TryGetValue("TranscriptCardHint", out _));
    }

    private sealed class DelegateAuth(Func<CancellationToken, Task> signIn) : IAuthenticationService
    {
        private int _calls;
        public int Calls => _calls;
        public bool IsSignedIn => false;
        public Task SignInAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return signIn(cancellationToken);
        }
    }
}
