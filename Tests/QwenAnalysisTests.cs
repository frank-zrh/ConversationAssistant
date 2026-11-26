using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ConversationAssistant.Core.Analysis;
using ConversationAssistant.Core.Context;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Persistence;
using ConversationAssistant.Core.Qwen;
using ConversationAssistant.Core.Settings;
using ConversationAssistant.Core.Speech;
using ConversationAssistant.Core.Transcript;
using ConversationAssistant.Core.WorkIQ;
using ConversationAssistant_App;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class QwenAnalysisTests
{
    internal const string TestKey = "sk-not-a-real-test-credential";
    internal const string WorkspaceTestKey = "sk-ws-not-a-real-test-credential";
    internal const string TokenPlanTestKey = "sk-sp-not-a-real-test-credential";

    [TestMethod]
    [DataRow(WorkspaceTestKey, WorkspaceTestKey)]
    [DataRow(" \r\n\ufeff\u200b\u201c" + WorkspaceTestKey + "\u201d\t", WorkspaceTestKey)]
    [DataRow("bearer \"" + WorkspaceTestKey + "\"", WorkspaceTestKey)]
    [DataRow("'Bearer " + TestKey + "'", TestKey)]
    [DataRow("  " + TestKey + "\r\n", TestKey)]
    public void FullPayAsYouGoKeyCanBePastedWithClipboardFormatting(string pasted, string expected)
    {
        var settings = QwenSettings.FromInput(pasted, QwenModels.DefaultSpeech, QwenModels.DefaultAnalysis);

        Assert.AreEqual(expected, settings.ApiKey);
        Assert.IsTrue(settings.HasApiKey);
        Assert.AreEqual(expected, settings.RequireApiKey());
    }

    [TestMethod]
    [DataRow("sk-ws-ab****cd")]
    [DataRow("sk-ws-ab\u2026cd")]
    [DataRow("sk-ws-")]
    [DataRow("sk-")]
    [DataRow("key-1234")]
    [DataRow("sk-ws-abc\u200bdef")]
    [DataRow("sk-ws-abc\r\ndef")]
    public void InvalidInputCannotBeSavedOrUsedAsAKey(string pasted)
    {
        var inputError = Assert.ThrowsExactly<InvalidOperationException>(
            () => QwenSettings.FromInput(pasted, QwenModels.DefaultSpeech, QwenModels.DefaultAnalysis));
        var settings = new QwenSettings { ApiKey = pasted };

        Assert.IsFalse(settings.HasApiKey);
        var requestError = Assert.ThrowsExactly<InvalidOperationException>(() => settings.RequireApiKey());
        Assert.AreEqual("ErrorQwenKeyInvalid", UiMessage.FromDiagnostic(inputError.Message)?.Key);
        Assert.AreEqual(inputError.Message, requestError.Message);
    }

    [TestMethod]
    public async Task CompleteAnalysisSendsTheEntirePromptAndRetriesMalformedOutputWithoutRemoteState()
    {
        var store = new TestStore();
        store.Save(QwenSettings.FromInput(TestKey, QwenModels.DefaultSpeech, "qwen-flash"));
        var transport = new TestTransport("{}", """{"intentSummary":"Plan launch","questions":[]}""");
        using var http = new HttpClient(transport);
        var client = new LoggedWorkIqClient(new QwenAnalysisClient(http, store), new WorkIqDiagnostics());
        var prompt = "Beginning of conversation\n" + new string('A', 16_000) + "\nEnd of conversation";

        var (answer, analysis) = await WorkIqPromptSender.AskAnalysisAsync(client, prompt, "obsolete-remote-id", CancellationToken.None);

        Assert.AreEqual("Plan launch", analysis.IntentSummary);
        Assert.IsNull(answer.ConversationId);
        Assert.HasCount(2, transport.Calls);
        foreach (var call in transport.Calls)
        {
            Assert.AreEqual("https://maas.qianwenaiapi.com/compatible-mode/v1/chat/completions", call.Url);
            Assert.AreEqual("Bearer " + TestKey, call.Authorization);
            Assert.IsFalse(call.Body.Contains(TestKey, StringComparison.Ordinal));
            Assert.IsFalse(call.Body.Contains("obsolete-remote-id", StringComparison.Ordinal));
            using var json = JsonDocument.Parse(call.Body);
            Assert.AreEqual("qwen-flash", json.RootElement.GetProperty("model").GetString());
            Assert.AreEqual("json_object", json.RootElement.GetProperty("response_format").GetProperty("type").GetString());
            StringAssert.StartsWith(UserPrompt(json), prompt);
        }
        using var retry = JsonDocument.Parse(transport.Calls[1].Body);
        StringAssert.Contains(UserPrompt(retry), "FORMAT RECOVERY");
    }

    [TestMethod]
    public async Task MissingKeyNeverSendsARequestAndRejectedKeyDoesNotExposeRawError()
    {
        var store = new TestStore();
        var transport = new TestTransport { Status = HttpStatusCode.Unauthorized };
        using var http = new HttpClient(transport);
        var client = new QwenAnalysisClient(http, store);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.TestAsync());
        Assert.IsEmpty(transport.Calls);
        store.Save(QwenSettings.FromInput(TestKey, QwenModels.DefaultSpeech, QwenModels.DefaultAnalysis));
        var error = await Assert.ThrowsExactlyAsync<WorkIqException>(() => client.TestAsync());

        Assert.AreEqual("ErrorQwenAnalysisPermissions", UiMessage.FromDiagnostic(error.Message)?.Key);
        Assert.IsFalse(error.ToString().Contains(TestKey, StringComparison.Ordinal));
        Assert.HasCount(1, transport.Calls);
    }

    [TestMethod]
    public async Task TokenPlanKeyInPayAsYouGoFieldNeverReachesAnalysisOrSpeechEndpoints()
    {
        var store = new TestStore();
        store.Save(new QwenSettings { ApiKey = "Bearer \"" + TokenPlanTestKey + "\"" });
        var transport = new TestTransport();
        using var http = new HttpClient(transport);
        var analysis = new QwenAnalysisClient(http, store);
        var socketCreations = 0;
        using var speech = new QwenSpeechRecognitionService(store, () =>
        {
            socketCreations++;
            throw new InvalidOperationException("No speech socket should be created.");
        }, TimeProvider.System);

        Assert.IsFalse(store.Load().HasApiKey);
        var analysisError = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => analysis.TestAsync());
        var speechError = await Assert.ThrowsExactlyAsync<SpeechConnectionException>(() => speech.TestAsync(ConversationLanguage.Chinese));
        Assert.AreEqual("ErrorQwenTokenPlanKey", UiMessage.FromDiagnostic(analysisError.Message)?.Key);
        Assert.AreEqual(analysisError.Message, speechError.Message);
        Assert.IsEmpty(transport.Calls);
        Assert.AreEqual(0, socketCreations);
    }

    [TestMethod]
    public async Task AnalysisAndQuestionsUseSelectedEndpointCredentialsAndModelAfterSwitching()
    {
        var store = new TestStore();
        var transport = new TestTransport("summary", "answer", "pay-as-you-go answer");
        using var http = new HttpClient(transport);
        var client = new QwenAnalysisClient(http, store);
        store.Save(QwenSettings.FromInput(WorkspaceTestKey, QwenModels.DefaultSpeech, "qwen-max",
            QwenBillingMode.TokenPlan, $" Bearer '{TokenPlanTestKey}' ", "qwen3.8-max"));

        Assert.AreEqual("summary", (await client.AnalyzeAsync("Analyze", null, CancellationToken.None)).Text);
        Assert.AreEqual("answer", (await client.AskAsync("Ask", null, CancellationToken.None)).Text);
        store.Save(QwenSettings.FromInput(WorkspaceTestKey, QwenModels.DefaultSpeech, "qwen-max",
            QwenBillingMode.PayAsYouGo, TokenPlanTestKey, "qwen3.8-max"));
        Assert.AreEqual("pay-as-you-go answer", (await client.AskAsync("Ask again", null, CancellationToken.None)).Text);

        Assert.HasCount(3, transport.Calls);
        for (var index = 0; index < transport.Calls.Count; index++)
        {
            var (url, authorization, body) = transport.Calls[index];
            var tokenPlan = index < 2;
            var host = tokenPlan ? "token-plan.maas.qianwenaiapi.com" : "maas.qianwenaiapi.com";
            Assert.AreEqual($"https://{host}/compatible-mode/v1/chat/completions", url);
            Assert.AreEqual("Bearer " + (tokenPlan ? TokenPlanTestKey : WorkspaceTestKey), authorization);
            Assert.DoesNotContain(TokenPlanTestKey, body);
            Assert.DoesNotContain(WorkspaceTestKey, body);
            using var json = JsonDocument.Parse(body);
            Assert.AreEqual(tokenPlan ? "qwen3.8-max" : "qwen-max", json.RootElement.GetProperty("model").GetString());
            Assert.IsFalse(json.RootElement.TryGetProperty("enable_thinking", out _));
            Assert.AreEqual(index == 0, json.RootElement.TryGetProperty("response_format", out var format));
            if (index == 0) Assert.AreEqual("json_object", format.GetProperty("type").GetString());
        }
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, "ErrorQwenTokenPlanPermissions")]
    [DataRow(HttpStatusCode.TooManyRequests, "ErrorQwenTokenPlanQuota")]
    public async Task RejectedTokenPlanAnalysisDoesNotRetryWithPayAsYouGo(HttpStatusCode status, string messageKey)
    {
        var store = new TestStore();
        var transport = new TestTransport { Status = status };
        using var http = new HttpClient(transport);
        var client = new QwenAnalysisClient(http, store);
        store.Save(QwenSettings.FromInput(WorkspaceTestKey, QwenModels.DefaultSpeech, QwenModels.DefaultAnalysis,
            QwenBillingMode.TokenPlan));
        var missing = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.TestAsync());
        Assert.AreEqual("ErrorQwenTokenPlanKeyRequired", UiMessage.FromDiagnostic(missing.Message)?.Key);
        Assert.IsEmpty(transport.Calls);

        store.Save(QwenSettings.FromInput(WorkspaceTestKey, QwenModels.DefaultSpeech, QwenModels.DefaultAnalysis,
            QwenBillingMode.TokenPlan, TokenPlanTestKey));
        var rejected = await Assert.ThrowsExactlyAsync<WorkIqException>(() => client.TestAsync());
        Assert.AreEqual(messageKey, UiMessage.FromDiagnostic(rejected.Message)?.Key);
        Assert.HasCount(1, transport.Calls);
        Assert.AreEqual("token-plan.maas.qianwenaiapi.com", new Uri(transport.Calls[0].Url).Host);
        Assert.DoesNotContain(WorkspaceTestKey, rejected.ToString());
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void LegacyEncryptedConfigurationRemainsPayAsYouGoAndBothProfilesSurviveSwitching()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qwen-legacy-settings-{Guid.NewGuid():N}.bin");
        try
        {
            var protectedFile = new CurrentUserProtectedFile(path, "ConversationAssistant.QwenSettings.v1");
            protectedFile.Write(JsonSerializer.SerializeToUtf8Bytes(new
            {
                ApiKey = WorkspaceTestKey, SpeechModel = QwenModels.AlternateSpeech, AnalysisModel = "qwen-max"
            }));
            var store = new ProtectedQwenSettingsStore(path);
            var legacy = store.Load();
            Assert.AreEqual(QwenBillingMode.PayAsYouGo, legacy.BillingMode);
            Assert.AreEqual(QwenModels.DefaultTokenPlanAnalysis, legacy.TokenPlanAnalysisModel);
            Assert.IsNull(legacy.TokenPlanSpeechModel);
            Assert.AreEqual(WorkspaceTestKey, legacy.RequireAnalysisConfiguration().Key);

            store.Save(QwenSettings.FromInput(legacy.ApiKey, legacy.SpeechModel, legacy.AnalysisModel,
                QwenBillingMode.TokenPlan, TokenPlanTestKey, "qwen3.8-max", QwenModels.TokenPlanAsrFlash));
            var loaded = new ProtectedQwenSettingsStore(path).Load();
            Assert.AreEqual(QwenBillingMode.TokenPlan, loaded.BillingMode);
            Assert.AreEqual(TokenPlanTestKey, loaded.RequireAnalysisConfiguration().Key);
            Assert.AreEqual("qwen3.8-max", loaded.RequireAnalysisConfiguration().Model);
            Assert.AreEqual(WorkspaceTestKey, loaded.RequireApiKey());
            Assert.AreEqual(QwenModels.AlternateSpeech, loaded.SpeechModel);
            Assert.AreEqual(QwenModels.TokenPlanAsrFlash, loaded.TokenPlanSpeechModel);
            Assert.AreEqual("qwen-max", loaded.AnalysisModel);
            var ciphertext = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain(TokenPlanTestKey, ciphertext);
            Assert.DoesNotContain(WorkspaceTestKey, ciphertext);
            using var envelope = JsonDocument.Parse(protectedFile.Read()!);
            var profileData = envelope.RootElement;
            Assert.DoesNotContain(TokenPlanTestKey, profileData.GetRawText());
            Assert.DoesNotContain(WorkspaceTestKey, profileData.GetRawText());
            var payAsYouGoCiphertext = profileData.GetProperty("ProtectedPayAsYouGoKey").GetString();
            var tokenPlanCiphertext = profileData.GetProperty("ProtectedTokenPlanKey").GetString();
            Assert.IsFalse(string.IsNullOrEmpty(payAsYouGoCiphertext));
            Assert.IsFalse(string.IsNullOrEmpty(tokenPlanCiphertext));
            Assert.AreNotEqual(payAsYouGoCiphertext, tokenPlanCiphertext);

            store.Save(loaded.WithChanges(QwenBillingMode.PayAsYouGo, loaded.SpeechModel, loaded.AnalysisModel,
                loaded.TokenPlanAnalysisModel, "", "", loaded.TokenPlanSpeechModel));
            using var switched = JsonDocument.Parse(protectedFile.Read()!);
            Assert.AreEqual(payAsYouGoCiphertext, switched.RootElement.GetProperty("ProtectedPayAsYouGoKey").GetString());
            Assert.AreEqual(tokenPlanCiphertext, switched.RootElement.GetProperty("ProtectedTokenPlanKey").GetString());
            var reloaded = store.Load();
            Assert.AreEqual(WorkspaceTestKey, reloaded.RequireAnalysisConfiguration().Key);
            Assert.AreEqual(QwenModels.AlternateSpeech, reloaded.SpeechModel);
            Assert.AreEqual(QwenModels.TokenPlanAsrFlash, reloaded.TokenPlanSpeechModel);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void WindowsProtectedSettingsDefaultAndPersistBothModelsWithoutPlaintextKey()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Requires Windows DPAPI.");
        var path = Path.Combine(Path.GetTempPath(), "qwen-test-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var store = new ProtectedQwenSettingsStore(path);
            var defaults = store.Load();
            Assert.IsFalse(defaults.HasApiKey);
            Assert.AreEqual("qwen3-asr-flash-realtime", defaults.SpeechModel);
            Assert.AreEqual("qwen-plus", defaults.AnalysisModel);

            store.Save(new QwenSettings { ApiKey = "Bearer \"" + WorkspaceTestKey + "\"", SpeechModel = QwenModels.AlternateSpeech, AnalysisModel = "qwen-max" });

            Assert.IsFalse(Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(WorkspaceTestKey, StringComparison.Ordinal));
            var loaded = new ProtectedQwenSettingsStore(path).Load();
            Assert.AreEqual(WorkspaceTestKey, loaded.ApiKey);
            Assert.AreEqual(QwenModels.AlternateSpeech, loaded.SpeechModel);
            Assert.AreEqual("qwen-max", loaded.AnalysisModel);
            Assert.ThrowsExactly<InvalidOperationException>(() => QwenSettings.FromInput(TestKey, "qwen-max", loaded.AnalysisModel));

            Assert.ThrowsExactly<InvalidOperationException>(() => store.Save(
                QwenSettings.FromInput(TokenPlanTestKey, QwenModels.DefaultSpeech, QwenModels.DefaultAnalysis)));
            Assert.ThrowsExactly<InvalidOperationException>(() => store.Save(new QwenSettings { ApiKey = "sk-ws-ab****cd" }));
            Assert.AreEqual(WorkspaceTestKey, store.Load().RequireApiKey());
            Assert.AreEqual("qwen-max", store.Load().AnalysisModel);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void SettingsExposeBillingChoiceAndTwoMaskedApiKeys()
    {
        var page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => page.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        Assert.HasCount(2, page.Descendants().Where(element => element.Name.LocalName == "PasswordBox"));
        foreach (var name in new[] { "QwenApiKeyBox", "QwenTokenPlanApiKeyBox" })
        {
            var key = Named(name);
            Assert.AreEqual("Hidden", (string?)key.Attribute("PasswordRevealMode"));
            Assert.IsNull(key.Attribute("Password"));
            Assert.AreEqual("QwenKey_PasswordChanged", (string?)key.Attribute("PasswordChanged"));
        }
        Assert.AreEqual("{Binding QwenPayAsYouGoVisibility}", (string?)Named("QwenApiKeyBox").Attribute("Visibility"));
        Assert.AreEqual("{Binding QwenTokenPlanVisibility}", (string?)Named("QwenTokenPlanApiKeyBox").Attribute("Visibility"));
        Assert.AreEqual("{Binding QwenPayAsYouGoVisibility}", (string?)Named("QwenSpeechModelPicker").Attribute("Visibility"));
        Assert.AreEqual("{Binding QwenBillingIndex, Mode=TwoWay}", (string?)Named("QwenBillingPicker").Attribute("SelectedIndex"));
        Assert.AreEqual("{Binding QwenSpeechModel, Mode=TwoWay}", (string?)Named("QwenSpeechModelPicker").Attribute("SelectedItem"));
        Assert.AreEqual("{Binding QwenTokenPlanSpeechIndex, Mode=TwoWay}", (string?)Named("QwenTokenPlanSpeechModelPicker").Attribute("SelectedIndex"));
        Assert.AreEqual("{Binding QwenTokenPlanVisibility}", (string?)Named("QwenTokenPlanSpeechModelPicker").Attribute("Visibility"));
        Assert.AreEqual("{Binding QwenAnalysisModel, Mode=TwoWay}", (string?)Named("QwenAnalysisModelPicker").Attribute("SelectedItem"));
        Assert.AreEqual("{Binding QwenPayAsYouGoVisibility}", (string?)Named("QwenAnalysisModelPicker").Attribute("Visibility"));
        Assert.AreEqual("{Binding QwenTokenPlanAnalysisModel, Mode=TwoWay}", (string?)Named("QwenTokenPlanAnalysisModelPicker").Attribute("SelectedItem"));
        Assert.AreEqual("{Binding QwenTokenPlanVisibility}", (string?)Named("QwenTokenPlanAnalysisModelPicker").Attribute("Visibility"));
    }

    private static string UserPrompt(JsonDocument request) => request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;

    internal sealed class TestStore : IQwenSettingsStore
    {
        private QwenSettings _settings = new();
        public QwenSettings Load() => _settings;
        public void Save(QwenSettings settings) => _settings = settings;
    }

    internal sealed class TestTransport(params string[] replies) : HttpMessageHandler
    {
        internal List<(string Url, string? Authorization, string Body)> Calls { get; } = [];
        internal HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Calls.Count;
            Calls.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            var text = Status == HttpStatusCode.OK
                ? JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = replies[index] } } } })
                : "Ungarded server response echoing " + TestKey;
            return new HttpResponseMessage(Status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }
}

