using ConversationAssistant.Core.Context;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Settings;
using ConversationAssistant.Core.Speech;
using ConversationAssistant.Core.Transcript;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class AzureSpeechPipelineTests
{
    [TestMethod]
    public void AzureRequiresSavedEndpointAndConsentBeforeConstructingSdkSession()
    {
        var settings = new FakeSettings(new SpeechServiceConfiguration());
        var factory = new FakeSessionFactory();
        using var service = new AzureSpeechRecognitionService(settings, factory);
        Assert.ThrowsExactly<InvalidOperationException>(() => service.Start(ConversationLanguage.Chinese));
        Assert.IsEmpty(factory.Sessions);
        settings.Save(Config(consent: false));
        Assert.ThrowsExactly<InvalidOperationException>(() => service.Start(ConversationLanguage.Chinese));
        Assert.IsEmpty(factory.Sessions);
    }

    [TestMethod]
    public void AzureStreamsSelectedPcmAndSeparatesPartialFromFinalIncludingRestart()
    {
        var settings = new FakeSettings(Config());
        var factory = new FakeSessionFactory();
        using var service = new AzureSpeechRecognitionService(settings, factory);
        var partial = 0;
        var final = 0;
        service.PartialTranscriptReceived += _ => partial++;
        service.FinalTranscriptReceived += _ => final++;
        service.Start(ConversationLanguage.Chinese);
        Assert.AreEqual(ConversationLanguage.Chinese, factory.Language);
        var first = factory.Sessions.Single();
        service.AcceptAudio([1, 2, 3, 4]);
        Assert.AreEqual(4, first.Bytes);
        first.EmitPartial("如何");
        Assert.AreEqual(1, partial);
        Assert.AreEqual(0, final);
        first.EmitFinal("如何设计DLP策略？");
        Assert.AreEqual(1, final);
        service.Stop();
        Assert.IsTrue(first.Disposed);
        first.EmitFinal("late event");
        Assert.AreEqual(1, final);
        service.Start(ConversationLanguage.English);
        Assert.AreEqual(ConversationLanguage.English, factory.Language);
        Assert.HasCount(2, factory.Sessions);
    }

    [TestMethod]
    public void AuthenticationFailureReleasesSessionAndDoesNotFallbackOrPersistAFalseSuccess()
    {
        var settings = new FakeSettings(Config());
        var factory = new FakeSessionFactory { FailStart = true };
        using var service = new AzureSpeechRecognitionService(settings, factory);
        Assert.ThrowsExactly<SpeechConnectionException>(() => service.Start(ConversationLanguage.Chinese));
        Assert.IsTrue(factory.Sessions.Single().Disposed);
        factory.FailStart = false;
        service.Start(ConversationLanguage.Chinese);
        Assert.HasCount(2, factory.Sessions);
    }

    [TestMethod]
    public void CancellationDuringSdkStartupCannotBeMistakenForSuccessfulListening()
    {
        var settings = new FakeSettings(Config());
        var factory = new FakeSessionFactory { FailDuringStart = true };
        using var service = new AzureSpeechRecognitionService(settings, factory);
        Assert.ThrowsExactly<SpeechConnectionException>(() => service.Start(ConversationLanguage.Chinese));
        Assert.IsTrue(factory.Sessions.Single().Disposed);
    }

    [TestMethod]
    public void ProviderChoiceIsExplicitAndNeverSilentlyChangesToCloud()
    {
        var settings = new FakeSettings(new SpeechServiceConfiguration { Provider = SpeechProvider.OfflineWhisper });
        var factory = new FakeSessionFactory();
        using var azure = new AzureSpeechRecognitionService(settings, factory);
        var offline = new FakeOffline();
        var router = new SelectableSpeechRecognitionService(settings, azure, offline);
        router.Start(ConversationLanguage.Chinese);
        Assert.AreEqual(1, offline.Starts);
        Assert.IsEmpty(factory.Sessions);
        router.Stop();
        settings.Save(Config());
        factory.FailStart = true;
        Assert.ThrowsExactly<SpeechConnectionException>(() => router.Start(ConversationLanguage.Chinese));
        Assert.AreEqual(1, offline.Starts);
    }

    [TestMethod]
    public async Task AzureFinalSpeechStaysLocalUntilRequestedAndConnectionLossStopsCaptureUntilResume()
    {
        var settings = new FakeSettings(Config());
        var factory = new FakeSessionFactory();
        using var azure = new AzureSpeechRecognitionService(settings, factory);
        var audio = new FakeAudio();
        var work = new FakeWork();
        var manager = new ConversationSessionManager(audio, azure, new FakeAuth(), new FakeNetwork(),
            work, new TranscriptEngine(), new ContextBuilder(new PromptBuilder()));
        manager.StartConversation(new ConversationSettings());
        var first = factory.Sessions.Single();
        first.EmitPartial("如何配置");
        Assert.AreEqual(0, work.Calls);
        first.EmitFinal("我们有二十个环境，如何配置DLP策略？");
        Assert.AreEqual(0, work.Calls);
        manager.AskFromTranscript(manager.TranscriptSegments.Single().Id);
        await WaitFor(() => work.Calls == 1);
        Assert.IsTrue(manager.IsListening);
        first.EmitFailure();
        Assert.IsFalse(manager.IsListening);
        Assert.AreEqual(ConversationUiState.Error, manager.State);
        await WaitFor(() => first.Disposed);
        Assert.AreEqual(1, audio.Stops);
        manager.Resume();
        Assert.IsTrue(manager.IsListening);
        Assert.HasCount(2, factory.Sessions);
        Assert.HasCount(1, manager.TranscriptSegments);
        await manager.EndConversationAsync();
        Assert.AreEqual(ConversationUiState.Idle, manager.State);
    }

    private static SpeechServiceConfiguration Config(bool consent = true) => new()
    {
        ServiceUri = "https://test.cognitiveservices.azure.com/",
        TenantId = "11111111-1111-1111-1111-111111111111",
        CloudAudioConsent = consent
    };

    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeSettings(SpeechServiceConfiguration configuration) : ISpeechSettingsStore
    {
        private SpeechServiceConfiguration _configuration = configuration;
        public SpeechServiceConfiguration Load() => _configuration;
        public void Save(SpeechServiceConfiguration value) => _configuration = value;
    }

    private sealed class FakeSessionFactory : IAzureSpeechSessionFactory
    {
        public List<FakeSession> Sessions { get; } = [];
        public ConversationLanguage Language { get; private set; }
        public bool FailStart { get; set; }
        public bool FailDuringStart { get; set; }
        public IAzureSpeechSession Create(SpeechServiceConfiguration configuration, ConversationLanguage language)
        {
            Language = language;
            var session = new FakeSession { FailStart = FailStart, FailDuringStart = FailDuringStart };
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class FakeSession : IAzureSpeechSession
    {
        public event Action<SpeechText>? Partial;
        public event Action<SpeechText>? Final;
        public event Action<SpeechConnectionException>? Failed;
        public int Bytes { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailStart { get; init; }
        public bool FailDuringStart { get; init; }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (FailDuringStart) EmitFailure();
            return FailStart
                ? Task.FromException(new SpeechConnectionException("Azure authentication failed."))
                : Task.CompletedTask;
        }
        public void Write(byte[] pcm) => Bytes += pcm.Length;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
        public void EmitPartial(string text) => Partial?.Invoke(new(text, DateTimeOffset.Now, DateTimeOffset.Now));
        public void EmitFinal(string text) => Final?.Invoke(new(text, DateTimeOffset.Now, DateTimeOffset.Now));
        public void EmitFailure() => Failed?.Invoke(new SpeechConnectionException("Azure connection unavailable."));
    }

    private sealed class FakeAudio : IAudioCaptureService
    {
        public event Action<byte[]>? AudioDataAvailable { add { } remove { } }
        public event Action<Exception>? AudioError { add { } remove { } }
        public event Action? AudioStarted { add { } remove { } }
        public event Action? AudioStopped { add { } remove { } }
        public event Action? AudioDeviceChanged { add { } remove { } }
        public int Stops { get; private set; }
        public IReadOnlyList<AudioDevice> ListDevices(AudioDeviceKind kind = AudioDeviceKind.Input) =>
            kind == AudioDeviceKind.Input ? [new("test", "Fake microphone")] : [new("test-output", "Fake speakers")];
        public void Start(AudioCaptureOptions options) { }
        public void Stop() => Stops++;
    }

    private sealed class FakeOffline : ISpeechRecognitionService
    {
        public event Action<SpeechText>? PartialTranscriptReceived { add { } remove { } }
        public event Action<SpeechText>? FinalTranscriptReceived { add { } remove { } }
        public event Action? SpeechStarted { add { } remove { } }
        public event Action? SpeechEnded { add { } remove { } }
        public event Action<Exception>? RecognitionError { add { } remove { } }
        public int Starts { get; private set; }
        public IReadOnlyList<ConversationLanguage> AvailableLanguages() => [ConversationLanguage.Chinese, ConversationLanguage.English];
        public void Start(ConversationLanguage language) => Starts++;
        public void AcceptAudio(byte[] pcm16Mono16000) { }
        public void Stop() { }
    }

    private sealed class FakeAuth : IAuthenticationService
    {
        public bool IsSignedIn => true;
        public Task SignInAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeNetwork : INetworkStatus { public bool IsAvailable => true; }

    private sealed class FakeWork : IWorkIqClient
    {
        public int Calls;
        public Task<WorkIqAnswer> AskAsync(string prompt, string? conversationId, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new WorkIqAnswer("A synthetic answer.", [], "test-conversation"));
        }
    }
}
