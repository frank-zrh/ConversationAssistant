using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Analysis;

public enum AnalysisProvider { WorkIq, Copilot }

public sealed class SelectableAnalysisClient(
    IWorkIqClient workIq, IWorkIqClient copilot, Func<AnalysisProvider> selectedProvider) : IWorkIqClient
{
    private const string CopilotPrefix = "copilot:";

    public async Task<WorkIqAnswer> AskAsync(string prompt, string? conversationId, CancellationToken cancellationToken)
    {
        var provider = selectedProvider();
        if (!Enum.IsDefined(provider)) throw new InvalidOperationException("Unknown analysis provider.");
        var isCopilotId = conversationId?.StartsWith(CopilotPrefix, StringComparison.Ordinal) == true;
        if (provider == AnalysisProvider.Copilot)
        {
            var answer = await copilot.AskAsync(prompt,
                isCopilotId ? conversationId![CopilotPrefix.Length..] : null, cancellationToken).ConfigureAwait(false);
            return answer with { ConversationId = answer.ConversationId is null ? null : CopilotPrefix + answer.ConversationId };
        }
        return await workIq.AskAsync(prompt, isCopilotId ? null : conversationId, cancellationToken).ConfigureAwait(false);
    }
}
