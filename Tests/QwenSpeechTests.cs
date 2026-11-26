using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Qwen;
using ConversationAssistant.Core.Speech;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class QwenSpeechTests
{
    private const string ApiKey = QwenAnalysisTests.WorkspaceTestKey;
    private static readonly DateTimeOffset AudioReceivedAt = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task RealtimeWaitsForUpdateThenSendsBase64AndDrainsVadFinal()
    {
        var (service, socket, _) = Create();
        var partial = Signal<SpeechText>();
        var final = Signal<SpeechText>();
        var ended = Signal<bool>();
        service.PartialTranscriptReceived += speech => partial.TrySetResult(speech);
        service.FinalTranscriptReceived += speech => final.TrySetResult(speech);
        service.SpeechEnded += () => ended.TrySetResult(final.Task.IsCompletedSuccessfully);

        var start = Task.Run(() => service.Start(ConversationLanguage.English));
        var update = (await socket.NextSentAsync()).Json;
        Assert.AreEqual("wss://maas.qianwenaiapi.com/api-ws/v1/realtime?model=qwen3-asr-flash-realtime", socket.Endpoint!.AbsoluteUri);
        Assert.AreEqual(ApiKey, socket.Key);
        Assert.IsTrue(socket.Realtime);
        Assert.AreEqual("session.update", update.GetProperty("type").GetString());
        var configuration = update.GetProperty("session");
        Assert.AreEqual("pcm", configuration.GetProperty("input_audio_format").GetString());
        Assert.AreEqual(16000, configuration.GetProperty("sample_rate").GetInt32());
        Assert.AreEqual("en", configuration.GetProperty("input_audio_transcription").GetProperty("language").GetString());
        Assert.AreEqual("server_vad", configuration.GetProperty("turn_detection").GetProperty("type").GetString());

        var pcm = new byte[3200];
        pcm[0] = 9;
        service.AcceptAudio(pcm);
        pcm[0] = 87;
        socket.Reply("""{"type":"session.created"}""");
        await socket.WaitForReceiveCountAsync(2);
        Assert.IsFalse(start.IsCompleted);
        Assert.IsFalse(socket.HasSent);
        socket.Reply("""{"type":"session.updated"}""", split: 10);
        await start.WaitAsync(Watchdog);

        var append = await socket.NextSentAsync();
        Assert.AreEqual(WebSocketMessageType.Text, append.Type);
        Assert.AreEqual("input_audio_buffer.append", append.Json.GetProperty("type").GetString());
        pcm[0] = 9;
        CollectionAssert.AreEqual(pcm, Convert.FromBase64String(append.Json.GetProperty("audio").GetString()!));
        socket.Reply("""{"type":"input_audio_buffer.speech_started","item_id":"item","audio_start_ms":20}""");
        socket.Reply("""{"type":"conversation.item.input_audio_transcription.text","item_id":"item","text":"Hello","stash":" world"}""");
        var origin = AudioReceivedAt.AddMilliseconds(-100);
        Assert.AreEqual(new SpeechText("Hello world", origin.AddMilliseconds(20), AudioReceivedAt), await partial.Task.WaitAsync(Watchdog));
        Assert.IsFalse(final.Task.IsCompleted);

        var stop = Task.Run(service.Stop);
        Assert.AreEqual("session.finish", (await socket.NextSentAsync()).Json.GetProperty("type").GetString());
        socket.Reply("""{"type":"input_audio_buffer.speech_stopped","item_id":"item","audio_end_ms":80}""");
        socket.Reply("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"item","transcript":"Hello world!"}""");
        Assert.AreEqual(new SpeechText("Hello world!", origin.AddMilliseconds(20), origin.AddMilliseconds(80)), await final.Task.WaitAsync(Watchdog));
        Assert.IsFalse(stop.IsCompleted);
        socket.Reply("""{"type":"session.finished"}""");
        await stop.WaitAsync(Watchdog);
        Assert.IsTrue(await ended.Task.WaitAsync(Watchdog));
        Assert.IsTrue(socket.Closed && socket.Disposed.Task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task InferenceUsesTaskIdsAndBinaryPcmAndDrainsSentenceTimestamps()
    {
        var (service, socket, _) = Create(QwenModels.AlternateSpeech);
        var partial = Signal<SpeechText>();
        var final = Signal<SpeechText>();
        service.PartialTranscriptReceived += speech => partial.TrySetResult(speech);
        service.FinalTranscriptReceived += speech => final.TrySetResult(speech);

        var start = Task.Run(() => service.Start(ConversationLanguage.Chinese));
        var setup = (await socket.NextSentAsync()).Json;
        var taskId = TaskId(setup);
        Assert.AreEqual("wss://maas.qianwenaiapi.com/api-ws/v1/inference", socket.Endpoint!.AbsoluteUri);
        Assert.AreEqual(ApiKey, socket.Key);
        Assert.IsFalse(socket.Realtime);
        Assert.IsTrue(Guid.TryParse(taskId, out _));
        Assert.AreEqual("run-task", setup.GetProperty("header").GetProperty("action").GetString());
        Assert.AreEqual("duplex", setup.GetProperty("header").GetProperty("streaming").GetString());
        var payload = setup.GetProperty("payload");
        Assert.AreEqual(QwenModels.AlternateSpeech, payload.GetProperty("model").GetString());
        Assert.AreEqual("asr", payload.GetProperty("task").GetString());
        Assert.AreEqual("recognition", payload.GetProperty("function").GetString());
        var parameters = payload.GetProperty("parameters");
        Assert.AreEqual("pcm", parameters.GetProperty("format").GetString());
        Assert.AreEqual(16000, parameters.GetProperty("sample_rate").GetInt32());
        Assert.AreEqual("zh", parameters.GetProperty("language_hints")[0].GetString());

        var pcm = new byte[6400];
        pcm[0] = 1;
        pcm[3200] = 2;
        service.AcceptAudio(pcm);
        socket.TaskReply("different-task-id", "task-started");
        await socket.WaitForReceiveCountAsync(2);
        Assert.IsFalse(start.IsCompleted);
        Assert.IsFalse(socket.HasSent);
        socket.TaskReply(taskId, "task-started");
        await start.WaitAsync(Watchdog);
        var stop = Task.Run(service.Stop);
        var first = await socket.NextSentAsync();
        var second = await socket.NextSentAsync();
        Assert.AreEqual(WebSocketMessageType.Binary, first.Type);
        Assert.AreEqual(WebSocketMessageType.Binary, second.Type);
        CollectionAssert.AreEqual(pcm[..3200], first.Bytes);
        CollectionAssert.AreEqual(pcm[3200..], second.Bytes);

        socket.TaskReply(taskId, "result-generated", """{"output":{"sentence":{"heartbeat":true,"text":"not speech"}}}""");
        socket.TaskReply(taskId, "result-generated", """{"output":{"sentence":{"text":"你好","begin_time":25,"end_time":null,"sentence_end":false,"words":[{"end_time":50}]}},"usage":{"duration":1}}""");
        var origin = AudioReceivedAt.AddMilliseconds(-200);
        Assert.AreEqual(new SpeechText("你好", origin.AddMilliseconds(25), origin.AddMilliseconds(50)), await partial.Task.WaitAsync(Watchdog));
        Assert.IsFalse(final.Task.IsCompleted);

        var finish = (await socket.NextSentAsync()).Json;
        Assert.AreEqual("finish-task", finish.GetProperty("header").GetProperty("action").GetString());
        Assert.AreEqual(taskId, TaskId(finish));
        socket.TaskReply(taskId, "result-generated", """{"output":{"sentence":{"text":"你好。","begin_time":25,"end_time":150,"sentence_end":true,"channel_id":0}}}""");
        socket.TaskReply(taskId, "task-finished");
        await stop.WaitAsync(Watchdog);
        Assert.AreEqual(new SpeechText("你好。", origin.AddMilliseconds(25), origin.AddMilliseconds(150)), await final.Task.WaitAsync(Watchdog));
        Assert.IsTrue(socket.Closed && socket.Disposed.Task.IsCompletedSuccessfully);
    }

    [TestMethod]
    [DataRow(QwenModels.DefaultSpeech)]
    [DataRow(QwenModels.AlternateSpeech)]
    public async Task ProbeWaitsForSetupAndClosesWithoutSubmittingAudio(string model)
    {
        var (service, socket, _) = Create(model);
        var started = false;
        service.SpeechStarted += () => started = true;
        var probe = service.TestAsync(ConversationLanguage.Auto);
        var setup = (await socket.NextSentAsync()).Json;
        await socket.WaitForReceiveCountAsync(1);
        Assert.IsFalse(probe.IsCompleted);
        if (model == QwenModels.DefaultSpeech)
        {
            Assert.IsFalse(setup.GetProperty("session").TryGetProperty("input_audio_transcription", out _));
            socket.Reply("""{"type":"session.updated"}""");
        }
        else
        {
            Assert.IsFalse(setup.GetProperty("payload").GetProperty("parameters").TryGetProperty("language_hints", out _));
            socket.TaskReply(TaskId(setup), "task-started");
        }
        await probe.WaitAsync(Watchdog);
        Assert.IsTrue(socket.Closed && socket.Disposed.Task.IsCompletedSuccessfully);
        Assert.IsFalse(socket.HasSent || started);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task BothModelProbesUseTheSameSavedPayAsYouGoKey()
    {
        var path = Path.Combine(Path.GetTempPath(), "qwen-probe-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            new ProtectedQwenSettingsStore(path).Save(
                QwenSettings.FromInput("\tBearer '" + ApiKey + "'\r\n", QwenModels.DefaultSpeech, "qwen-flash"));
            var reloadedStore = new ProtectedQwenSettingsStore(path);
            var socket = new FakeSocket();
            using var speech = new QwenSpeechRecognitionService(reloadedStore, () => socket, new TestClock());
            var transport = new QwenAnalysisTests.TestTransport("""{"ok":true}""");
            using var http = new HttpClient(transport);
            var analysis = new QwenAnalysisClient(http, reloadedStore);

            socket.Reply("""{"type":"session.updated"}""");
            await speech.TestAsync(ConversationLanguage.Chinese).WaitAsync(Watchdog);
            await analysis.TestAsync();

            Assert.AreEqual(ApiKey, socket.Key);
            Assert.AreEqual("wss://maas.qianwenaiapi.com/api-ws/v1/realtime?model=qwen3-asr-flash-realtime", socket.Endpoint!.AbsoluteUri);
            var request = transport.Calls.Single();
            Assert.AreEqual("https://maas.qianwenaiapi.com/compatible-mode/v1/chat/completions", request.Url);
            Assert.AreEqual("Bearer " + ApiKey, request.Authorization);
            using var body = JsonDocument.Parse(request.Body);
            Assert.AreEqual("qwen-flash", body.RootElement.GetProperty("model").GetString());
            Assert.IsFalse(body.RootElement.TryGetProperty("max_tokens", out _));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task TokenPlanWithTranscriptionOffNeverOpensThePayAsYouGoSpeechSocket()
    {
        var store = new QwenAnalysisTests.TestStore();
        var socket = new FakeSocket();
        var opened = 0;
        using var speech = new QwenSpeechRecognitionService(store, () => { opened++; return socket; }, new TestClock());
        store.Save(QwenSettings.FromInput("", QwenModels.DefaultSpeech, QwenModels.DefaultAnalysis,
            QwenBillingMode.TokenPlan, QwenAnalysisTests.TokenPlanTestKey));

        await Assert.ThrowsExactlyAsync<SpeechConnectionException>(() => speech.TestAsync(ConversationLanguage.Chinese));
        Assert.AreEqual(0, opened);

        store.Save(QwenSettings.FromInput(ApiKey, QwenModels.DefaultSpeech, QwenModels.DefaultAnalysis,
            QwenBillingMode.TokenPlan, QwenAnalysisTests.TokenPlanTestKey));
        await Assert.ThrowsExactlyAsync<SpeechConnectionException>(() => speech.TestAsync(ConversationLanguage.Chinese));
        Assert.AreEqual(0, opened);

        store.Save(QwenSettings.FromInput(ApiKey, QwenModels.DefaultSpeech, QwenModels.DefaultAnalysis,
            QwenBillingMode.PayAsYouGo, QwenAnalysisTests.TokenPlanTestKey));
        socket.Reply("""{"type":"session.updated"}""");
        await speech.TestAsync(ConversationLanguage.Chinese).WaitAsync(Watchdog);

        Assert.AreEqual(1, opened);
        Assert.AreEqual("maas.qianwenaiapi.com", socket.Endpoint!.Host);
        Assert.AreEqual(ApiKey, socket.Key);
    }

    [TestMethod]
    public async Task TokenPlanUploadsEightSecondWavAndFlushesTrailingAudioUsingOnlyItsOwnModelAndKey()
    {
        var store = new QwenAnalysisTests.TestStore();
        store.Save(QwenSettings.FromInput(ApiKey, QwenModels.AlternateSpeech, QwenModels.DefaultAnalysis,
            QwenBillingMode.TokenPlan, QwenAnalysisTests.TokenPlanTestKey, tokenPlanSpeechModel: QwenModels.TokenPlanAsrFlash));
        var transport = new TokenPlanTransport();
        using var http = new HttpClient(transport);
        using var speech = new QwenSpeechRecognitionService(store, () => throw new AssertFailedException("Unexpected WebSocket"),
            new TestClock(), http);
        var results = Channel.CreateUnbounded<SpeechText>();
        speech.FinalTranscriptReceived += text => results.Writer.TryWrite(text);

        speech.Start(ConversationLanguage.Chinese);
        var frame = new byte[32000];
        frame[0] = 17;
        for (var index = 0; index < 8; index++) speech.AcceptAudio(frame);
        var first = await results.Reader.ReadAsync().AsTask().WaitAsync(Watchdog);
        speech.AcceptAudio(frame);
        await Task.Run(speech.Stop).WaitAsync(Watchdog);
        var last = await results.Reader.ReadAsync().AsTask().WaitAsync(Watchdog);

        Assert.AreEqual("Segment 1", first.Text);
        Assert.AreEqual("Segment 2", last.Text);
        Assert.AreEqual(TimeSpan.FromSeconds(8), first.End - first.Start);
        Assert.AreEqual(first.End, last.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(1), last.End - last.Start);
        Assert.HasCount(2, transport.Calls);
        for (var index = 0; index < 2; index++)
        {
            var (uri, key, body) = transport.Calls[index];
            Assert.AreEqual("https://token-plan.maas.qianwenaiapi.com/api/v1/services/aigc/multimodal-generation/generation", uri);
            Assert.AreEqual("Bearer " + QwenAnalysisTests.TokenPlanTestKey, key);
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            Assert.AreEqual(QwenModels.TokenPlanAsrFlash, root.GetProperty("model").GetString());
            Assert.AreEqual("zh", root.GetProperty("parameters").GetProperty("language_hints")[0].GetString());
            var uriData = root.GetProperty("input").GetProperty("messages")[0].GetProperty("content")[0]
                .GetProperty("input_audio").GetProperty("data").GetString()!;
            const string prefix = "data:audio/wav;base64,";
            Assert.StartsWith(prefix, uriData);
            var wav = Convert.FromBase64String(uriData[prefix.Length..]);
            var pcmLength = index == 0 ? 8 * frame.Length : frame.Length;
            Assert.AreEqual(44 + pcmLength, wav.Length);
            Assert.AreEqual("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
            Assert.AreEqual(16000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
            Assert.AreEqual(pcmLength, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
            for (var offset = 44; offset < wav.Length; offset += frame.Length)
                Assert.IsTrue(frame.AsSpan().SequenceEqual(wav.AsSpan(offset, frame.Length)));
        }
    }

    [TestMethod]
    public async Task TokenPlanSpeechQuotaErrorIsLocalizedWithoutFallingBack()
    {
        var store = new QwenAnalysisTests.TestStore();
        store.Save(QwenSettings.FromInput(ApiKey, QwenModels.DefaultSpeech, QwenModels.DefaultAnalysis,
            QwenBillingMode.TokenPlan, QwenAnalysisTests.TokenPlanTestKey, tokenPlanSpeechModel: QwenModels.TokenPlanAsrFlash));
        var transport = new TokenPlanTransport { Status = HttpStatusCode.TooManyRequests };
        using var http = new HttpClient(transport);
        using var speech = new QwenSpeechRecognitionService(store, () => throw new AssertFailedException("Unexpected WebSocket"),
            new TestClock(), http);
        speech.Start(ConversationLanguage.Auto);
        speech.AcceptAudio([1, 0]);

        var error = await Assert.ThrowsExactlyAsync<SpeechConnectionException>(() => Task.Run(speech.Stop).WaitAsync(Watchdog));

        Assert.AreEqual("ErrorQwenTokenPlanSpeechQuota", UiMessage.FromDiagnostic(error.Message)?.Key);
        Assert.DoesNotContain(QwenAnalysisTests.TokenPlanTestKey, error.Message);
        Assert.HasCount(1, transport.Calls);
    }

    private sealed class TokenPlanTransport : HttpMessageHandler
    {
        internal List<(string Uri, string? Authorization, string Body)> Calls { get; } = [];
        internal HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            var body = Status == HttpStatusCode.OK ? JsonSerializer.Serialize(new { output = new { text = $"Segment {Calls.Count}" } }) : "{}";
            return new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [TestMethod]
    public async Task ProbeCancellationReleasesSocketAndPreservesCallerToken()
    {
        var (service, socket, _) = Create();
        using var cancellation = new CancellationTokenSource();
        var probe = service.TestAsync(ConversationLanguage.Auto, cancellation.Token);
        await socket.WaitForReceiveCountAsync(1);
        cancellation.Cancel();
        var error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => probe.WaitAsync(Watchdog));
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        await socket.Disposed.Task.WaitAsync(Watchdog);
    }

    [TestMethod]
    [DataRow(QwenModels.DefaultSpeech)]
    [DataRow(QwenModels.AlternateSpeech)]
    public async Task SetupRejectionNeverExposesServiceErrorDetails(string model)
    {
        var (service, socket, _) = Create(model);
        var start = Task.Run(() => service.Start(ConversationLanguage.Auto));
        var setup = (await socket.NextSentAsync()).Json;
        if (model == QwenModels.DefaultSpeech)
            socket.Reply($$"""{"type":"error","error":{"message":"{{ApiKey}}"} }""");
        else
            socket.Reply($$"""{"header":{"task_id":"{{TaskId(setup)}}","event":"task-failed","error_message":"{{ApiKey}}"} }""");
        var error = await Assert.ThrowsExactlyAsync<SpeechConnectionException>(() => start.WaitAsync(Watchdog));
        StringAssert.Contains(error.Message, "Qwen rejected the speech request");
        Assert.IsFalse(error.ToString().Contains(ApiKey, StringComparison.Ordinal));
        Assert.IsNull(error.InnerException);
        Assert.IsTrue(socket.Disposed.Task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task BlockedNetworkDoesNotBlockAudioProducerAndOverflowSignalsFailure()
    {
        var (service, socket, _) = Create();
        var error = Signal<Exception>();
        service.RecognitionError += exception => error.TrySetResult(exception);
        var start = Task.Run(() => service.Start(ConversationLanguage.Auto));
        await socket.NextSentAsync();
        socket.BlockAudio = true;
        socket.Reply("""{"type":"session.updated"}""");
        await start.WaitAsync(Watchdog);
        service.AcceptAudio(new byte[3200]);
        await socket.AudioBlocked.Task.WaitAsync(Watchdog);

        await Task.Run(() =>
        {
            for (var i = 0; i < 100; ++i) service.AcceptAudio(new byte[3200]);
        }).WaitAsync(Watchdog);
        var failure = await error.Task.WaitAsync(Watchdog);
        Assert.IsInstanceOfType<SpeechConnectionException>(failure);
        StringAssert.Contains(failure.Message, "could not keep up");
        Assert.IsTrue(socket.Disposed.Task.IsCompletedSuccessfully);
        service.Stop();
    }

    [TestMethod]
    public async Task RuntimeSocketFailureIsSanitizedAndAllowsRestartAfterStop()
    {
        var first = new FakeSocket();
        var second = new FakeSocket();
        var sockets = new Queue<FakeSocket>([first, second]);
        var settings = new FakeSettings(new QwenSettings { ApiKey = ApiKey });
        var service = new QwenSpeechRecognitionService(settings, sockets.Dequeue, new TestClock());
        var failure = Signal<Exception>();
        service.RecognitionError += error => failure.TrySetResult(error);

        var start = Task.Run(() => service.Start(ConversationLanguage.Auto));
        await first.NextSentAsync();
        first.Reply("""{"type":"session.updated"}""");
        await start.WaitAsync(Watchdog);
        first.FailReceive(new SpeechConnectionException(ApiKey));
        var error = await failure.Task.WaitAsync(Watchdog);
        Assert.IsInstanceOfType<SpeechConnectionException>(error);
        StringAssert.Contains(error.Message, "recognition was interrupted");
        Assert.IsFalse(error.ToString().Contains(ApiKey, StringComparison.Ordinal));
        Assert.IsNull(error.InnerException);
        Assert.IsTrue(first.Disposed.Task.IsCompletedSuccessfully);
        service.Stop();

        var restart = Task.Run(() => service.Start(ConversationLanguage.Auto));
        await second.NextSentAsync();
        second.Reply("""{"type":"session.updated"}""");
        await restart.WaitAsync(Watchdog);
        var stop = Task.Run(service.Stop);
        Assert.AreEqual("session.finish", (await second.NextSentAsync()).Json.GetProperty("type").GetString());
        second.Reply("""{"type":"session.finished"}""");
        await stop.WaitAsync(Watchdog);
        Assert.IsTrue(second.Closed);
    }

    [TestMethod]
    public async Task StopDeadlineReleasesSocketEvenWithoutAFinishAcknowledgement()
    {
        var (service, socket, clock) = Create();
        var start = Task.Run(() => service.Start(ConversationLanguage.Auto));
        await socket.NextSentAsync();
        socket.Reply("""{"type":"session.updated"}""");
        await start.WaitAsync(Watchdog);

        var stop = Task.Run(service.Stop);
        Assert.AreEqual("session.finish", (await socket.NextSentAsync()).Json.GetProperty("type").GetString());
        await clock.FinishTimerRegistered.Task.WaitAsync(Watchdog);
        clock.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.IsFalse(stop.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var error = await Assert.ThrowsExactlyAsync<SpeechConnectionException>(() => stop.WaitAsync(Watchdog));
        StringAssert.Contains(error.Message, "did not finish in time");
        await socket.Disposed.Task.WaitAsync(Watchdog);
    }

    private static string TaskId(JsonElement root) => root.GetProperty("header").GetProperty("task_id").GetString()!;
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static (QwenSpeechRecognitionService Service, FakeSocket Socket, TestClock Clock) Create(string model = QwenModels.DefaultSpeech)
    {
        var socket = new FakeSocket();
        var clock = new TestClock();
        var settings = new FakeSettings(new QwenSettings { ApiKey = ApiKey, SpeechModel = model });
        return (new QwenSpeechRecognitionService(settings, () => socket, clock), socket, clock);
    }

    private sealed class FakeSettings(QwenSettings settings) : IQwenSettingsStore
    {
        public QwenSettings Load() => settings;
        public void Save(QwenSettings updated) => throw new NotSupportedException();
    }

    private sealed class FakeSocket : IQwenSpeechSocket
    {
        private readonly Channel<Frame> _outgoing = Channel.CreateUnbounded<Frame>();
        private readonly Channel<Frame> _incoming = Channel.CreateUnbounded<Frame>();
        private readonly Channel<int> _receiveCalls = Channel.CreateUnbounded<int>();
        private int _receiveCount;
        public Uri? Endpoint { get; private set; }
        public string? Key { get; private set; }
        public bool Realtime { get; private set; }
        public bool Closed { get; private set; }
        public bool HasSent => _outgoing.Reader.TryPeek(out _);
        public bool BlockAudio { get; set; }
        public TaskCompletionSource<bool> AudioBlocked { get; } = Signal<bool>();
        public TaskCompletionSource<bool> Disposed { get; } = Signal<bool>();

        public async Task<Frame> NextSentAsync() => await _outgoing.Reader.ReadAsync().AsTask().WaitAsync(Watchdog);

        public async Task WaitForReceiveCountAsync(int count)
        {
            while (await _receiveCalls.Reader.ReadAsync().AsTask().WaitAsync(Watchdog) < count) { }
        }

        public void Reply(string json, int split = 0)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            if (split > 0) _incoming.Writer.TryWrite(new Frame(WebSocketMessageType.Text, bytes[..split], false));
            _incoming.Writer.TryWrite(new Frame(WebSocketMessageType.Text, bytes[split..]));
        }

        public void TaskReply(string taskId, string name, string payload = "{}") =>
            Reply($$"""{"header":{"task_id":"{{taskId}}","event":"{{name}}"},"payload":{{payload}} }""");

        public void FailReceive(Exception error) => _incoming.Writer.TryComplete(error);

        public Task ConnectAsync(Uri endpoint, string apiKey, bool realtime, CancellationToken token)
        {
            Endpoint = endpoint;
            Key = apiKey;
            Realtime = realtime;
            return Task.CompletedTask;
        }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> message, WebSocketMessageType type, CancellationToken token)
        {
            _outgoing.Writer.TryWrite(new Frame(type, message.ToArray()));
            if (!BlockAudio) return;
            AudioBlocked.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }

        public async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken token)
        {
            _receiveCalls.Writer.TryWrite(Interlocked.Increment(ref _receiveCount));
            var frame = await _incoming.Reader.ReadAsync(token);
            frame.Bytes.AsSpan().CopyTo(buffer.Span);
            return new ValueWebSocketReceiveResult(frame.Bytes.Length, frame.Type, frame.EndOfMessage);
        }

        public Task CloseOutputAsync(CancellationToken token)
        {
            Closed = true;
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed.TrySetResult(true);
    }

    private sealed record Frame(WebSocketMessageType Type, byte[] Bytes, bool EndOfMessage = true)
    {
        internal JsonElement Json
        {
            get { using var json = JsonDocument.Parse(Bytes); return json.RootElement.Clone(); }
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<Timer> _timers = [];
        private DateTimeOffset _now = AudioReceivedAt;
        internal TaskCompletionSource<bool> FinishTimerRegistered { get; } = Signal<bool>();

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timer.Change(dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(5)) FinishTimerRegistered.TrySetResult(true);
            return timer;
        }

        internal void Advance(TimeSpan amount)
        {
            Timer[] ready;
            lock (_gate)
            {
                _now += amount;
                ready = _timers.Where(timer => timer.Due <= _now).ToArray();
                foreach (var timer in ready) _timers.Remove(timer);
            }
            foreach (var timer in ready) timer.Fire();
        }

        private sealed class Timer(TestClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset Due { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    clock._timers.Remove(this);
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    {
                        Due = clock._now + dueTime;
                        clock._timers.Add(this);
                    }
                }
                return true;
            }
            internal void Fire() => callback(state);
            public void Dispose() { lock (clock._gate) clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}