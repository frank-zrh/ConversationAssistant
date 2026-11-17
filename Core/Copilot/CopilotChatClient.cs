using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Copilot;

public interface ICopilotTokenProvider
{
    ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken);
    void Invalidate();
}

public sealed class CopilotChatClient(HttpClient http, ICopilotTokenProvider identity) : IWorkIqClient
{
    public const string ConversationsEndpoint = "https://graph.microsoft.com/beta/copilot/conversations";
    private const int MaximumResponseBytes = 4 * 1024 * 1024;

    public async Task VerifyAccessAsync(CancellationToken cancellationToken) =>
        _ = await CreateConversationAsync(cancellationToken).ConfigureAwait(false);

    public async Task<WorkIqAnswer> AskAsync(string prompt, string? conversationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var id = conversationId is null
            ? await CreateConversationAsync(cancellationToken).ConfigureAwait(false)
            : ValidateId(conversationId);
        using var response = await PostAsync(ConversationsEndpoint + "/" + Uri.EscapeDataString(id) + "/chat", new
        {
            message = new { text = prompt },
            locationHint = new { timeZone = "Etc/UTC" },
            contextualResources = new { webContext = new { isWebEnabled = false } }
        }, cancellationToken).ConfigureAwait(false);
        var root = response.RootElement;
        if (ReadString(root, "id") != id || !root.TryGetProperty("messages", out var messages) ||
            messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() < 2)
            throw InvalidResponse();
        // The documented collection includes the submitted prompt followed by the response.
        // Both have the same OData type; do not treat the echoed prompt as an answer.
        var message = messages[messages.GetArrayLength() - 1];
        if (message.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        var text = ReadString(message, "text");
        if (string.IsNullOrWhiteSpace(text) || text == prompt) throw InvalidResponse();
        var sources = new List<string>();
        if (message.TryGetProperty("attributions", out var attributions) && attributions.ValueKind == JsonValueKind.Array)
            foreach (var attribution in attributions.EnumerateArray())
                if (Uri.TryCreate(ReadString(attribution, "seeMoreWebUrl"), UriKind.Absolute, out var uri) &&
                    uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo))
                    sources.Add(uri.AbsoluteUri);
        cancellationToken.ThrowIfCancellationRequested();
        return new WorkIqAnswer(text, sources.Distinct(StringComparer.Ordinal).ToArray(), id);
    }

    private async Task<string> CreateConversationAsync(CancellationToken cancellationToken)
    {
        using var response = await PostAsync(ConversationsEndpoint, new { }, cancellationToken).ConfigureAwait(false);
        return ValidateId(ReadString(response.RootElement, "id"));
    }

    private async Task<JsonDocument> PostAsync(string endpoint, object body, CancellationToken cancellationToken)
    {
        var callerToken = cancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        cancellationToken = timeout.Token;
        var token = await identity.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(body);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized) identity.Invalidate();
                // Never include server bodies, headers, token values or raw transport errors in diagnostics/archives.
                throw new WorkIqException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Copilot authentication expired. Sign in again with Microsoft.",
                    HttpStatusCode.Forbidden => "Copilot access denied. Check the Microsoft 365 Copilot add-on license, all seven delegated Graph permissions, administrator consent and tenant policies.",
                    HttpStatusCode.NotFound => "Copilot conversation or preview API is unavailable. Sign in again to reset remote context; verify global-cloud availability.",
                    HttpStatusCode.TooManyRequests => "Copilot is throttling requests (HTTP 429). Wait before manually retrying; no request was automatically resubmitted.",
                    _ => $"Copilot Chat request failed (HTTP {(int)response.StatusCode}). Retry later."
                });
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > MaximumResponseBytes) throw InvalidResponse();
                buffer.Write(bytes, 0, read);
            }
            return JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
        }
        catch (JsonException) { throw InvalidResponse(); }
        catch (HttpRequestException) { throw new WorkIqException("Copilot Chat could not be reached. Check the network and try again."); }
        catch (IOException) { throw new WorkIqException("Copilot Chat response could not be read. Check the network and try again."); }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new WorkIqException("Copilot Chat request timed out. No request was automatically resubmitted.");
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string ValidateId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 512 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? id : throw InvalidResponse();

    private static WorkIqException InvalidResponse() => new("Copilot Chat returned an invalid or empty response. No partial result was saved.");
}
