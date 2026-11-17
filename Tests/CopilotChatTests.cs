using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ConversationAssistant.Core.Analysis;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Copilot;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Persistence;
using ConversationAssistant.Core.Settings;
using ConversationAssistant.Core.WorkIQ;
using ConversationAssistant_App.Authentication;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class CopilotChatTests
{
    private const string Id = "11111111-1111-1111-1111-111111111111";
    private const string Secret = "synthetic-token-never-log";
    private static CopilotConfiguration Configuration(bool consent = true) => new()
    {
        TenantId = Id, ClientId = "22222222-2222-2222-2222-222222222222", PreviewConsent = consent
    };

    [TestMethod]
    public async Task UsesOnlyOfficialBetaCreateAndChatContractsWithDistinctBearerAndWebDisabled()
    {
        var transport = new FakeHttp();
        transport.Responses.Enqueue(Json("""{"id":"11111111-1111-1111-1111-111111111111"}""", HttpStatusCode.Created));
        transport.Responses.Enqueue(Reply("Question", "Answer"));
        transport.Responses.Enqueue(Reply("Next", "Follow-up"));
        var identity = new FakeIdentity();
        using var http = new HttpClient(transport);
        var client = new CopilotChatClient(http, identity);
        var first = await client.AskAsync("Question", null, CancellationToken.None);
        var next = await client.AskAsync("Next", first.ConversationId, CancellationToken.None);
        Assert.AreEqual(Id, first.ConversationId);
        Assert.AreEqual("Answer", first.Text);
        Assert.AreEqual("Follow-up", next.Text);
        Assert.HasCount(3, transport.Calls);
        Assert.AreEqual(CopilotChatClient.ConversationsEndpoint, transport.Calls[0].Url);
        Assert.AreEqual("{}", transport.Calls[0].Body);
        foreach (var call in transport.Calls)
        {
            Assert.AreEqual(HttpMethod.Post, call.Method);
            Assert.AreEqual("Bearer " + Secret, call.Authorization);
            Assert.AreEqual("application/json", call.MediaType);
            Assert.IsFalse(call.Body.Contains(Secret, StringComparison.Ordinal));
        }
        using var body = JsonDocument.Parse(transport.Calls[1].Body);
        Assert.AreEqual("Question", body.RootElement.GetProperty("message").GetProperty("text").GetString());
        Assert.AreEqual("Etc/UTC", body.RootElement.GetProperty("locationHint").GetProperty("timeZone").GetString());
        Assert.IsFalse(body.RootElement.GetProperty("contextualResources").GetProperty("webContext").GetProperty("isWebEnabled").GetBoolean());
        Assert.AreEqual(CopilotChatClient.ConversationsEndpoint + "/" + Id + "/chat", transport.Calls[2].Url);
        Assert.AreEqual(3, identity.Calls);
    }

    [TestMethod]
    public async Task AccessVerificationCreatesConversationWithoutSubmittingQuestions()
    {
        var transport = new FakeHttp();
        transport.Responses.Enqueue(Json(JsonSerializer.Serialize(new { id = Id }), HttpStatusCode.Created));
        using var http = new HttpClient(transport);
        await new CopilotChatClient(http, new FakeIdentity()).VerifyAccessAsync(CancellationToken.None);
        Assert.HasCount(1, transport.Calls);
        Assert.AreEqual("{}", transport.Calls[0].Body);
    }

    [TestMethod]
    public async Task CitationsComeOnlyFromFinalResponseAndRejectUnsafeUrls()
    {
        var transport = new FakeHttp();
        transport.Responses.Enqueue(Json(JsonSerializer.Serialize(new { id = Id, messages = new object[]
        {
            new { text = "Question", attributions = new[] { new { seeMoreWebUrl = "https://echo.invalid/" } } },
            new { text = "Answer", attributions = new[] { new { seeMoreWebUrl = "https://contoso.sharepoint.com/file" },
                new { seeMoreWebUrl = "https://contoso.sharepoint.com/file" }, new { seeMoreWebUrl = "javascript:alert(1)" },
                new { seeMoreWebUrl = "https://user:password@example.invalid/" } } }
        } })));
        using var http = new HttpClient(transport);
        var answer = await new CopilotChatClient(http, new FakeIdentity()).AskAsync("Question", Id, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "https://contoso.sharepoint.com/file" }, answer.Sources.ToArray());
    }

    [TestMethod]
    [DataRow(401)]
    [DataRow(403)]
    [DataRow(404)]
    [DataRow(429)]
    [DataRow(503)]
    public async Task HttpFailuresAreSanitizedNeverRetriedAndOnly401InvalidatesIdentity(int status)
    {
        var transport = new FakeHttp();
        transport.Responses.Enqueue(Json(Secret, (HttpStatusCode)status));
        var identity = new FakeIdentity();
        using var http = new HttpClient(transport);
        var error = await Assert.ThrowsExactlyAsync<WorkIqException>(() =>
            new CopilotChatClient(http, identity).AskAsync("Question", Id, CancellationToken.None));
        Assert.IsFalse(error.ToString().Contains(Secret, StringComparison.Ordinal));
        Assert.IsNull(error.InnerException);
        Assert.HasCount(1, transport.Calls);
        Assert.AreEqual(status == 401, identity.Invalidated);
    }

    [TestMethod]
    [DataRow("not-json")]
    [DataRow("{}")]
    [DataRow("{\"id\":\"11111111-1111-1111-1111-111111111111\",\"messages\":[{\"text\":\"Question\"}]}")]
    [DataRow("{\"id\":\"11111111-1111-1111-1111-111111111111\",\"messages\":[{},\"bad\"]}")]
    [DataRow("{\"id\":\"different\",\"messages\":[{}, {\"text\":\"Answer\"}]}")]
    public async Task MalformedResponsesCannotPublishPartialResultsOrEchoedPrompts(string json)
    {
        var transport = new FakeHttp();
        transport.Responses.Enqueue(Json(json));
        using var http = new HttpClient(transport);
        await Assert.ThrowsExactlyAsync<WorkIqException>(() =>
            new CopilotChatClient(http, new FakeIdentity()).AskAsync("Question", Id, CancellationToken.None));
        Assert.HasCount(1, transport.Calls);
    }

    [TestMethod]
    public async Task CallerCancellationIsPropagatedAndDoesNotRetry()
    {
        var transport = new FakeHttp { Block = true };
        using var http = new HttpClient(transport);
        using var cancellation = new CancellationTokenSource();
        var pending = new CopilotChatClient(http, new FakeIdentity()).AskAsync("Question", Id, cancellation.Token);
        await transport.Entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.HasCount(1, transport.Calls);
    }

    [TestMethod]
    public async Task OversizedResponsesAndUnsafeConversationPathsAreRejected()
    {
        var transport = new FakeHttp();
        transport.Responses.Enqueue(Json(new string('x', 4 * 1024 * 1024 + 1)));
        using var http = new HttpClient(transport);
        var client = new CopilotChatClient(http, new FakeIdentity());
        await Assert.ThrowsExactlyAsync<WorkIqException>(() => client.AskAsync("Question", Id, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<WorkIqException>(() => client.AskAsync("Question", "../../me", CancellationToken.None));
        Assert.HasCount(1, transport.Calls);
    }

    [TestMethod]
    public async Task ProviderRoutingNeverPassesConversationIdsAcrossProvidersAndDefaultsRemainWorkIq()
    {
        var work = new FakeClient("work-id");
        var copilot = new FakeClient(Id);
        var provider = new SpeechServiceConfiguration().AnalysisProvider;
        Assert.AreEqual(AnalysisProvider.WorkIq, provider);
        var selected = new SelectableAnalysisClient(work, copilot, () => provider);
        var first = await selected.AskAsync("one", "legacy-work-id", CancellationToken.None);
        Assert.AreEqual("legacy-work-id", work.Ids.Single());
        provider = AnalysisProvider.Copilot;
        var second = await selected.AskAsync("two", first.ConversationId, CancellationToken.None);
        Assert.IsNull(copilot.Ids.Single());
        StringAssert.StartsWith(second.ConversationId!, "copilot:");
        await selected.AskAsync("three", second.ConversationId, CancellationToken.None);
        Assert.AreEqual(Id, copilot.Ids[1]);
        provider = AnalysisProvider.WorkIq;
        await selected.AskAsync("four", second.ConversationId, CancellationToken.None);
        Assert.IsNull(work.Ids[1]);
    }

    [TestMethod]
    public async Task CopilotUsesExistingEvidenceValidationRetriesWithoutSubmittingRecommendations()
    {
        const string quote = "Who owns rollout?";
        var transport = new FakeHttp();
        for (var index = 0; index < 2; index++)
        {
            transport.Responses.Enqueue(Json(JsonSerializer.Serialize(new { id = Id }), HttpStatusCode.Created));
            var answer = JsonSerializer.Serialize(new { questions = new[] { new
            {
                question = quote, reason = "Owner unknown", kind = "Unanswered", evidence = index == 0 ? "Invented quote" : quote
            } } });
            transport.Responses.Enqueue(Reply("prompt", answer));
        }
        using var http = new HttpClient(transport);
        var router = new SelectableAnalysisClient(new FakeClient("unused"), new CopilotChatClient(http, new FakeIdentity()),
            () => AnalysisProvider.Copilot);
        var now = DateTimeOffset.UtcNow;
        var (_, analysis) = await WorkIqPromptSender.AskAnalysisAsync(router, "Analyze read-only", null, CancellationToken.None,
            analyzedTranscript: [new TranscriptSegment(Guid.NewGuid(), now, now, quote, "test", true)]);
        Assert.HasCount(1, analysis.Questions);
        Assert.AreEqual(RecommendationKind.Unanswered, analysis.Questions.Single().Kind);
        Assert.HasCount(4, transport.Calls);
        StringAssert.Contains(transport.Calls[3].Body, "FORMAT RECOVERY");
    }

    [TestMethod]
    public async Task MultipartCopilotRequestsRetainOnlyCopilotConversationContext()
    {
        var transport = new FakeHttp();
        transport.Responses.Enqueue(Json(JsonSerializer.Serialize(new { id = Id }), HttpStatusCode.Created));
        transport.Responses.Enqueue(Reply("part one", "ACK"));
        transport.Responses.Enqueue(Reply("part two", "Complete answer"));
        using var http = new HttpClient(transport);
        var work = new FakeClient("unused");
        var router = new SelectableAnalysisClient(work, new CopilotChatClient(http, new FakeIdentity()), () => AnalysisProvider.Copilot);
        var answer = await WorkIqPromptSender.AskAsync(router, new string('a', WorkIqPromptSender.MaxPartCharacters + 20),
            null, CancellationToken.None);
        Assert.AreEqual("Complete answer", answer.Text);
        Assert.AreEqual("copilot:" + Id, answer.ConversationId);
        Assert.HasCount(3, transport.Calls);
        Assert.IsEmpty(work.Ids);
        StringAssert.Contains(transport.Calls[1].Body, "Part 1 of 2");
        StringAssert.Contains(transport.Calls[2].Body, "Part 2 of 2");
    }

    [TestMethod]
    public void CopilotScopesAndEncryptedCacheAreDistinctFromSpeechAndRequireExplicitConsent()
    {
        var expected = new[] { "Sites.Read.All", "Mail.Read", "People.Read.All", "OnlineMeetingTranscript.Read.All",
            "Chat.Read", "ChannelMessage.Read.All", "ExternalItem.Read.All" }.Select(p => "https://graph.microsoft.com/" + p).ToArray();
        CollectionAssert.AreEquivalent(expected, CopilotEntraIdentity.Scopes.ToArray());
        Assert.IsFalse(CopilotEntraIdentity.Scopes.Contains(SpeechEntraIdentity.CognitiveScope));
        var options = CopilotEntraIdentity.CreateOptions(Configuration(), null, "test");
        Assert.AreEqual("ConversationAssistant.Copilot.test", options.TokenCachePersistenceOptions.Name);
        Assert.IsFalse(options.TokenCachePersistenceOptions.UnsafeAllowUnencryptedStorage);
        Assert.IsTrue(options.DisableAutomaticAuthentication);
        Assert.IsFalse(options.IsUnsafeSupportLoggingEnabled);
        Assert.IsFalse(options.Diagnostics.IsLoggingContentEnabled);
        Assert.AreEqual(new Uri("http://localhost"), options.RedirectUri);
        Assert.ThrowsExactly<InvalidOperationException>(() => CopilotEntraIdentity.CreateOptions(Configuration(false), null, "test"));
        Assert.ThrowsExactly<InvalidOperationException>(() => new CopilotConfiguration { PreviewConsent = true }.Validate());
    }

    [TestMethod]
    public async Task CopilotCannotAcquireTokensFromSpeechOrImplicitDefaultCredentials()
    {
        var identity = new CopilotEntraIdentity(new MemorySettings());
        Assert.IsFalse(identity.IsSignedIn);
        var error = await Assert.ThrowsExactlyAsync<WorkIqException>(async () => await identity.GetAccessTokenAsync(CancellationToken.None));
        StringAssert.Contains(error.Message, "Speech and Work IQ tokens cannot be used");
    }

    [TestMethod]
    public void ProtectedAnalysisSavePreservesSpeechAndLegacyDefaultsWithoutRequiringSpeechSetup()
    {
        var folder = Path.Combine(Path.GetTempPath(), "copilot-settings-" + Guid.NewGuid());
        var path = Path.Combine(folder, "settings.bin");
        try
        {
            var store = new ProtectedSpeechSettingsStore(path);
            Assert.AreEqual(AnalysisProvider.WorkIq, store.Load().AnalysisProvider);
            store.SaveAnalysis(AnalysisProvider.Copilot, Configuration());
            var saved = store.Load();
            Assert.AreEqual(AnalysisProvider.Copilot, saved.AnalysisProvider);
            Assert.AreEqual("", saved.ServiceUri);
            Assert.AreEqual(SpeechProvider.AzureSpeech, saved.Provider);
            Assert.IsFalse(Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(Id, StringComparison.Ordinal));
            var speech = SpeechServiceConfiguration.FromInput(SpeechProvider.OfflineWhisper, "", "", "", false)
                .WithAnalysis(saved.AnalysisProvider, saved.Copilot);
            store.Save(speech);
            store.SaveAnalysis(AnalysisProvider.WorkIq, Configuration());
            Assert.AreEqual(SpeechProvider.OfflineWhisper, store.Load().Provider);
            Assert.AreEqual(Id, store.Load().Copilot.TenantId);
            var file = new CurrentUserProtectedFile(path, "MeetingCopilot.AzureSpeech.Settings.v1");
            file.Write(Encoding.UTF8.GetBytes("{\"Provider\":1}"));
            Assert.AreEqual(AnalysisProvider.WorkIq, store.Load().AnalysisProvider);
            Assert.AreEqual("", store.Load().Copilot.ClientId);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [TestMethod]
    public async Task ArchivesPreserveProviderButNotCredentialsAndOldRecordsDefaultToWorkIq()
    {
        var folder = Path.Combine(Path.GetTempPath(), "copilot-archive-" + Guid.NewGuid());
        try
        {
            var store = new JsonConversationArchiveStore(folder);
            var session = new ConversationSession { Settings = new ConversationSettings { AnalysisProvider = AnalysisProvider.Copilot } };
            var path = await store.SaveAsync(ConversationArchive.Capture(session));
            var restored = (await store.LoadAsync(path)).Restore();
            Assert.AreEqual(AnalysisProvider.Copilot, restored.Settings.AnalysisProvider);
            Assert.IsNull(restored.WorkIqConversationId);
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            foreach (var name in new[] { "accessToken", "clientId", "tenantId", "workIqConversationId" })
                Assert.IsFalse(json.ToJsonString().Contains(name, StringComparison.OrdinalIgnoreCase));
            json["settings"]!.AsObject().Remove("analysisProvider");
            await File.WriteAllTextAsync(path, json.ToJsonString());
            Assert.AreEqual(AnalysisProvider.WorkIq, (await store.LoadAsync(path)).Restore().Settings.AnalysisProvider);
            json["settings"]!["analysisProvider"] = 999;
            await File.WriteAllTextAsync(path, json.ToJsonString());
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(path));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [TestMethod]
    public void SettingsExposeIndependentAccessibleProviderControlsAndPreserveSpeechPicker()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var picker = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "AnalysisProviderPicker");
        Assert.AreEqual("{Binding AnalysisProviderIndex, Mode=TwoWay}", (string?)picker.Attribute("SelectedIndex"));
        Assert.AreEqual("{Binding CanEditAnalysisSettings}", (string?)picker.Attribute("IsEnabled"));
        Assert.IsTrue(document.Descendants().Any(element => (string?)element.Attribute("SelectedIndex") == "{Binding SpeechProviderIndex, Mode=TwoWay}"));
    }

    private static HttpResponseMessage Reply(string prompt, string text) => Json(JsonSerializer.Serialize(new
    {
        id = Id, messages = new[] { new { text = prompt }, new { text } }
    }));
    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private sealed class FakeIdentity : ICopilotTokenProvider
    {
        public int Calls { get; private set; }
        public bool Invalidated { get; private set; }
        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(Secret);
        }
        public void Invalidate() => Invalidated = true;
    }

    private sealed class FakeHttp : HttpMessageHandler
    {
        public Queue<HttpResponseMessage> Responses { get; } = new();
        public List<(string Url, HttpMethod Method, string Body, string? Authorization, string? MediaType)> Calls { get; } = [];
        public bool Block { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri!.AbsoluteUri, request.Method, await request.Content!.ReadAsStringAsync(cancellationToken),
                request.Headers.Authorization?.ToString(), request.Content.Headers.ContentType?.MediaType));
            Entered.TrySetResult();
            if (Block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Responses.Dequeue();
        }
    }

    private sealed class FakeClient(string id) : IWorkIqClient
    {
        public List<string?> Ids { get; } = [];
        public Task<WorkIqAnswer> AskAsync(string prompt, string? conversationId, CancellationToken cancellationToken)
        {
            Ids.Add(conversationId);
            return Task.FromResult(new WorkIqAnswer("answer", [], id));
        }
    }

    private sealed class MemorySettings : ISpeechSettingsStore
    {
        public SpeechServiceConfiguration Load() => new();
        public void Save(SpeechServiceConfiguration configuration) => throw new NotSupportedException();
    }
}
