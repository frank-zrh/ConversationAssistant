using ConversationAssistant.Core.Authentication;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.WorkIQ;

namespace ConversationAssistant_App.Authentication;

public sealed class AuthenticationService(WorkIqEntraIdentity identity, WorkIqHttpClient workIq) : IAuthenticationService
{
    public bool IsSignedIn => identity.IsSignedIn;

    public Task SignInAsync(CancellationToken cancellationToken = default) =>
        SignInCoreAsync(false, cancellationToken);

    public Task SignInInteractivelyAsync(CancellationToken cancellationToken = default) =>
        SignInCoreAsync(true, cancellationToken);

    private async Task SignInCoreAsync(bool forceInteractive, CancellationToken cancellationToken)
    {
        using var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        loginTimeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await identity.SignInAsync(loginTimeout.Token, forceInteractive).ConfigureAwait(false);
            workIq.ResetSession();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Microsoft sign-in timed out. Please try again.");
        }
    }
}
