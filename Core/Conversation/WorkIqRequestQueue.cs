using System.Threading.Channels;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Conversation;

public sealed class WorkIqRequestQueue
{
    private readonly Channel<WorkIqRequest> _channel = Channel.CreateBounded<WorkIqRequest>(
        new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });

    public bool TryEnqueue(WorkIqRequest request) => _channel.Writer.TryWrite(request);
    public IAsyncEnumerable<WorkIqRequest> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
    public void Complete() => _channel.Writer.TryComplete();
}
