using System.Diagnostics;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.WorkIQ;

public sealed class LoggedWorkIqClient(IWorkIqClient inner, WorkIqDiagnostics diagnostics) : IWorkIqClient
{
    public async Task<WorkIqAnswer> AskAsync(string prompt, string? conversationId, CancellationToken cancellationToken)
    {
        using var scope = diagnostics.BeginCall();
        var context = diagnostics.Capture();
        var timer = Stopwatch.StartNew();
        await diagnostics.WriteAsync(context, "CallStarted", new { Prompt = prompt, ConversationId = conversationId })
            .ConfigureAwait(false);
        try
        {
            var answer = await inner.AskAsync(prompt, conversationId, cancellationToken).ConfigureAwait(false);
            await diagnostics.WriteAsync(context, "CallCompleted", new
            {
                ElapsedMs = timer.ElapsedMilliseconds, Response = answer.Text,
                answer.ConversationId, answer.Sources
            }).ConfigureAwait(false);
            return answer;
        }
        catch (Exception error)
        {
            await diagnostics.WriteAsync(context, error is OperationCanceledException ? "CallCancelled" : "CallFailed", new
            {
                ElapsedMs = timer.ElapsedMilliseconds, ErrorType = error.GetType().FullName,
                Error = error.ToString(), error.HResult, CallerCancelled = cancellationToken.IsCancellationRequested
            }).ConfigureAwait(false);
            throw;
        }
    }
}
