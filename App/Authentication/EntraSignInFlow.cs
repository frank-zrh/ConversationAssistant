using System.Security.Cryptography;
using Azure.Identity;
using ConversationAssistant.Core.Settings;

namespace ConversationAssistant_App.Authentication;

internal static class EntraSignInFlow
{
    internal static AuthenticationRecord? ReadRecord(
        CurrentUserProtectedFile file, bool forceInteractive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (forceInteractive) return null;
        byte[]? plaintext = null;
        try
        {
            plaintext = file.Read();
            if (plaintext is null) return null;
            using var stream = new MemoryStream(plaintext);
            return AuthenticationRecord.Deserialize(stream, cancellationToken);
        }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException)
        {
            // Records copied from another Windows user or PC cannot be reused; authenticate again.
            return null;
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static async Task<T> AuthenticateAsync<T>(
        T? record, Func<Task> acquireSilently, Func<Task<T>> acquireInteractively) where T : class
    {
        if (record is not null)
        {
            try
            {
                await acquireSilently().ConfigureAwait(false);
                return record;
            }
            catch (Exception error) when (error is AuthenticationRequiredException or AuthenticationFailedException)
            {
                // Only an explicit sign-in flow may open a browser, never background token renewal.
            }
        }
        return await acquireInteractively().ConfigureAwait(false);
    }
}
