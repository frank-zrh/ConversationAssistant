using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.WorkIQ;

public interface IWorkIqTokenProvider
{
    ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken);
    void Invalidate();
}

/// <summary>Direct client for the documented Work IQ Streamable HTTP MCP service.</summary>
public sealed class WorkIqHttpClient(HttpClient http, IWorkIqTokenProvider identity) : IWorkIqClient
{
    public const string Endpoint = "https://workiq.svc.cloud.microsoft/mcp";
    public const string Scope = "fdcc1f02-fc51-4226-8753-f668596af7f7/WorkIQAgent.Ask";
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextId;
    private string? _sessionId;
    private string? _protocol;
    private string? _askTool;
    private bool _canContinue;
    private bool _canSetTimeZone;

    public void ResetSession()
    {
        _sessionId = null;
        _protocol = null;
        _askTool = null;
    }

    public async Task VerifyAccessAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await InitializeAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<WorkIqAnswer> AskAsync(string prompt, string? conversationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            if (conversationId is not null && !_canContinue)
                throw Failure("The Work IQ answering tool cannot continue conversations. No context was silently discarded.");
            var arguments = new Dictionary<string, object> { ["question"] = prompt };
            if (conversationId is not null) arguments["conversationId"] = conversationId;
            if (_canSetTimeZone) arguments["timeZone"] = "UTC";
            using var response = await SendAsync("tools/call", new { name = _askTool, arguments }, false, cancellationToken)
                .ConfigureAwait(false);
            var result = Result(response!);
            if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
                throw Failure("Work IQ could not answer this request. Check access and service availability; no automatic retry was made.");
            return ParseAnswer(result);
        }
        finally { _gate.Release(); }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_askTool is not null) return;
        try
        {
            using var initialized = await SendAsync("initialize", new
            {
                protocolVersion = "2025-03-26", capabilities = new { },
                clientInfo = new { name = "ConversationAssistant", version = "2.0.0" }
            }, false, cancellationToken).ConfigureAwait(false);
            var version = ReadString(Result(initialized!), "protocolVersion");
            if (version is not ("2025-03-26" or "2025-06-18" or "2025-11-25"))
                throw Failure("Work IQ returned an unsupported protocol version.");
            _protocol = version;
            using var notification = await SendAsync("notifications/initialized", new { }, true, cancellationToken)
                .ConfigureAwait(false);
            string? cursor = null;
            for (var page = 0; page < 10; page++)
            {
                using var catalog = await SendAsync("tools/list", cursor is null ? new { } : (object)new { cursor },
                    false, cancellationToken).ConfigureAwait(false);
                var result = Result(catalog!);
                if (!result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
                    throw InvalidResponse();
                foreach (var tool in tools.EnumerateArray())
                {
                    var name = ReadString(tool, "name");
                    if (name is null || !(name == "ask" || name.EndsWith("_ask", StringComparison.Ordinal) ||
                        name.EndsWith(".ask", StringComparison.Ordinal))) continue;
                    if (tool.TryGetProperty("annotations", out var annotations) &&
                        IsBoolean(annotations, "destructiveHint", true)) continue;
                    if (!tool.TryGetProperty("inputSchema", out var schema) ||
                        !schema.TryGetProperty("properties", out var properties) ||
                        !IsStringProperty(properties, "question")) continue;
                    if (schema.TryGetProperty("required", out var required) &&
                        (required.ValueKind != JsonValueKind.Array || required.EnumerateArray().Any(item =>
                            item.ValueKind != JsonValueKind.String || item.GetString() is not ("question" or "conversationId" or "timeZone")))) continue;
                    // A new conversation must not require an identifier we do not possess.
                    if (schema.TryGetProperty("required", out required) &&
                        required.EnumerateArray().Any(item => item.GetString() == "conversationId")) continue;
                    _canContinue = IsStringProperty(properties, "conversationId");
                    _canSetTimeZone = IsStringProperty(properties, "timeZone");
                    _askTool = name;
                    return;
                }
                cursor = ReadString(result, "nextCursor");
                if (string.IsNullOrEmpty(cursor)) break;
            }
            throw Failure("Microsoft authentication completed, but the hosted Work IQ tool catalog does not expose a compatible Ask schema. " +
                "This is a Work IQ capability check, not a sign-in failure. Check service compatibility, Work IQ availability, license and administrator consent.");
        }
        catch { ResetSession(); throw; }
    }

    private async Task<JsonDocument?> SendAsync(string method, object parameters, bool notification, CancellationToken callerToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        try
        {
            var accessToken = await identity.GetAccessTokenAsync(token).ConfigureAwait(false);
            var id = Interlocked.Increment(ref _nextId);
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (_sessionId is not null) request.Headers.Add("Mcp-Session-Id", _sessionId);
            if (_protocol is not null) request.Headers.Add("MCP-Protocol-Version", _protocol);
            request.Content = JsonContent.Create(notification
                ? new { jsonrpc = "2.0", method, @params = parameters }
                : (object)new { jsonrpc = "2.0", id, method, @params = parameters });
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized) identity.Invalidate();
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound) ResetSession();
                throw Failure(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Work IQ authentication expired. Sign in with Microsoft again.",
                    HttpStatusCode.Forbidden => "Work IQ access denied. Check the Copilot license, tenant administrator consent and organizational policies.",
                    HttpStatusCode.NotFound => "The Work IQ HTTP session or service is unavailable. Sign in again to reset context.",
                    HttpStatusCode.TooManyRequests => "Work IQ is throttling requests. Wait before retrying; no request was automatically resubmitted.",
                    _ => $"Work IQ HTTP request failed (HTTP {(int)response.StatusCode})."
                });
            }
            if (method == "initialize" && response.Headers.TryGetValues("Mcp-Session-Id", out var values))
            {
                var session = values.SingleOrDefault();
                if (session is null || session.Length > 1024 || session.Any(character => character < 33 || character > 126))
                    throw InvalidResponse();
                _sessionId = session;
            }
            if (notification) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                return await ReadEventStreamAsync(stream, id, token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > MaximumResponseBytes) throw InvalidResponse();
                buffer.Write(chunk, 0, read);
            }
            var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            try { ValidateEnvelope(document, id); return document; }
            catch { document.Dispose(); throw; }
        }
        catch (JsonException) { throw InvalidResponse(); }
        catch (HttpRequestException) { throw Failure("Work IQ could not be reached. Check the network and proxy settings."); }
        catch (IOException) { throw Failure("The Work IQ response could not be read. No request was automatically resubmitted."); }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        { throw Failure("Work IQ timed out. No request was automatically resubmitted."); }
    }

    private static async Task<JsonDocument> ReadEventStreamAsync(Stream stream, long id, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        var bytes = new char[2048];
        var line = new StringBuilder();
        var total = 0;
        int read;
        while ((read = await reader.ReadAsync(bytes.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaximumResponseBytes) throw InvalidResponse();
            for (var index = 0; index < read; index++)
            {
                var character = bytes[index];
                if (character != '\n') { if (character != '\r') line.Append(character); continue; }
                var value = line.ToString();
                line.Clear();
                if (value.StartsWith("data:", StringComparison.Ordinal)) data.AppendLine(value[5..].TrimStart(' '));
                else if (value.Length == 0 && data.Length > 0)
                {
                    var document = JsonDocument.Parse(data.ToString());
                    data.Clear();
                    if (document.RootElement.TryGetProperty("id", out _))
                    {
                        try { ValidateEnvelope(document, id); return document; }
                        catch { document.Dispose(); throw; }
                    }
                    document.Dispose();
                }
            }
        }
        throw InvalidResponse();
    }

    private static WorkIqAnswer ParseAnswer(JsonElement result)
    {
        if (result.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object)
            return ReadAnswerObject(structured);
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw InvalidResponse();
        var text = string.Join("\n", content.EnumerateArray().Where(item => ReadString(item, "type") == "text")
            .Select(item => ReadString(item, "text")).Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(text)) throw InvalidResponse();
        if (text.TrimStart().StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    (ReadString(root, "response") is not null || ReadString(root, "answer") is not null || ReadString(root, "text") is not null))
                    return ReadAnswerObject(root);
            }
            catch (JsonException) { }
        }
        return new WorkIqAnswer(text, [], null);
    }

    private static WorkIqAnswer ReadAnswerObject(JsonElement result)
    {
        var text = ReadString(result, "response") ?? ReadString(result, "answer") ?? ReadString(result, "text");
        if (string.IsNullOrWhiteSpace(text)) throw InvalidResponse();
        var sources = new List<string>();
        foreach (var field in new[] { "sources", "citations", "attributions" })
            if (result.TryGetProperty(field, out var references) && references.ValueKind == JsonValueKind.Array)
                foreach (var reference in references.EnumerateArray())
                {
                    var value = reference.ValueKind == JsonValueKind.String ? reference.GetString() :
                        ReadString(reference, "url") ?? ReadString(reference, "seeMoreWebUrl");
                    if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo))
                        sources.Add(uri.AbsoluteUri);
                }
        return new WorkIqAnswer(text, sources.Distinct(StringComparer.Ordinal).ToArray(), ReadString(result, "conversationId"));
    }

    private static void ValidateEnvelope(JsonDocument document, long expectedId)
    {
        var root = document.RootElement;
        if (ReadString(root, "jsonrpc") != "2.0" || !root.TryGetProperty("id", out var id) ||
            !id.TryGetInt64(out var number) || number != expectedId) throw InvalidResponse();
        if (root.TryGetProperty("error", out _))
            throw Failure("Work IQ rejected the protocol request. Check service compatibility and permissions; no automatic retry was made.");
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) throw InvalidResponse();
    }

    private static JsonElement Result(JsonDocument document) => document.RootElement.GetProperty("result");
    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    private static bool IsStringProperty(JsonElement properties, string name) =>
        properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var value) && IsStringSchema(value);
    private static bool IsStringSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return false;
        if (schema.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.String) return type.GetString() == "string";
            if (type.ValueKind == JsonValueKind.Array)
                return type.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "string") &&
                    type.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && item.GetString() is "string" or "null");
        }
        foreach (var keyword in new[] { "anyOf", "oneOf" })
            if (schema.TryGetProperty(keyword, out var alternatives) && alternatives.ValueKind == JsonValueKind.Array)
                return alternatives.EnumerateArray().Any(IsStringSchema) && alternatives.EnumerateArray().All(item =>
                    IsStringSchema(item) || ReadString(item, "type") == "null");
        return false;
    }
    private static bool IsBoolean(JsonElement element, string property, bool expected) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);
    private static WorkIqException Failure(string message) => new(message);
    private static WorkIqException InvalidResponse() => Failure("Work IQ returned an invalid or unsupported HTTP response. No partial result was saved.");
}
