using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ConversationAssistant.Core.WorkIQ;

public sealed record WorkIqLogContext(string? Path, Guid SessionId,
    Guid? RequestId = null, string? RequestKind = null, Guid? CallId = null);

/// <summary>Local, session-bound diagnostics. File failures never fail a Work IQ request.</summary>
public sealed partial class WorkIqDiagnostics
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly AsyncLocal<WorkIqLogContext?> _scope = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _reportedFailures = new(StringComparer.OrdinalIgnoreCase);
    private WorkIqLogContext? _current;
    private Task _initialization = Task.CompletedTask;

    public event Action<Exception>? Failed;
    public WorkIqLogContext? Capture() => _scope.Value ?? Volatile.Read(ref _current);
    public static string GetPath(string recordPath) => System.IO.Path.ChangeExtension(recordPath, ".workiq.log");

    public void BindConversation(Guid sessionId, string? recordPath)
    {
        var context = new WorkIqLogContext(recordPath is null ? null : GetPath(recordPath), sessionId);
        Volatile.Write(ref _current, context);
        _initialization = WriteAsync(context, "SessionOpened", new
        {
            Format = "JSON Lines; UTC timestamps; untrusted request/response content",
            Privacy = "Contains conversation and workplace data. Common credential formats are redacted. Protect this local file."
        });
    }

    public IDisposable BeginRequest(Guid sessionId, Guid requestId, string kind)
    {
        var current = Volatile.Read(ref _current);
        return Push(new WorkIqLogContext(current?.SessionId == sessionId ? current.Path : null,
            sessionId, requestId, kind));
    }

    public IDisposable BeginCall()
    {
        var context = Capture();
        return Push(context is null ? null : context with { CallId = Guid.NewGuid() });
    }

    private IDisposable Push(WorkIqLogContext? context)
    {
        var previous = _scope.Value;
        _scope.Value = context;
        return new Scope(() => _scope.Value = previous);
    }

    public Task WriteAsync(WorkIqLogContext? context, string eventName, object details)
    {
        if (context?.Path is null) return Task.CompletedTask;
        var timestamp = DateTimeOffset.UtcNow;
        // Serialization, redaction, directory creation and append all run away from UI/capture callers.
        return Task.Run(async () =>
        {
            try
            {
                var data = JsonSerializer.SerializeToNode(details, JsonOptions);
                Sanitize(data);
                var line = JsonSerializer.Serialize(new
                {
                    Timestamp = timestamp, Event = eventName, context.SessionId,
                    context.RequestId, context.RequestKind, context.CallId, Data = data
                }, JsonOptions);
                await _writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(context.Path)!);
                    await using var stream = new FileStream(context.Path, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                    await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }
                finally { _writeGate.Release(); }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                JsonException or ArgumentException or RegexMatchTimeoutException or NotSupportedException)
            {
                if (_reportedFailures.TryAdd(context.Path, 0)) Failed?.Invoke(error);
            }
        });
    }

    public async Task FlushAsync()
    {
        await _initialization.ConfigureAwait(false);
        await _writeGate.WaitAsync().ConfigureAwait(false);
        _writeGate.Release();
    }

    private static void Sanitize(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
            {
                if (SecretName().IsMatch(key)) obj[key] = "[REDACTED]";
                else if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                    obj[key] = Redact(text);
                else Sanitize(obj[key]);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
                if (array[index] is JsonValue value && value.TryGetValue<string>(out var text))
                    array[index] = Redact(text);
                else Sanitize(array[index]);
        }
    }

    public static string Redact(string text)
    {
        text = Bearer().Replace(text, "$1[REDACTED]");
        text = Credentials().Replace(text, "$1[REDACTED]");
        return Jwt().Replace(text, "[REDACTED]");
    }

    [GeneratedRegex(@"^(?:access[_-]?token|refresh[_-]?token|id[_-]?token|api[_-]?key|client[_-]?secret|password|authorization|secret|sig)$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();
    [GeneratedRegex(@"\b(Bearer\s+)[A-Za-z0-9._~+/-]+=*", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Bearer();
    [GeneratedRegex("""(["']?(?:access[_-]?token|refresh[_-]?token|id[_-]?token|api[_-]?key|client[_-]?secret|password|authorization|secret|sig)["']?\s*[:=]\s*)(?:"[^"]*"|'[^']*'|[^\s,;}\]]+)""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex Credentials();
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b", RegexOptions.None, 1000)]
    private static partial Regex Jwt();

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
