using System.Net.WebSockets;
using System.Text.Json;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Speech;

namespace ConversationAssistant.Core.Qwen;

internal sealed class QwenSpeechProtocol(string model, ConversationLanguage language)
{
    private const int SampleRate = 16000;
    private readonly string _taskId = Guid.NewGuid().ToString("D");
    private readonly Dictionary<string, (long? Start, long? End)> _vad = new(StringComparer.Ordinal);
    private readonly string? _language = language switch
    {
        ConversationLanguage.Auto => null,
        ConversationLanguage.Chinese => "zh",
        ConversationLanguage.English => "en",
        _ => throw new ArgumentOutOfRangeException(nameof(language))
    };

    internal bool IsRealtime => model == QwenModels.DefaultSpeech;
    internal Uri Endpoint { get; } = model switch
    {
        QwenModels.DefaultSpeech => QwenPayAsYouGoEndpoints.SpeechRealtime,
        QwenModels.AlternateSpeech => QwenPayAsYouGoEndpoints.SpeechInference,
        _ => throw new SpeechConnectionException("Select a supported Qwen speech model in settings.")
    };

    internal byte[] SetupMessage()
    {
        if (IsRealtime)
        {
            var session = new Dictionary<string, object>
            {
                ["modalities"] = new[] { "text" },
                ["input_audio_format"] = "pcm",
                ["sample_rate"] = SampleRate,
                ["turn_detection"] = new { type = "server_vad" }
            };
            if (_language is not null) session["input_audio_transcription"] = new { language = _language };
            return JsonSerializer.SerializeToUtf8Bytes(new { event_id = NewEventId(), type = "session.update", session });
        }

        var parameters = new Dictionary<string, object>
        {
            ["format"] = "pcm", ["sample_rate"] = SampleRate, ["heartbeat"] = true
        };
        if (_language is not null) parameters["language_hints"] = new[] { _language };
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            header = Header("run-task"),
            payload = new { task_group = "audio", task = "asr", function = "recognition", model, parameters, input = new { } }
        });
    }

    internal ValueTask SendAudioAsync(IQwenSpeechSocket socket, ReadOnlyMemory<byte> pcm, CancellationToken token) =>
        IsRealtime
            ? socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
                { event_id = NewEventId(), type = "input_audio_buffer.append", audio = Convert.ToBase64String(pcm.Span) }),
                WebSocketMessageType.Text, token)
            : socket.SendAsync(pcm, WebSocketMessageType.Binary, token);

    internal byte[] FinishMessage() => IsRealtime
        ? JsonSerializer.SerializeToUtf8Bytes(new { event_id = NewEventId(), type = "session.finish" })
        : JsonSerializer.SerializeToUtf8Bytes(new { header = Header("finish-task"), payload = new { input = new { } } });

    private object Header(string action) => new { action, task_id = _taskId, streaming = "duplex" };
    private static string NewEventId() => "event_" + Guid.NewGuid().ToString("N");

    internal Event Parse(JsonElement message) => IsRealtime ? ParseRealtime(message) : ParseInference(message);

    private Event ParseRealtime(JsonElement message)
    {
        var item = String(message, "item_id");
        var hasItem = !string.IsNullOrEmpty(item);
        var timing = hasItem ? _vad.GetValueOrDefault(item!) : default;
        switch (String(message, "type"))
        {
            case "session.updated": return new(EventKind.Ready);
            case "session.finished": return new(EventKind.Finished);
            case "error":
            case "conversation.item.input_audio_transcription.failed": return new(EventKind.Failed);
            case "input_audio_buffer.speech_started" when hasItem:
            case "input_audio_buffer.speech_stopped" when hasItem:
                // Realtime ASR supplies VAD boundaries, not word or sentence timestamps.
                if (_vad.Count >= 64 && !_vad.ContainsKey(item!)) _vad.Clear();
                _vad[item!] = (Milliseconds(message, "audio_start_ms") ?? timing.Start,
                    Milliseconds(message, "audio_end_ms") ?? timing.End);
                return default;
            case "conversation.item.input_audio_transcription.text":
                return new(EventKind.Partial, String(message, "text") + String(message, "stash"), timing.Start, timing.End);
            case "conversation.item.input_audio_transcription.completed":
                if (hasItem) _vad.Remove(item!);
                return new(EventKind.Final, String(message, "transcript"), timing.Start, timing.End);
            default: return default;
        }
    }

    private Event ParseInference(JsonElement message)
    {
        var header = Property(message, "header");
        if (!string.Equals(String(header, "task_id"), _taskId, StringComparison.Ordinal)) return default;
        switch (String(header, "event"))
        {
            case "task-started": return new(EventKind.Ready);
            case "task-finished": return new(EventKind.Finished);
            case "task-failed": return new(EventKind.Failed);
            case "result-generated":
                var sentence = Property(Property(Property(message, "payload"), "output"), "sentence");
                if (Property(sentence, "heartbeat").ValueKind == JsonValueKind.True) return default;
                var final = Property(sentence, "sentence_end").ValueKind == JsonValueKind.True;
                var end = Milliseconds(sentence, "end_time");
                if (end is null && Property(sentence, "words") is { ValueKind: JsonValueKind.Array } words)
                {
                    foreach (var word in words.EnumerateArray())
                        if (Milliseconds(word, "end_time") is long wordEnd && (end is null || wordEnd > end)) end = wordEnd;
                }
                return new(final ? EventKind.Final : EventKind.Partial, String(sentence, "text"),
                    Milliseconds(sentence, "begin_time"), end);
            default: return default;
        }
    }

    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;

    private static string? String(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.String } property ? property.GetString() : null;

    private static long? Milliseconds(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.Number } property &&
        property.TryGetInt64(out var number) && number >= 0 ? number : null;

    internal enum EventKind { Ignored, Ready, Partial, Final, Finished, Failed }
    internal readonly record struct Event(EventKind Kind, string? Text = null, long? StartMs = null, long? EndMs = null);
}