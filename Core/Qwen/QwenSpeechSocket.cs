using System.Net.WebSockets;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ConversationAssistant.Tests")]

namespace ConversationAssistant.Core.Qwen;

internal interface IQwenSpeechSocket : IDisposable
{
    Task ConnectAsync(Uri endpoint, string apiKey, bool realtime, CancellationToken token);
    ValueTask SendAsync(ReadOnlyMemory<byte> message, WebSocketMessageType type, CancellationToken token);
    ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken token);
    Task CloseOutputAsync(CancellationToken token);
}

internal sealed class QwenSpeechSocket : IQwenSpeechSocket
{
    private readonly ClientWebSocket _socket = new();

    public Task ConnectAsync(Uri endpoint, string apiKey, bool realtime, CancellationToken token)
    {
        _socket.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        if (realtime) _socket.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");
        return _socket.ConnectAsync(endpoint, token);
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, WebSocketMessageType type, CancellationToken token) =>
        _socket.SendAsync(message, type, endOfMessage: true, token);

    public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken token) =>
        _socket.ReceiveAsync(buffer, token);

    public Task CloseOutputAsync(CancellationToken token) =>
        _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token);

    public void Dispose() => _socket.Dispose();
}