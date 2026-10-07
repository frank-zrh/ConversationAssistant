using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Speech;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class OfflineSpeakerTurnTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow("First speaker.", "Second speaker.", "First speaker again.")]
    [DataRow("我们先讨论预算。", "接下来讨论日程。", "再确认一下预算。")]
    public async Task ContinuousTwelveSecondWindowKeepsEveryAcousticTurnAndItsExactText(
        string first, string second, string third)
    {
        var samples = Enumerable.Repeat(.1f, 64000)
            .Concat(Enumerable.Repeat(-.2f, 48000)).Concat(Enumerable.Repeat(.3f, 80000)).ToArray();
        using var diarizer = new FakeDiarizer(
            new(0, 64000, "session:a"), new(64000, 112000, "session:b"), new(112000, 192000, "session:a"));
        var inputs = new List<float[]>();
        var strings = new Queue<string>([first, second, third]);
        var transcripts = await Collect(OfflineSpeakerTurnTranscriber.TranscribeAsync(
            new SpeechAudioWindow(samples, Start, Start.AddSeconds(12), true), diarizer,
            (turn, _) =>
            {
                inputs.Add((float[])turn.Clone());
                return Task.FromResult(strings.Dequeue());
            }));

        CollectionAssert.AreEqual(new[] { first, second, third }, transcripts.Select(t => t.Text).ToArray());
        CollectionAssert.AreEqual(new[] { "session:a", "session:b", "session:a" }, transcripts.Select(t => t.SpeakerId).ToArray());
        CollectionAssert.AreEqual(new[] { Start, Start.AddSeconds(4), Start.AddSeconds(7) },
            transcripts.Select(t => t.Start).ToArray());
        CollectionAssert.AreEqual(new[] { Start.AddSeconds(4), Start.AddSeconds(7), Start.AddSeconds(12) },
            transcripts.Select(t => t.End).ToArray());
        CollectionAssert.AreEqual(new[] { 64000, 48000, 80000 }, inputs.Select(input => input.Length).ToArray());
        Assert.IsTrue(inputs[0].All(value => value == .1f));
        Assert.IsTrue(inputs[1].All(value => value == -.2f));
        Assert.IsTrue(inputs[2].All(value => value == .3f));
        Assert.AreEqual(.1f, samples[0]);
    }

    [TestMethod]
    public async Task TurnTimingUsesSampleOffsetsNotWholeWindowOrProcessingClock()
    {
        using var diarizer = new FakeDiarizer(new OfflineSpeakerTurn(1234, 31789, "s:a"));
        var transcript = (await Collect(OfflineSpeakerTurnTranscriber.TranscribeAsync(
            new SpeechAudioWindow(new float[48000], Start, Start.AddSeconds(3), true), diarizer,
            (_, _) => Task.FromResult("  Keep the original text.  ")))).Single();
        Assert.AreEqual("  Keep the original text.  ", transcript.Text);
        Assert.AreEqual(Start.AddSeconds(1234d / 16000), transcript.Start);
        Assert.AreEqual(Start.AddSeconds(31789d / 16000), transcript.End);
    }

    [TestMethod]
    public async Task NoAcousticSpeechDoesNotCallWhisperOrEmitText()
    {
        using var diarizer = new FakeDiarizer();
        var transcribed = false;
        var result = await Collect(OfflineSpeakerTurnTranscriber.TranscribeAsync(
            new SpeechAudioWindow(new float[192000], Start, Start.AddSeconds(12), true), diarizer,
            (_, _) =>
            {
                transcribed = true;
                return Task.FromResult("Must not hallucinate.");
            }));
        Assert.IsEmpty(result);
        Assert.IsFalse(transcribed);
    }

    [TestMethod]
    public async Task OverlappingTurnsAreRetainedWithoutClippingAwayEitherSpeaker()
    {
        using var diarizer = new FakeDiarizer(new(0, 32000, "s:a"), new(16000, 48000, "s:b"));
        var result = await Collect(OfflineSpeakerTurnTranscriber.TranscribeAsync(
            new SpeechAudioWindow(new float[48000], Start, Start.AddSeconds(3), true), diarizer,
            (_, _) => Task.FromResult("Recognized turn")));
        Assert.HasCount(2, result);
        Assert.AreEqual(Start.AddSeconds(2), result[0].End);
        Assert.AreEqual(Start.AddSeconds(1), result[1].Start);
        Assert.AreNotEqual(result[0].SpeakerId, result[1].SpeakerId);
    }

    [TestMethod]
    public async Task CancellationBeforeAnalysisDoesNotInvokeModels()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using var diarizer = new FakeDiarizer(new OfflineSpeakerTurn(0, 32000, "s:a"));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => Collect(
            OfflineSpeakerTurnTranscriber.TranscribeAsync(Window(), diarizer,
                (_, _) => Task.FromResult("Unexpected"), cancel.Token)));
        Assert.AreEqual(0, diarizer.Calls);
    }

    [TestMethod]
    public async Task CancellationDuringNativeAnalysisSuppressesAllTranscripts()
    {
        using var cancel = new CancellationTokenSource();
        using var diarizer = new FakeDiarizer(new OfflineSpeakerTurn(0, 32000, "s:a")) { DuringAnalyze = cancel.Cancel };
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => Collect(
            OfflineSpeakerTurnTranscriber.TranscribeAsync(Window(), diarizer,
                (_, _) => throw new AssertFailedException("Whisper ran after cancellation."), cancel.Token)));
    }

    [TestMethod]
    public async Task CancellationDuringTranscriptionDoesNotPublishOrStartNextTurn()
    {
        using var cancel = new CancellationTokenSource();
        using var diarizer = new FakeDiarizer(new(0, 16000, "s:a"), new(16000, 32000, "s:b"));
        var calls = 0;
        float[]? input = null;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => Collect(
            OfflineSpeakerTurnTranscriber.TranscribeAsync(Window(), diarizer, (audio, _) =>
            {
                input = audio;
                calls++;
                cancel.Cancel();
                return Task.FromResult("Stale result");
            }, cancel.Token)));
        Assert.AreEqual(1, calls);
        Assert.IsNotNull(input);
        Assert.IsTrue(input.All(sample => sample == 0));
    }

    [TestMethod]
    public async Task CancellingAfterFirstTurnPreservesItButNeverStartsTheNextSpeaker()
    {
        using var cancel = new CancellationTokenSource();
        using var diarizer = new FakeDiarizer(new(0, 16000, "s:a"), new(16000, 32000, "s:b"));
        var calls = 0;
        await using var results = OfflineSpeakerTurnTranscriber.TranscribeAsync(Window(), diarizer, (_, _) =>
        {
            calls++;
            return Task.FromResult("First turn.");
        }, cancel.Token).GetAsyncEnumerator();
        Assert.IsTrue(await results.MoveNextAsync());
        Assert.AreEqual("s:a", results.Current.SpeakerId);
        cancel.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => { await results.MoveNextAsync(); });
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task EmptyRecognitionDoesNotSwallowTheNextSpeakersTurn()
    {
        using var diarizer = new FakeDiarizer(new(0, 16000, "s:a"), new(16000, 32000, "s:b"));
        var strings = new Queue<string>(["", "Second turn."]);
        var result = await Collect(OfflineSpeakerTurnTranscriber.TranscribeAsync(Window(), diarizer,
            (_, _) => Task.FromResult(strings.Dequeue())));
        Assert.HasCount(1, result);
        Assert.AreEqual("Second turn.", result[0].Text);
        Assert.AreEqual("s:b", result[0].SpeakerId);
        Assert.AreEqual(Start.AddSeconds(1), result[0].Start);
    }

    [TestMethod]
    public async Task UnknownSpeakerStaysNullWithoutCombiningSeparateAcousticTurns()
    {
        using var diarizer = new FakeDiarizer(new(0, 16000, null), new(16000, 32000, null));
        var strings = new Queue<string>(["First unknown turn.", "Second unknown turn."]);
        var result = await Collect(OfflineSpeakerTurnTranscriber.TranscribeAsync(Window(), diarizer,
            (_, _) => Task.FromResult(strings.Dequeue())));
        Assert.HasCount(2, result);
        Assert.IsTrue(result.All(text => text.SpeakerId is null));
        Assert.AreEqual("First unknown turn.", result[0].Text);
        Assert.AreEqual("Second unknown turn.", result[1].Text);
        Assert.AreEqual(Start.AddSeconds(1), result[0].End);
        Assert.AreEqual(Start.AddSeconds(1), result[1].Start);
    }

    [TestMethod]
    public async Task InvalidTurnBoundariesFailBeforeTranscribingAnySpeaker()
    {
        using var diarizer = new FakeDiarizer(new(0, 16000, "s:a"), new(16000, 32001, "s:b"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Collect(
            OfflineSpeakerTurnTranscriber.TranscribeAsync(Window(), diarizer,
                (_, _) => throw new AssertFailedException("Invalid results must not be partially published."))));
    }

    [TestMethod]
    public void UnsupportedOrUnboundedAudioHasAnActionableDiagnostic()
    {
        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            OfflineSpeakerTurnTranscriber.ValidateAudio([float.NaN]));
        StringAssert.Contains(error.Message, "16 kHz mono");
        Assert.ThrowsExactly<InvalidDataException>(() => OfflineSpeakerTurnTranscriber.ValidateAudio([1.1f]));
        Assert.ThrowsExactly<InvalidDataException>(() => OfflineSpeakerTurnTranscriber.ValidateAudio(
            new float[OfflineSpeakerTurnTranscriber.MaximumWindowSamples + 1]));
    }

    [TestMethod]
    public void AcousticMatchingStabilizesIdsWhenWindowLocalLabelsSwap()
    {
        using var identities = new OfflineSpeakerIdentityMap();
        var first = identities.Identify(new Dictionary<int, float[]?> { [0] = [1, 0, 0], [1] = [0, 1, 0] });
        var second = identities.Identify(new Dictionary<int, float[]?> { [0] = [0, 1, .01f], [1] = [1, 0, .01f] });
        Assert.AreEqual(first[0], second[1]);
        Assert.AreEqual(first[1], second[0]);
        Assert.AreNotEqual(first[0], first[1]);
    }

    [TestMethod]
    public void DifferentLocalSpeakersNeverMergeEvenWithSimilarEmbeddings()
    {
        using var identities = new OfflineSpeakerIdentityMap();
        var first = identities.Identify(new Dictionary<int, float[]?> { [0] = [1, 0] });
        var second = identities.Identify(new Dictionary<int, float[]?> { [0] = [1, 0], [1] = [1, .01f] });
        Assert.AreEqual(first[0], second[0]);
        Assert.AreNotEqual(second[0], second[1]);
    }

    [TestMethod]
    public void AnonymousIdentitiesNeverLinkSeparateListeningSessions()
    {
        using var first = new OfflineSpeakerIdentityMap();
        using var second = new OfflineSpeakerIdentityMap();
        var embeddings = new Dictionary<int, float[]?> { [0] = [1, 0] };
        var firstId = first.Identify(embeddings)[0];
        var secondId = second.Identify(embeddings)[0];
        Assert.IsNotNull(firstId);
        Assert.IsNotNull(secondId);
        StringAssert.StartsWith(firstId, "offline:");
        StringAssert.StartsWith(secondId, "offline:");
        Assert.AreNotEqual(firstId, secondId);
        first.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => first.Identify(embeddings));
    }

    [TestMethod]
    public void TooShortToEmbedRemainsUnknownInsteadOfInventingSpeakerIdentity()
    {
        using var identities = new OfflineSpeakerIdentityMap();
        var first = identities.Identify(new Dictionary<int, float[]?> { [0] = null, [1] = null });
        var second = identities.Identify(new Dictionary<int, float[]?> { [0] = null });
        Assert.IsNull(first[0]);
        Assert.IsNull(first[1]);
        Assert.IsNull(second[0]);
    }

    [TestMethod]
    public void InvalidEmbeddingsAreNotUsedToInventSpeakerIdentity()
    {
        using var identities = new OfflineSpeakerIdentityMap();
        Assert.ThrowsExactly<InvalidDataException>(() =>
            identities.Identify(new Dictionary<int, float[]?> { [0] = [0, 0] }));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            identities.Identify(new Dictionary<int, float[]?> { [0] = [float.NaN, 1] }));
    }

    [TestMethod]
    public void SpeakerMemoryIsBoundedAndTheLimitHasAnExplicitRecoveryAction()
    {
        using var identities = new OfflineSpeakerIdentityMap();
        for (var i = 0; i < 64; i++)
        {
            var embedding = new float[65];
            embedding[i] = 1;
            identities.Identify(new Dictionary<int, float[]?> { [0] = embedding });
        }
        var next = new float[65];
        next[64] = 1;
        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            identities.Identify(new Dictionary<int, float[]?> { [0] = next }));
        StringAssert.Contains(error.Message, "Pause and resume");
    }

    private static SpeechAudioWindow Window() => new(
        Enumerable.Repeat(.1f, 32000).ToArray(), Start, Start.AddSeconds(2), true);

    private static async Task<List<SpeechText>> Collect(IAsyncEnumerable<SpeechText> source)
    {
        var result = new List<SpeechText>();
        await foreach (var item in source) result.Add(item);
        return result;
    }

    private sealed class FakeDiarizer(params OfflineSpeakerTurn[] turns) : IOfflineSpeakerDiarizer
    {
        public int Calls { get; private set; }
        public Action? DuringAnalyze { get; init; }
        public IReadOnlyList<OfflineSpeakerTurn> Analyze(float[] samples, CancellationToken cancellationToken)
        {
            Calls++;
            DuringAnalyze?.Invoke();
            return turns;
        }
        public void Dispose() { }
    }
}