public sealed partial class PipelineTests
{
    [TestMethod]
    [DataRow(AnalysisProvider.WorkIq)]
    [DataRow(AnalysisProvider.Copilot)]
    public async Task LegacyRecordImportsWithSpeakerLabelsButUsesOnlyQwenForNewAnalysis(AnalysisProvider provider)
    {
        var folder = Directory.CreateTempSubdirectory("qwen-legacy-archive-");
        try
        {
            var sourceStore = new JsonConversationArchiveStore(Path.Combine(folder.FullName, "source"));
            var localStore = new JsonConversationArchiveStore(Path.Combine(folder.FullName, "local"));
            var now = DateTimeOffset.UtcNow;
            var oldSession = new ConversationSession { Settings = new ConversationSettings { AnalysisProvider = provider }, StartTime = now };
            oldSession.TranscriptSegments.Add(new TranscriptSegment(Guid.NewGuid(), now, now, "Agreed to ship Monday.", "Speech", true, 2));
            var path = await sourceStore.SaveAsync(ConversationArchive.Capture(oldSession));
            var fake = new Harness(localStore);

            await fake.Conversation.ImportConversationAsync(path);

            var original = (await sourceStore.LoadAsync(path)).Restore();
            var imported = (await localStore.LoadAsync(fake.Conversation.RecordPath!)).Restore();
            Assert.AreEqual(provider, original.Settings.AnalysisProvider);
            Assert.AreEqual(AnalysisProvider.Qwen, imported.Settings.AnalysisProvider);
            Assert.AreEqual(original.TranscriptSegments.Single(), imported.TranscriptSegments.Single());
            Assert.IsEmpty(fake.Work.Calls);
        }
        finally { folder.Delete(recursive: true); }
    }

