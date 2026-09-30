using ConversationAssistant.Core.Authentication;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Settings;
using ConversationAssistant.Core.Speech;
using ConversationAssistant_App.WorkIQ;

namespace ConversationAssistant_App.Authentication;

public sealed class AuthenticationService(WorkIqProcess process, IWorkIqClient workIq,
    SpeechEntraIdentity speechIdentity, ISpeechSettingsStore settings) : IAuthenticationService
{
    public bool IsSignedIn { get; private set; }

    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        IsSignedIn = false;
        process.Account = null;
        using var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        loginTimeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            if (settings.Load().Provider == SpeechProvider.AzureSpeech)
            {
                await speechIdentity.SignInAsync(loginTimeout.Token).ConfigureAwait(false);
                process.Account = speechIdentity.AccountName ??
                    throw new SpeechConnectionException("Entra 登录未返回可匹配的工作帐号，Work IQ 登录未继续。");
            }
            await process.RunAsync(["auth", "login"], loginTimeout.Token).ConfigureAwait(false);
            await AuthenticationVerifier.VerifyAsync(workIq, loginTimeout.Token).ConfigureAwait(false);
            IsSignedIn = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Microsoft sign-in timed out. Please try again.");
        }
    }
}
