using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Speech;

namespace ConversationAssistant.Core.Qwen;

internal sealed class QwenTokenPlanSpeechClient(HttpClient http, TimeProvider clock)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private const string Unavailable = "Could not reach Token Plan transcription. Check the network and retry.";
    private const string UnexpectedResponse = "Token Plan returned an unexpected transcription response.";

    internal async Task<string?> TranscribeAsync(string model, string key, ConversationLanguage language,
        ReadOnlyMemory<byte> pcm, CancellationToken token)
    {
        var parameters = new Dictionary<string, object> { ["format"] = "wav", ["sample_rate"] = "16000" };
        if (language != ConversationLanguage.Auto)
            parameters["language_hints"] = new[] { language == ConversationLanguage.Chinese ? "zh" : "en" };
        var inputAudio = new { type = "input_audio", input_audio = new { data = ToWavDataUri(pcm.Span) } };
        using var request = new HttpRequestMessage(HttpMethod.Post, QwenTokenPlanEndpoints.SpeechRecognition)
        {
            Content = JsonContent.Create(new
            {
                model,
                input = new { messages = new[] { new { role = "user", content = new[] { inputAudio } } } },
                parameters
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var timeout = new CancellationTokenSource(RequestTimeout, clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, token);
        try
        {
            using var response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new SpeechConnectionException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound =>
                        "Token Plan rejected the transcription request. Check the subscription, API key, and speech model access.",
                    HttpStatusCode.TooManyRequests =>
                        "Token Plan transcription is rate limited or out of Credits. Check your subscription usage before retrying.",
                    HttpStatusCode.BadRequest => "Token Plan rejected the audio or speech model. Check the model and retry.",
                    _ => Unavailable
                });
            using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(deadline.Token).ConfigureAwait(false);
            if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
                !root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object)
                throw new SpeechConnectionException(UnexpectedResponse);
            if (output.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                return text.GetString();
            if (output.TryGetProperty("sentence", out var sentence) && sentence.ValueKind == JsonValueKind.Object &&
                sentence.TryGetProperty("text", out text) && text.ValueKind == JsonValueKind.String)
                return text.GetString();
            throw new SpeechConnectionException(UnexpectedResponse);
        }
        catch (JsonException) { throw new SpeechConnectionException(UnexpectedResponse); }
        catch (Exception error) when (error is HttpRequestException or IOException ||
            error is OperationCanceledException && !token.IsCancellationRequested)
        {
            throw new SpeechConnectionException(Unavailable);
        }
    }

    private static string ToWavDataUri(ReadOnlySpan<byte> pcm)
    {
        var wav = new byte[44 + pcm.Length];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), pcm.Length);
        pcm.CopyTo(wav.AsSpan(44));
        return "data:audio/wav;base64," + Convert.ToBase64String(wav);
    }
}