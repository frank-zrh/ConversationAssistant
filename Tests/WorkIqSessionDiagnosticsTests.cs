using System.Text.Json;
using System.Xml.Linq;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Persistence;
using ConversationAssistant.Core.WorkIQ;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class WorkIqSessionDiagnosticsTests
{
    private string _directory = null!;

    [TestInitialize]
    public void Initialize() => _directory = Path.Combine(Path.GetTempPath(), $"workiq-log-tests-{Guid.NewGuid():N}");

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [TestMethod]
    public async Task LogsRequestsRepliesTimingAndCorrelationBesideRecord()
    {
        var diagnostics = new WorkIqDiagnostics();
        var session = Guid.NewGuid();
        var request = Guid.NewGuid();
        var record = Path.Combine(_directory, "conversation.json");
        diagnostics.BindConversation(session, record);
        var client = new LoggedWorkIqClient(new DelegateClient((_, _, _) =>
            Task.FromResult(new WorkIqAnswer("Detailed reply", ["Evidence"], "remote-session"))), diagnostics);
        using (diagnostics.BeginRequest(session, request, "Analysis"))
            await client.AskAsync("Complete prompt with conversation", null, CancellationToken.None);
        await diagnostics.FlushAsync();
        var log = WorkIqDiagnostics.GetPath(record);
        Assert.AreEqual(Path.GetDirectoryName(record), Path.GetDirectoryName(log));
        var events = ReadEvents(log);
        var started = events.Single(item => Event(item) == "CallStarted");
        var completed = events.Single(item => Event(item) == "CallCompleted");
        Assert.AreEqual(session, started.GetProperty("SessionId").GetGuid());
        Assert.AreEqual(request, started.GetProperty("RequestId").GetGuid());
        Assert.AreEqual(started.GetProperty("CallId").GetGuid(), completed.GetProperty("CallId").GetGuid());
        Assert.AreEqual("Complete prompt with conversation", started.GetProperty("Data").GetProperty("Prompt").GetString());
        Assert.AreEqual("Detailed reply", completed.GetProperty("Data").GetProperty("Response").GetString());
        Assert.AreEqual("remote-session", completed.GetProperty("Data").GetProperty("ConversationId").GetString());
        Assert.IsTrue(completed.GetProperty("Data").GetProperty("ElapsedMs").GetInt64() >= 0);
    }

    [TestMethod]
    public async Task LogIncludesFailuresAndCancellationWithoutChangingExceptions()
    {
        var diagnostics = new WorkIqDiagnostics();
        var record = Path.Combine(_directory, "failures.json");
        diagnostics.BindConversation(Guid.NewGuid(), record);
        var client = new LoggedWorkIqClient(new DelegateClient((_, _, _) =>
            Task.FromException<WorkIqAnswer>(new TimeoutException("No response from provider"))), diagnostics);
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => client.AskAsync("timeout", null, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        client = new LoggedWorkIqClient(new DelegateClient((_, _, token) => Task.FromCanceled<WorkIqAnswer>(token)), diagnostics);
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.AskAsync("cancel", null, cancellation.Token));
        await diagnostics.FlushAsync();
        var events = ReadEvents(WorkIqDiagnostics.GetPath(record));
        Assert.AreEqual(1, events.Count(item => Event(item) == "CallFailed"));
        Assert.AreEqual(1, events.Count(item => Event(item) == "CallCancelled"));
        StringAssert.Contains(events.Single(item => Event(item) == "CallFailed").GetProperty("Data").GetProperty("Error").GetString()!, "No response");
    }

    [TestMethod]
    public async Task CredentialsAreRedactedButDiagnosticConversationContentRemains()
    {
        var diagnostics = new WorkIqDiagnostics();
        var record = Path.Combine(_directory, "redaction.json");
        diagnostics.BindConversation(Guid.NewGuid(), record);
        await diagnostics.WriteAsync(diagnostics.Capture(), "Test", new
        {
            Prompt = "Power Apps discussion; api_key=super-secret; Authorization: Bearer eyJabc.def.ghi",
            Response = "password=another-secret; client_secret=private-value",
            Nested = new { access_token = "hidden-token" }
        });
        await diagnostics.FlushAsync();
        var text = await File.ReadAllTextAsync(WorkIqDiagnostics.GetPath(record));
        foreach (var secret in new[] { "super-secret", "eyJabc.def.ghi", "another-secret", "private-value", "hidden-token" })
            Assert.IsFalse(text.Contains(secret, StringComparison.Ordinal), secret);
        StringAssert.Contains(text, "Power Apps discussion");
        StringAssert.Contains(text, "[REDACTED]");
        Assert.IsTrue(ReadEvents(WorkIqDiagnostics.GetPath(record)).Length >= 2);
    }

    [TestMethod]
    public async Task LateResponsesStayInOriginalConversationAfterSwitch()
    {
        var diagnostics = new WorkIqDiagnostics();
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();
        var firstRecord = Path.Combine(_directory, "first.json");
        var secondRecord = Path.Combine(_directory, "second.json");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WorkIqAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new LoggedWorkIqClient(new DelegateClient((prompt, _, _) =>
        {
            if (prompt != "first-request") return Task.FromResult(new WorkIqAnswer("second-response", [], null));
            entered.TrySetResult();
            return release.Task;
        }), diagnostics);
        diagnostics.BindConversation(firstSession, firstRecord);
        using (diagnostics.BeginRequest(firstSession, Guid.NewGuid(), "Analysis"))
        {
            var pending = client.AskAsync("first-request", null, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            diagnostics.BindConversation(secondSession, secondRecord);
            using (diagnostics.BeginRequest(secondSession, Guid.NewGuid(), "Question"))
                await client.AskAsync("second-request", null, CancellationToken.None);
            release.SetResult(new WorkIqAnswer("late-first-response", [], null));
            await pending;
        }
        await diagnostics.FlushAsync();
        var firstText = await File.ReadAllTextAsync(WorkIqDiagnostics.GetPath(firstRecord));
        var secondText = await File.ReadAllTextAsync(WorkIqDiagnostics.GetPath(secondRecord));
        StringAssert.Contains(firstText, "late-first-response");
        Assert.IsFalse(firstText.Contains("second-request", StringComparison.Ordinal));
        StringAssert.Contains(secondText, "second-response");
        Assert.IsFalse(secondText.Contains("late-first-response", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EveryMultipartApiCallIsLogged()
    {
        var diagnostics = new WorkIqDiagnostics();
        var record = Path.Combine(_directory, "multipart.json");
        diagnostics.BindConversation(Guid.NewGuid(), record);
        var client = new LoggedWorkIqClient(new DelegateClient((_, id, _) =>
            Task.FromResult(new WorkIqAnswer("ACK or result", [], id ?? "multipart-thread"))), diagnostics);
        await WorkIqPromptSender.AskAsync(client, new string('x', WorkIqPromptSender.MaxPartCharacters * 2 + 1), null, CancellationToken.None);
        await diagnostics.FlushAsync();
        var events = ReadEvents(WorkIqDiagnostics.GetPath(record));
        Assert.AreEqual(3, events.Count(item => Event(item) == "CallStarted"));
        Assert.AreEqual(3, events.Count(item => Event(item) == "CallCompleted"));
        Assert.AreEqual(3, events.Where(item => Event(item) == "CallStarted").Select(item => item.GetProperty("CallId").GetGuid()).Distinct().Count());
    }

    [TestMethod]
    public async Task SchemaRetriesRecordInvalidResponsesAndValidationReasons()
    {
        var diagnostics = new WorkIqDiagnostics();
        var record = Path.Combine(_directory, "schema.json");
        diagnostics.BindConversation(Guid.NewGuid(), record);
        var client = new LoggedWorkIqClient(new DelegateClient((_, _, _) =>
            Task.FromResult(new WorkIqAnswer("not valid analysis JSON", [], "bad-response"))), diagnostics);
        await Assert.ThrowsExactlyAsync<WorkIqException>(() => WorkIqPromptSender.AskAnalysisAsync(
            client, "Analyze this conversation", null, CancellationToken.None, diagnostics));
        await diagnostics.FlushAsync();
        var events = ReadEvents(WorkIqDiagnostics.GetPath(record));
        var failures = events.Where(item => Event(item) == "AnalysisValidationFailed").ToArray();
        Assert.HasCount(WorkIqPromptSender.MaximumAnalysisAttempts, failures);
        CollectionAssert.AreEqual(new[] { true, true, false }, failures
            .Select(item => item.GetProperty("Data").GetProperty("WillRetry").GetBoolean()).ToArray());
        Assert.AreEqual(3, events.Count(item => Event(item) == "CallCompleted" &&
            item.GetProperty("Data").GetProperty("Response").GetString() == "not valid analysis JSON"));
    }

    [TestMethod]
    public async Task LogWriteFailureDoesNotPreventAnApiCall()
    {
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "block directory creation");
        var diagnostics = new WorkIqDiagnostics();
        var failures = 0;
        diagnostics.Failed += _ => Interlocked.Increment(ref failures);
        diagnostics.BindConversation(Guid.NewGuid(), Path.Combine(blocker, "record.json"));
        var client = new LoggedWorkIqClient(new DelegateClient((_, _, _) =>
            Task.FromResult(new WorkIqAnswer("Valid reply", [], null))), diagnostics);
        var answer = await client.AskAsync("Still process this", null, CancellationToken.None);
        await diagnostics.FlushAsync();
        Assert.AreEqual("Valid reply", answer.Text);
        Assert.AreEqual(1, failures);
    }

    [TestMethod]
    public void ViewSessionActionIsLocalizedAndRemainsInTranscriptOverflowMenu()
    {
        var page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var item = page.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "ViewSessionMenuItem");
        Assert.AreEqual("MenuFlyoutItem", item.Name.LocalName);
        Assert.AreEqual("{Binding Texts[ViewSession]}", (string?)item.Attribute("Text"));
        Assert.AreEqual("{Binding CanViewSession}", (string?)item.Attribute("IsEnabled"));
        Assert.AreEqual("ViewSession_Click", (string?)item.Attribute("Click"));
        Assert.IsTrue(item.Ancestors().Any(element => (string?)element.Attribute(x + "Name") == "TranscriptActionsButton"));
    }

    private static string? Event(JsonElement entry) => entry.GetProperty("Event").GetString();
    private static JsonElement[] ReadEvents(string path) => File.ReadLines(path)
        .Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();

    private sealed class DelegateClient(Func<string, string?, CancellationToken, Task<WorkIqAnswer>> call) : IWorkIqClient
    {
        public Task<WorkIqAnswer> AskAsync(string prompt, string? conversationId, CancellationToken cancellationToken) =>
            call(prompt, conversationId, cancellationToken);
    }
}

public sealed partial class PipelineTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NextUtteranceAfterFailureAnalyzesAllSkippedContent(bool timeout)
    {
        var fake = new Harness();
        fake.Work.AnalysisReply = """{"intentSummary":"Initial","stageSummary":"First successful stage","questions":[]}""";
        fake.Conversation.StartConversation(new ConversationSettings());
        try
        {
            EmitFinals(fake, 1);
            var first = fake.Conversation.AnalyzeConversation();
            await WaitUntil(() => first.Status == QuestionStatus.Completed);
            if (timeout) fake.Work.Failures.Enqueue(new TimeoutException("Work IQ did not respond"));
            else fake.Work.AnalysisReply = "invalid schema";
            EmitFinals(fake, 7, 2);
            await WaitUntil(() => fake.Conversation.Analysis?.Status == QuestionStatus.Failed);
            var failed = fake.Conversation.Analysis!;
            var calls = fake.Work.Calls.Count;
            Assert.AreEqual(7, fake.Conversation.PendingAnalysisCount);
            Assert.IsTrue(fake.Conversation.IsListening);
            for (var index = 0; index < 10; index++) fake.Conversation.AnalyzeIfDue();
            Assert.HasCount(calls, fake.Work.Calls);
            fake.Work.AnalysisReply = """{"intentSummary":"Recovered","stageSummary":"All skipped content included","questions":[]}""";
            fake.Work.Reply = fake.Work.AnalysisReply;
            EmitFinals(fake, 1, 9);
            Assert.AreNotEqual(failed.Id, fake.Conversation.Analysis?.Id,
                $"Next utterance did not queue analysis: transcript={fake.Conversation.TranscriptSegments.Count}, pending={fake.Conversation.PendingAnalysisCount}, state={fake.Conversation.State}.");
            await WaitUntil(() => fake.Conversation.Analysis?.Status is QuestionStatus.Completed or QuestionStatus.Failed);
            var recovered = fake.Conversation.Analysis!;
            Assert.AreEqual(QuestionStatus.Completed, recovered.Status, recovered.Error);
            Assert.AreNotEqual(failed.Id, recovered.Id);
            Assert.HasCount(9, recovered.Transcript);
            Assert.AreEqual(8, recovered.StageContext!.Split("Statement ", StringSplitOptions.None).Length - 1);
            foreach (var segment in failed.Transcript.Skip(1)) StringAssert.Contains(recovered.StageContext, segment.Text);
            StringAssert.Contains(recovered.StageContext, "Statement 9");
            Assert.IsFalse(recovered.StageContext.Contains(first.Transcript[0].Text, StringComparison.Ordinal));
            StringAssert.Contains(recovered.PreviousStageSummaries, first.StageSummary);
            Assert.AreEqual("", failed.StageSummary);
            Assert.AreEqual(0, fake.Conversation.PendingAnalysisCount);
            Assert.IsTrue(fake.Conversation.Session!.Settings.AutomaticAnalysis);
            Assert.IsTrue(fake.Conversation.IsListening);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }

    [TestMethod]
    public async Task ApiFailureDoesNotStopLaterQuestionsOrMixImportedLogs()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"workiq-session-pipeline-{Guid.NewGuid():N}");
        var fake = new Harness(new JsonConversationArchiveStore(directory));
        try
        {
            fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
            fake.Work.Failures.Enqueue(new IOException("Transport closed"));
            var failed = fake.Conversation.AskManually("First request fails");
            await WaitUntil(() => failed.Status == QuestionStatus.Failed);
            var next = fake.Conversation.AskManually("Second request succeeds");
            await WaitUntil(() => next.Status == QuestionStatus.Completed);
            await fake.Conversation.EndConversationAsync();
            var originalLog = fake.Conversation.WorkIqLogPath!;
            var originalRecord = fake.Conversation.RecordPath!;
            var originalText = await File.ReadAllTextAsync(originalLog);
            StringAssert.Contains(originalText, "CallFailed");
            StringAssert.Contains(originalText, "Transport closed");
            StringAssert.Contains(originalText, "Second request succeeds");
            await fake.Conversation.ImportConversationAsync(originalRecord);
            Assert.AreNotEqual(originalLog, fake.Conversation.WorkIqLogPath);
            var imported = fake.Conversation.AskManually("Imported record request");
            await WaitUntil(() => imported.Status == QuestionStatus.Completed);
            await fake.Conversation.EndConversationAsync();
            var importedText = await File.ReadAllTextAsync(fake.Conversation.WorkIqLogPath!);
            StringAssert.Contains(importedText, "Imported record request");
            Assert.IsFalse(importedText.Contains("First request fails", StringComparison.Ordinal));
            Assert.AreEqual(originalText, await File.ReadAllTextAsync(originalLog));
        }
        finally
        {
            await fake.Conversation.EndConversationAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
