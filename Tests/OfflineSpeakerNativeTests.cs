using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Speech;
using ConversationAssistant_App.Speech;
using Whisper.net;

namespace ConversationAssistant.Tests;

[TestClass]
[TestCategory("OfflineSpeakerNative")]
[DoNotParallelize]
[SupportedOSPlatform("windows")]
public sealed class OfflineSpeakerNativeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void NativeModelRejectsSilenceWithoutInventingSpeakers()
    {
        using var diarizer = new OfflineSpeakerDiarizer(Models());
        Assert.IsEmpty(diarizer.Analyze(new float[12 * 16000], CancellationToken.None));
    }

    [TestMethod]
    [DataRow("en")]
    [DataRow("zh")]
    public void NativeModelsSeparateSyntheticVoicesAndKeepSessionIdentity(string language)
    {
        var models = Models();
        var (first, second) = SyntheticVoices(language);
        var window = TwelveSeconds(first, second, first);
        using var diarizer = new OfflineSpeakerDiarizer(models);
        var timer = Stopwatch.StartNew();
        var turns = diarizer.Analyze(window, CancellationToken.None);
        var firstId = SpeakerAt(turns, first.Length / 2);
        var secondId = SpeakerAt(turns, first.Length + second.Length / 2);
        var returningId = SpeakerAt(turns, first.Length + second.Length + first.Length / 2);
        Assert.AreNotEqual(firstId, secondId, "Two installed synthetic voices must not become one speaker.");
        Assert.AreEqual(firstId, returningId, "A returning voice must retain its anonymous identity.");

        var reordered = diarizer.Analyze(TwelveSeconds(second, first), CancellationToken.None);
        Assert.AreEqual(secondId, SpeakerAt(reordered, second.Length / 2));
        Assert.AreEqual(firstId, SpeakerAt(reordered, second.Length + first.Length / 2));

        var (changedFirst, changedSecond) = SyntheticVoices(language, alternateText: true);
        var differentWords = diarizer.Analyze(TwelveSeconds(changedSecond, changedFirst), CancellationToken.None);
        Assert.AreEqual(secondId, SpeakerAt(differentWords, changedSecond.Length / 2));
        Assert.AreEqual(firstId, SpeakerAt(differentWords, changedSecond.Length + changedFirst.Length / 2));
        TestContext.WriteLine($"Native {language} separation: {turns.Count} initial turns; three windows in {timer.Elapsed.TotalSeconds:F2}s.");
    }

    [TestMethod]
    [DataRow("en")]
    [DataRow("zh")]
    public async Task NativeWhisperProducesSeparateFinalTextForDifferentSyntheticVoices(string language)
    {
        var models = Models();
        var model = Environment.GetEnvironmentVariable("CONVERSATIONASSISTANT_WHISPER_MODEL");
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(model))
            Assert.Inconclusive("Set CONVERSATIONASSISTANT_WHISPER_MODEL to the provisioned ggml-small.bin for local inference.");
        var (first, second) = SyntheticVoices(language);
        using var diarizer = new OfflineSpeakerDiarizer(models);
        using var whisper = WhisperFactory.FromPath(model);
        using var processor = whisper.CreateBuilder().WithLanguage(language).WithNoContext()
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8)).Build();
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var result = new List<SpeechText>();
        var timer = Stopwatch.StartNew();
        await foreach (var text in OfflineSpeakerTurnTranscriber.TranscribeAsync(
            new SpeechAudioWindow(TwelveSeconds(first, second, first), start, start.AddSeconds(12), true),
            diarizer, (audio, token) => OfflineWhisperSpeechRecognitionService.TranscribeWindowAsync(
                processor, audio, language == "zh" ? ConversationLanguage.Chinese : ConversationLanguage.English, token), stop.Token))
            result.Add(text);
        Assert.IsGreaterThanOrEqualTo(2, result.Where(text => text.SpeakerId is not null)
            .Select(text => text.SpeakerId).Distinct().Count(),
            "Local Whisper must publish text separately for at least two acoustically identified voices.");
        Assert.IsTrue(result.All(text => text.Start >= start && text.End <= start.AddSeconds(12) && text.End > text.Start));
        Assert.IsTrue(result.All(text => !string.IsNullOrWhiteSpace(text.Text)));
        TestContext.WriteLine($"Native {language} Whisper: {result.Count} separate finals in {timer.Elapsed.TotalSeconds:F2}s. Audio and text were not saved.");
    }

    private static OfflineSpeakerModelPaths Models()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            Assert.Inconclusive("Native offline speaker validation requires Windows x64.");
        var directory = Environment.GetEnvironmentVariable("CONVERSATIONASSISTANT_SPEAKER_MODELS");
        if (string.IsNullOrWhiteSpace(directory))
            Assert.Inconclusive("Explicitly provision speaker models and set CONVERSATIONASSISTANT_SPEAKER_MODELS. Tests never download models.");
        return new OfflineSpeakerModelManager(directory).Validate();
    }

    private (float[] First, float[] Second) SyntheticVoices(string language, bool alternateText = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var synthesizer = new SpeechSynthesizer();
        var voices = synthesizer.GetInstalledVoices().Where(voice => voice.Enabled &&
            voice.VoiceInfo.Culture.TwoLetterISOLanguageName == language).Select(voice => voice.VoiceInfo)
            // Desktop and OneCore registrations can refer to the same synthetic voice.
            .GroupBy(voice => voice.Name.EndsWith(" Desktop", StringComparison.OrdinalIgnoreCase)
                ? voice.Name[..^8] : voice.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
        TestContext.WriteLine("Installed synthetic voices: " +
            string.Join(", ", voices.Select(voice => $"{voice.Name} ({voice.Culture.Name}, {voice.Gender})")));
        if (voices.Length < 2)
            Assert.Inconclusive($"Install two {language} Windows speech synthesis voices to validate acoustic separation without recording anyone.");
        var first = voices[0];
        var second = voices.FirstOrDefault(voice => voice.Gender != first.Gender) ?? voices[1];
        var text = (language, alternateText) switch
        {
            ("zh", true) => "安排已经确认了，我们继续讨论项目预算。",
            ("zh", false) => "我们先讨论预算，然后确认日程。",
            (_, true) => "The next meeting is tomorrow, and we can discuss the budget together.",
            _ => "Let us confirm the plans and finish the review today."
        };
        Assert.AreNotEqual(first.Name, second.Name);
        return (Synthesize(first.Name, text), Synthesize(second.Name, text));
    }

    private static float[] Synthesize(string voice, string text)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var memory = new MemoryStream();
        using (var synthesizer = new SpeechSynthesizer())
        {
            synthesizer.SelectVoice(voice);
            synthesizer.Rate = 2;
            synthesizer.SetOutputToAudioStream(memory,
                new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synthesizer.Speak(text);
            synthesizer.SetOutputToNull();
        }
        var pcm = memory.ToArray();
        var frames = MemoryMarshal.Cast<byte, short>(pcm);
        var samples = new float[frames.Length];
        for (var i = 0; i < frames.Length; i++) samples[i] = frames[i] / 32768f;
        var start = Array.FindIndex(samples, value => Math.Abs(value) > .002f);
        var end = Array.FindLastIndex(samples, value => Math.Abs(value) > .002f);
        Assert.IsTrue(start >= 0 && end > start, "Installed synthesis voice returned no audio.");
        return samples[Math.Max(0, start - 160)..Math.Min(samples.Length, end + 161)];
    }

    private static float[] TwelveSeconds(params float[][] turns)
    {
        var output = new float[12 * 16000];
        var offset = 0;
        foreach (var turn in turns)
        {
            Assert.IsLessThanOrEqualTo(output.Length, offset + turn.Length, "Synthetic turns must fit in the 12-second inference window.");
            turn.CopyTo(output, offset);
            offset += turn.Length;
        }
        while (offset < output.Length)
        {
            var count = Math.Min(turns[^1].Length, output.Length - offset);
            turns[^1].AsSpan(0, count).CopyTo(output.AsSpan(offset));
            offset += count;
        }
        return output;
    }

    private static string SpeakerAt(IReadOnlyList<OfflineSpeakerTurn> turns, int sample)
    {
        var turn = turns.FirstOrDefault(turn => turn.StartSample <= sample && turn.EndSample > sample);
        Assert.IsNotNull(turn, "Expected acoustic speech at the middle of a synthetic utterance.");
        Assert.IsNotNull(turn.SpeakerId, "The synthetic fixture must contain enough speech for an acoustic identity.");
        return turn.SpeakerId;
    }
}
