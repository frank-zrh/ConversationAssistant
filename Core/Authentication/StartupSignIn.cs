using ConversationAssistant.Core.Conversation;

namespace ConversationAssistant.Core.Authentication;

public sealed class StartupSignIn(IAuthenticationService authentication)
{
    private readonly object _gate = new();
    private Task? _attempt;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return _attempt ??= Task.Run(() => authentication.SignInAsync(cancellationToken), cancellationToken);
    }
}
