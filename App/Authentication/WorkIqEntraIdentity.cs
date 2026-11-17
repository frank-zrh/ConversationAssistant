using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.WorkIQ;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;

namespace ConversationAssistant_App.Authentication;

public sealed class WorkIqEntraIdentity : IWorkIqTokenProvider
{
    // Published public-client configuration from the official hosted Work IQ plugin.
    public const string ClientId = "ba081686-5d24-4bc6-a0d6-d034ecffed87";
    internal const string BrowserRedirectUri = "http://localhost:12798";
    internal static readonly string[] Scopes = [WorkIqHttpClient.Scope];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPublicClientApplication? _browser;
    private IPublicClientApplication? _broker;
    private sealed record Session(IPublicClientApplication Application, IAccount Account);
    private Session? _session;
    public bool IsSignedIn => _session is not null;
    public event Action? StateChanged;

    public async Task SignInAsync(CancellationToken cancellationToken, bool forceInteractive = false)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            var session = await EntraSignInFlow.AuthenticateAsync(
                () => AcquireSilentlyAsync(cancellationToken),
                async () =>
                {
                    var result = await _browser!.AcquireTokenInteractive(Scopes)
                        .WithUseEmbeddedWebView(false).WithPrompt(Prompt.SelectAccount)
                        .ExecuteAsync(cancellationToken).ConfigureAwait(false);
                    return new Session(_browser!, result.Account);
                }, cancellationToken, forceInteractive)
                .ConfigureAwait(false);
            _session = session;
            StateChanged?.Invoke();
        }
        catch (MsalException error)
        {
            throw SignInFailure(error.ErrorCode);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var session = _session;
        if (session is null) throw new WorkIqException("Sign in to Microsoft Work IQ before using AI assistance. Local Whisper transcription does not require sign-in.");
        try
        {
            var token = await session.Application.AcquireTokenSilent(Scopes, session.Account)
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return token.AccessToken;
        }
        catch (MsalException error)
        {
            Invalidate();
            throw SignInFailure(error.ErrorCode);
        }
    }

    public void Invalidate()
    {
        _session = null;
        StateChanged?.Invoke();
    }

    private async Task InitializeAsync()
    {
        if (_browser is not null) return;
        var browser = PublicClientApplicationBuilder.Create(ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, "organizations")
            .WithRedirectUri(BrowserRedirectUri).Build();
        var broker = PublicClientApplicationBuilder.Create(ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, "organizations")
            .WithRedirectUri($"ms-appx-web://microsoft.aad.brokerplugin/{ClientId}")
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows)).Build();
        var cache = await MsalCacheHelper.CreateAsync(new StorageCreationPropertiesBuilder(
            "workiq-msal.bin", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ConversationAssistant")).Build()).ConfigureAwait(false);
        cache.VerifyPersistence();
        cache.RegisterCache(browser.UserTokenCache);
        cache.RegisterCache(broker.UserTokenCache);
        _broker = broker;
        _browser = browser;
    }

    private async Task<Session> AcquireSilentlyAsync(CancellationToken cancellationToken)
    {
        var cached = (await _browser!.GetAccountsAsync().ConfigureAwait(false)).ToArray();
        if (cached.Length > 1) throw new MsalUiRequiredException("account_selection_required", "Choose an account.");
        if (cached.Length == 1)
        {
            try
            {
                var result = await _browser.AcquireTokenSilent(Scopes, cached[0])
                    .ExecuteAsync(cancellationToken).ConfigureAwait(false);
                return new Session(_browser, result.Account);
            }
            catch (MsalUiRequiredException) { }
        }
        var accounts = (await _broker!.GetAccountsAsync().ConfigureAwait(false)).ToArray();
        if (accounts.Length > 1) throw new MsalUiRequiredException("account_selection_required", "Choose an account.");
        var account = accounts.SingleOrDefault() ?? PublicClientApplication.OperatingSystemAccount;
        var token = await _broker.AcquireTokenSilent(Scopes, account)
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return new Session(_broker, token.Account);
    }

    private static WorkIqException SignInFailure(string errorCode) => new(
        $"Microsoft Work IQ sign-in failed ({errorCode}). Check internet access, the organizational account, Microsoft 365 Copilot license, " +
        "administrator consent for Work IQ and tenant policies. Ensure localhost port 12798 is available, then sign in again.");
}
