using Microsoft.Identity.Client;

namespace ConversationAssistant_App.Authentication;

internal static class EntraSignInFlow
{
    internal static async Task<T> AuthenticateAsync<T>(
        Func<Task<T>> acquireSilently, Func<Task<T>> acquireInteractively,
        CancellationToken cancellationToken, bool forceInteractive = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!forceInteractive)
        {
            try
            {
                return await acquireSilently().ConfigureAwait(false);
            }
            catch (Exception error) when (error is MsalUiRequiredException or MsalClientException)
            {
                // A missing Windows broker or an interaction requirement permits browser fallback.
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return await acquireInteractively().ConfigureAwait(false);
    }
}
