using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Transcript;
using ConversationAssistant.Core.WorkIQ;
using ConversationAssistant_App.Speech;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class SpeakerTurnTests
{
    [TestMethod]
    public void AdjacentVoicesAreSeparateEvenWithoutSilenceOrPunctuation()
    {
        var engine = new TranscriptEngine();
        var start = DateTimeOffset.UnixEpoch;
        var first = engine.CommitFinal(new("first voice", start, start.AddSeconds(1), "run:alpha"));
        var second = engine.CommitFinal(new("second voice", first.TimestampEnd, start.AddSeconds(2), "run:beta"));
        var third = engine.CommitFinal(new("first voice again", second.TimestampEnd, start.AddSeconds(3), "run:alpha"));
        Assert.HasCount(3, engine.Segments);
        Assert.AreEqual(1, first.SpeakerNumber);
        Assert.AreEqual(2, second.SpeakerNumber);
        Assert.AreEqual(1, third.SpeakerNumber);
        Assert.AreEqual(first.TimestampEnd, second.TimestampStart);
        Assert.AreEqual(second.TimestampEnd, third.TimestampStart);
        Assert.AreEqual("second voice", second.Text);
        Assert.AreNotEqual(first.Id, third.Id);
    }

    [TestMethod]
    public void PartialSpeakerAttributionDoesNotCommitOrMergeTurns()
    {
        var engine = new TranscriptEngine();
        TranscriptSegment? partial = null;
        engine.Updated += segment => partial = segment;
        var start = DateTimeOffset.UnixEpoch;
        engine.ReceivePartial(new("hello", start, start.AddSeconds(1)));
        Assert.IsNotNull(partial);
        Assert.IsNull(partial.SpeakerNumber);
        engine.ReceivePartial(new("hello there", start, start.AddSeconds(2), "run:a"));
        Assert.AreEqual(1, partial!.SpeakerNumber);
        Assert.IsEmpty(engine.Segments);
        var first = engine.CommitFinal(new("Hello there.", start, start.AddSeconds(2), "run:a"));
        Assert.IsNull(partial);
        Assert.AreEqual(1, first.SpeakerNumber);
        engine.ReceivePartial(new("a reply", first.TimestampEnd, start.AddSeconds(3), "run:b"));
        Assert.AreEqual(2, partial!.SpeakerNumber);
        Assert.HasCount(1, engine.Segments);
        Assert.AreEqual("Hello there.", engine.Segments.Single().Text);
    }

    [TestMethod]
    public void ClearResetsAnonymousGroupsAndUnknownVoiceNeverBecomesASpeaker()
    {
        var engine = new TranscriptEngine();
        var time = DateTimeOffset.UnixEpoch;
        foreach (var unknown in new string?[] { null, "", " ", "Unknown", "unknown" })
            Assert.IsNull(engine.CommitFinal(new("Unattributed.", time, time, unknown)).SpeakerNumber);
        Assert.AreEqual(1, engine.CommitFinal(new("First.", time, time, "run:1")).SpeakerNumber);
        Assert.AreEqual(2, engine.CommitFinal(new("Second.", time, time, "run:2")).SpeakerNumber);
        engine.Clear();
        Assert.IsEmpty(engine.Segments);
        Assert.AreEqual(1, engine.CommitFinal(new("After clear.", time, time, "run:2")).SpeakerNumber);
    }

    [TestMethod]
    public void SpeakerLabelsReachBothContextPathsAndCountTowardTheManualContextLimit()
    {
        var engine = new TranscriptEngine();
        var now = DateTimeOffset.UnixEpoch.AddMinutes(1);
        var first = engine.CommitFinal(new("Alpha.", now.AddSeconds(-3), now.AddSeconds(-2), "run:a"));
        var second = engine.CommitFinal(new("Beta.", now.AddSeconds(-1), now, "run:b"));
        var buffer = new ConversationBuffer(TimeSpan.FromMinutes(1), 30);
        buffer.Add(first);
        buffer.Add(second);
        Assert.AreEqual("[Speaker 2] Beta.", buffer.GetRecentContext(now));
        var full = ConversationAnalysisPrompt.FormatTranscript(engine.Segments);
        StringAssert.Contains(full, "[Speaker 1] Alpha.");
        StringAssert.Contains(full, "[Speaker 2] Beta.");
        Assert.IsFalse(full.Contains("run:", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SpeakerLabelsTranslateWithoutInventingIdentity()
    {
        var english = UiText.For(ConversationLanguage.English);
        var chinese = UiText.For(ConversationLanguage.Chinese);
        Assert.AreEqual("Speaker 2", TranscriptSpeaker.Label(2, english));
        Assert.AreEqual("说话人 2", TranscriptSpeaker.Label(2, chinese));
        Assert.AreEqual("Speaker not identified", TranscriptSpeaker.Label(null, english));
        Assert.AreEqual("说话人未识别", TranscriptSpeaker.Label(null, chinese));
    }

    [TestMethod]
    [DataRow(ConversationLanguage.English, " Hello . ", "Hello.")]
    [DataRow(ConversationLanguage.Chinese, " 我們使用 DLP。 ", "我们使用 DLP。")]
    public void AzureMappingPreservesDiarizedOffsetsTextAndSessionScopedIdentity(
        ConversationLanguage language, string input, string expected)
    {
        var origin = DateTimeOffset.UnixEpoch;
        var first = AzureSpeechSession.CreateSpeechText(input, TimeSpan.FromSeconds(3).Ticks,
            TimeSpan.FromMilliseconds(1200), "Guest-1", language, origin, "session-a")!;
        var same = AzureSpeechSession.CreateSpeechText("Same voice", 0, TimeSpan.FromSeconds(1),
            "Guest-1", language, origin, "session-a")!;
        var reconnect = AzureSpeechSession.CreateSpeechText("Another connection", 0, TimeSpan.FromSeconds(1),
            "Guest-1", language, origin, "session-b")!;
        Assert.AreEqual(expected, first.Text);
        Assert.AreEqual(origin.AddSeconds(3), first.Start);
        Assert.AreEqual(origin.AddMilliseconds(4200), first.End);
        Assert.AreEqual(first.SpeakerId, same.SpeakerId);
        Assert.AreNotEqual(first.SpeakerId, reconnect.SpeakerId);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("Unknown")]
    [DataRow(" unknown ")]
    public void AzureUnknownSpeakerIsNotAssignedAFabricatedIdentity(string? speakerId)
    {
        var speech = AzureSpeechSession.CreateSpeechText("Unattributed words", 0, TimeSpan.FromSeconds(1),
            speakerId, ConversationLanguage.English, DateTimeOffset.UnixEpoch, "session");
        Assert.IsNotNull(speech);
        Assert.IsNull(speech.SpeakerId);
        Assert.IsNull(AzureSpeechSession.CreateSpeechText(" ", 0, TimeSpan.Zero, speakerId,
            ConversationLanguage.English, DateTimeOffset.UnixEpoch, "session"));
    }
}
