using System.Diagnostics;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.WorkIQ;
using Microsoft.Extensions.Logging;

namespace ConversationAssistant_App.WorkIQ;

public sealed class WorkIqClient(WorkIqProcess process, ILogger<WorkIqClient> logger) : IWorkIqClient
{
    public async Task<WorkIqAnswer> AskAsync(string prompt, string? conversationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var arguments = new List<string> { "ask", "--json", "-q", prompt };
        if (conversationId is not null) { arguments.Add("--conversation-id"); arguments.Add(conversationId); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        string output;
        var timer = Stopwatch.StartNew();
        try { output = await process.RunAsync(arguments, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Work IQ did not respond within three minutes.");
        }
        var answer = WorkIqResponseParser.Parse(output);
        logger.LogInformation("Work IQ response received in {LatencyMs}ms", timer.ElapsedMilliseconds);
        return answer;
    }
}