    [TestMethod]
    public async Task ImportedConversationCanBeAnalyzedAndAskedAboutWithOnlyATokenPlanKey()
    {
        var folder = Directory.CreateTempSubdirectory("qwen-token-plan-archive-");
        var source = new JsonConversationArchiveStore(Path.Combine(folder.FullName, "source"));
        var local = new JsonConversationArchiveStore(Path.Combine(folder.FullName, "local"));
        var store = new QwenAnalysisTests.TestStore();
        store.Save(QwenSettings.FromInput("", QwenModels.DefaultSpeech, QwenModels.DefaultAnalysis,
            QwenBillingMode.TokenPlan, QwenAnalysisTests.TokenPlanTestKey));
        var transport = new QwenAnalysisTests.TestTransport("""{"intentSummary":"Shipping plan","questions":[]}""", "Monday.");
        using var http = new HttpClient(transport);
        var conversation = new ConversationSessionManager(new FakeAudio(), new FakeSpeech(), new FakeNetwork(),
            new QwenAnalysisClient(http, store), new TranscriptEngine(), new ContextBuilder(new PromptBuilder()), archiveStore: local);
        try
        {
            var now = DateTimeOffset.UtcNow;
            const string transcript = "We agreed to ship Monday.";
            var recorded = new ConversationSession { Settings = new ConversationSettings(), StartTime = now, EndTime = now };
            recorded.TranscriptSegments.Add(new TranscriptSegment(Guid.NewGuid(), now, now, transcript, "Speech", true, 1));
            var path = await source.SaveAsync(ConversationArchive.Capture(recorded));
            await conversation.ImportConversationAsync(path);
            Assert.IsEmpty(transport.Calls);
            var analysis = conversation.AnalyzeConversation();
            await WaitUntil(() => analysis.Status is QuestionStatus.Completed or QuestionStatus.Failed);
            Assert.AreEqual(QuestionStatus.Completed, analysis.Status, analysis.Error);
            var answer = conversation.AskManually("When do we ship?");
            await WaitUntil(() => answer.Status is QuestionStatus.Completed or QuestionStatus.Failed);
            Assert.AreEqual(QuestionStatus.Completed, answer.Status, answer.Error);
            Assert.AreEqual("Monday.", answer.Answer);
            Assert.HasCount(2, transport.Calls);
            foreach (var call in transport.Calls)
            {
                Assert.AreEqual("token-plan.maas.qianwenaiapi.com", new Uri(call.Url).Host);
                Assert.AreEqual("Bearer " + QwenAnalysisTests.TokenPlanTestKey, call.Authorization);
                Assert.Contains(transcript, call.Body);
            }
        }
        finally
        {
            await conversation.EndConversationAsync();
            folder.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task StartingAndEndingATextConversationDoesNotTouchAudioOrSpeech()
    {
        var audio = new FakeAudio();
        var speech = new FakeSpeech();
        var store = new QwenAnalysisTests.TestStore();
        store.Save(QwenSettings.FromInput(QwenAnalysisTests.WorkspaceTestKey, QwenModels.DefaultSpeech,
            QwenModels.DefaultAnalysis, QwenBillingMode.TokenPlan, QwenAnalysisTests.TokenPlanTestKey));
        var transport = new QwenAnalysisTests.TestTransport("Token Plan answer");
        using var http = new HttpClient(transport);
        var conversation = new ConversationSessionManager(audio, speech, new FakeNetwork(),
            new QwenAnalysisClient(http, store), new TranscriptEngine(), new ContextBuilder(new PromptBuilder()));

        conversation.StartConversation(new ConversationSettings(), captureAudio: false);
        try
        {
            Assert.IsFalse(conversation.IsListening);
            Assert.ThrowsExactly<InvalidOperationException>(conversation.Resume);
            var answer = conversation.AskManually("Test the text conversation.");
            await WaitUntil(() => answer.Status is QuestionStatus.Completed or QuestionStatus.Failed);
            Assert.AreEqual(QuestionStatus.Completed, answer.Status, answer.Error);
            Assert.AreEqual("Token Plan answer", answer.Answer);
            Assert.AreEqual("token-plan.maas.qianwenaiapi.com", new Uri(transport.Calls.Single().Url).Host);
        }
        finally { await conversation.EndConversationAsync(); }

        Assert.AreEqual(0, audio.StartCount);
        Assert.AreEqual(0, audio.StopCount);
        Assert.AreEqual(0, speech.StartCount);
        Assert.AreEqual(0, speech.StopCount);
    }

    [TestMethod]
    public async Task QwenConversationPreservesFinalAudioOnPauseAndEndAndAnswersWithoutMicrosoftSignIn()
    {
        var folder = Path.Combine(Path.GetTempPath(), "qwen-session-" + Guid.NewGuid().ToString("N"));
        var profile = new LocalAppProfile(folder);
        var store = new QwenAnalysisTests.TestStore();
        store.Save(QwenSettings.FromInput(QwenAnalysisTests.TestKey, QwenModels.DefaultSpeech, QwenModels.DefaultAnalysis));
        var transport = new QwenAnalysisTests.TestTransport("Agreed: ship Monday.");
        using var http = new HttpClient(transport);
        var speech = new FakeSpeech();
        var archive = new JsonConversationArchiveStore(profile.Conversations);
        var conversation = new ConversationSessionManager(new FakeAudio(), speech, new FakeNetwork(),
            new QwenAnalysisClient(http, store), new TranscriptEngine(), new ContextBuilder(new PromptBuilder()), archiveStore: archive);

        try
        {
            profile.EnsureCreated();
            conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
            speech.BeforeStop = () => speech.EmitFinal("We agreed to ship Monday.", DateTimeOffset.UtcNow.AddSeconds(-1));
            conversation.Pause();
            Assert.AreEqual("We agreed to ship Monday.", conversation.TranscriptSegments.Single().Text);

            var answer = conversation.AskManually("What did we agree?");
            await WaitUntil(() => answer.Status is QuestionStatus.Completed or QuestionStatus.Failed);
            Assert.AreEqual(QuestionStatus.Completed, answer.Status, answer.Error);
            Assert.AreEqual("Agreed: ship Monday.", answer.Answer);
            using var json = JsonDocument.Parse(transport.Calls.Single().Body);
            Assert.AreEqual("qwen-plus", json.RootElement.GetProperty("model").GetString());
            Assert.IsFalse(json.RootElement.TryGetProperty("response_format", out _));
            var prompt = json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            StringAssert.Contains(prompt, "We agreed to ship Monday.");
            Assert.IsFalse(prompt.Contains("Work IQ", StringComparison.OrdinalIgnoreCase));

            conversation.Resume();
            speech.BeforeStop = () => speech.EmitFinal("Please notify the team.", DateTimeOffset.UtcNow.AddSeconds(-1));
            await conversation.EndConversationAsync();
            var saved = (await archive.LoadAsync(conversation.RecordPath!)).Restore();
            Assert.AreEqual(profile.Conversations, Path.GetDirectoryName(conversation.RecordPath));
            Assert.AreEqual(AnalysisProvider.Qwen, saved.Settings.AnalysisProvider);
            CollectionAssert.AreEqual(new[] { "We agreed to ship Monday.", "Please notify the team." },
                saved.TranscriptSegments.Select(turn => turn.Text).ToArray());
            Assert.IsTrue(conversation.AnalysisLogPath!.EndsWith(".qwen.log", StringComparison.Ordinal));
            Assert.AreEqual(profile.Conversations, Path.GetDirectoryName(conversation.AnalysisLogPath));
            Assert.IsFalse((await File.ReadAllTextAsync(conversation.AnalysisLogPath)).Contains(QwenAnalysisTests.TestKey, StringComparison.Ordinal));
        }
        finally
        {
            await conversation.EndConversationAsync();
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}