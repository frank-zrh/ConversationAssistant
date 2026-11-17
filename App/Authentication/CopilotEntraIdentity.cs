using System.Security.Cryptography;
using System.Text;
using Azure.Core;
using Azure.Identity;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Copilot;
using ConversationAssistant.Core.Settings;

namespace ConversationAssistant_App.Authentication;

public sealed class CopilotEntraIdentity(ISpeechSettingsStore settings) : ICopilotTokenProvider
{
    public static IReadOnlyList<string> Scopes { get; } = Array.AsReadOnly(new[]
    {
        "https://graph.microsoft.com/Sites.Read.All",
        "https://graph.microsoft.com/Mail.Read",
        "https://graph.microsoft.com/People.Read.All",
        "https://graph.microsoft.com/OnlineMeetingTranscript.Read.All",
        "https://graph.microsoft.com/Chat.Read",
        "https://graph.microsoft.com/ChannelMessage.Read.All",
        "https://graph.microsoft.com/ExternalItem.Read.All"
    });
    private readonly SemaphoreSlim _signInGate = new(1, 1);
    private TokenCredential? _credential;
    private CopilotConfiguration? _configuration;
    public bool IsSignedIn => _credential is not null && _configuration is not null &&
        settings.Load().Copilot is { PreviewConsent: true } current && current.HasSameIdentity(_configuration);
    public event Action? StateChanged;

    public async Task SignInAsync(CancellationToken cancellationToken, bool forceInteractive = false)
    {
        await _signInGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Invalidate();
            var configuration = settings.Load().Copilot;
            configuration.Validate();
            var cacheId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                configuration.TenantId.ToLowerInvariant() + "|" + configuration.ClientId.ToLowerInvariant())))[..20];
            var file = new CurrentUserProtectedFile(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ConversationAssistant", $"copilot-account-{cacheId}.bin"), "ConversationAssistant.Copilot.EntraAccount.v1");
            var record = EntraSignInFlow.ReadRecord(file, forceInteractive, cancellationToken);
            var browser = new InteractiveBrowserCredential(CreateOptions(configuration, record, cacheId));
            var context = new TokenRequestContext(Scopes.ToArray());
            record = await EntraSignInFlow.AuthenticateAsync(record,
                async () => { await browser.GetTokenAsync(context, cancellationToken).ConfigureAwait(false); },
                () => new InteractiveBrowserCredential(CreateOptions(configuration, null, cacheId))
                    .AuthenticateAsync(context, cancellationToken)).ConfigureAwait(false);
            if (!string.Equals(record.TenantId, configuration.TenantId, StringComparison.OrdinalIgnoreCase))
                throw new WorkIqException("Copilot sign-in returned a different tenant. Check the Copilot organization Tenant ID.");
            using var memory = new MemoryStream();
            record.Serialize(memory, cancellationToken);
            var serialized = memory.ToArray();
            try { file.Write(serialized); }
            finally { CryptographicOperations.ZeroMemory(serialized); }
            _configuration = configuration;
            _credential = new InteractiveBrowserCredential(CreateOptions(configuration, record, cacheId));
            StateChanged?.Invoke();
        }
        catch (Exception error) when (error is AuthenticationFailedException or CredentialUnavailableException)
        {
            throw SignInFailure();
        }
        catch (System.Text.Json.JsonException)
        {
            throw new WorkIqException("Copilot account cache is invalid. Remove the application's copilot-account cache and sign in again.");
        }
        finally { _signInGate.Release(); }
    }

    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var credential = _credential;
        if (!IsSignedIn || credential is null)
            throw new WorkIqException("Sign in to Microsoft 365 Copilot first. Speech and Work IQ tokens cannot be used for Copilot Chat.");
        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes.ToArray()), cancellationToken)
                .ConfigureAwait(false);
            return token.Token;
        }
        catch (Exception error) when (error is AuthenticationFailedException or CredentialUnavailableException)
        {
            Invalidate();
            throw SignInFailure();
        }
    }

    public void Invalidate()
    {
        _credential = null;
        _configuration = null;
        StateChanged?.Invoke();
    }

    internal static InteractiveBrowserCredentialOptions CreateOptions(
        CopilotConfiguration configuration, AuthenticationRecord? record, string cacheId)
    {
        configuration.Validate();
        var options = new InteractiveBrowserCredentialOptions
        {
            TenantId = configuration.TenantId, ClientId = configuration.ClientId,
            RedirectUri = new Uri("http://localhost"), DisableAutomaticAuthentication = true,
            BrowserCustomization = new BrowserCustomizationOptions { UseEmbeddedWebView = false },
            AuthenticationRecord = record, IsUnsafeSupportLoggingEnabled = false,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = "ConversationAssistant.Copilot." + cacheId, UnsafeAllowUnencryptedStorage = false
            }
        };
        options.Diagnostics.IsLoggingContentEnabled = false;
        return options;
    }

    private static WorkIqException SignInFailure() => new(
        "Copilot Entra authentication failed. Check the Copilot tenant/client registration, all seven delegated Graph permissions, administrator consent and conditional access. No Speech token was reused.");
}
