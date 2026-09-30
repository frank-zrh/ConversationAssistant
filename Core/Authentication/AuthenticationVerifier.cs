using System.Text.RegularExpressions;
using ConversationAssistant.Core.Conversation;

namespace ConversationAssistant.Core.Authentication;

public static partial class AuthenticationVerifier
{
    public const string Probe = "What is two plus two? Reply with only the number 4.";

    [GeneratedRegex(@"(?<![\p{L}\p{N}])4(?![\p{L}\p{N}])|\bfour\b|四",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CorrectAnswer();

    public static async Task VerifyAsync(IWorkIqClient workIq, CancellationToken cancellationToken = default)
    {
        var answer = await workIq.AskAsync(Probe, null, cancellationToken).ConfigureAwait(false);
        if (!CorrectAnswer().IsMatch(answer.Text.Trim()))
            throw new WorkIqException("Microsoft sign-in completed, but Work IQ did not pass its access check.");
    }
}
