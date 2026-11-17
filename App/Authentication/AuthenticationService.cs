using ConversationAssistant.Core.Authentication;
using ConversationAssistant.Core.Analysis;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Copilot;
using ConversationAssistant.Core.Settings;
using ConversationAssistant.Core.Speech;
using ConversationAssistant_App.WorkIQ;

namespace ConversationAssistant_App.Authentication;

public sealed class AuthenticationService(WorkIqProcess process, IWorkIqClient workIq,
    SpeechEntraIdentity speechIdentity, ISpeechSettingsStore settings,
    CopilotEntraIdentity copilotIdentity, CopilotChatClient copilot) : IAuthenticationService
{
    private SpeechServiceConfiguration? _signedInSettings;
    public bool IsSignedIn
    {
        get
        {
            if (_signedInSettings is not { } signedIn) return false;
            var current = settings.Load();
            return current.AnalysisProvider == signedIn.AnalysisProvider &&
                (current.AnalysisProvider != AnalysisProvider.Copilot ||
                    current.Copilot.HasSameIdentity(signedIn.Copilot) && copilotIdentity.IsSignedIn);
        }
    }

    public Task SignInAsync(CancellationToken cancellationToken = default) =>
        SignInCoreAsync(false, cancellationToken);

    public Task SignInInteractivelyAsync(CancellationToken cancellationToken = default) =>
        SignInCoreAsync(true, cancellationToken);

    private async Task SignInCoreAsync(bool forceInteractive, CancellationToken cancellationToken)
    {
        _signedInSettings = null;
        process.Account = null;
        using var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        loginTimeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var configuration = settings.Load();
            if (configuration.Provider == SpeechProvider.AzureSpeech)
            {
                await speechIdentity.SignInAsync(loginTimeout.Token, forceInteractive).ConfigureAwait(false);
                if (configuration.AnalysisProvider == AnalysisProvider.WorkIq)
                    process.Account = speechIdentity.AccountName ??
                        throw new SpeechConnectionException("Entra 登录未返回可匹配的工作帐号，Work IQ 登录未继续。");
            }
            if (configuration.AnalysisProvider == AnalysisProvider.Copilot)
            {
                await copilotIdentity.SignInAsync(loginTimeout.Token, forceInteractive).ConfigureAwait(false);
                await copilot.VerifyAccessAsync(loginTimeout.Token).ConfigureAwait(false);
            }
            else
            {
                await process.RunAsync(["auth", "login"], loginTimeout.Token).ConfigureAwait(false);
                await AuthenticationVerifier.VerifyAsync(workIq, loginTimeout.Token).ConfigureAwait(false);
            }
            _signedInSettings = configuration;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Microsoft sign-in timed out. Please try again.");
        }
    }
}
