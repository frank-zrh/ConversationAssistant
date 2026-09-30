using System.Threading.Channels;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Conversation;

public sealed class QuestionQueue
{
    private readonly Channel<QuestionRequest> _channel = Channel.CreateBounded<QuestionRequest>(
        new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });

    public bool TryEnqueue(QuestionRequest question) => _channel.Writer.TryWrite(question);
    public IAsyncEnumerable<QuestionRequest> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
    public void Complete() => _channel.Writer.TryComplete();
}
