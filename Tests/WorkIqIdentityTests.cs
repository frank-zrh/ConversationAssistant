using ConversationAssistant.Core.WorkIQ;
using ConversationAssistant_App.Authentication;
using Microsoft.Identity.Client;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class WorkIqIdentityTests
{
    [TestMethod]
    public void UsesHostedWorkIqScopeAndPublishedPublicClientNotSpeechOrGraphCredentials()
    {
        Assert.AreEqual("ba081686-5d24-4bc6-a0d6-d034ecffed87", WorkIqEntraIdentity.ClientId);
        Assert.AreEqual("http://localhost:12798", WorkIqEntraIdentity.BrowserRedirectUri);
        CollectionAssert.AreEqual(new[] { "fdcc1f02-fc51-4226-8753-f668596af7f7/WorkIQAgent.Ask" }, WorkIqEntraIdentity.Scopes);
        Assert.AreEqual(WorkIqHttpClient.Scope, WorkIqEntraIdentity.Scopes.Single());
    }

    [TestMethod]
    public async Task WindowsAccountWithoutSavedRecordCanSignInSilently()
    {
        var silent = 0;
        var browser = 0;
        var result = await EntraSignInFlow.AuthenticateAsync(
            () => { silent++; return Task.FromResult("windows-account"); },
            () => { browser++; return Task.FromResult("selected"); }, CancellationToken.None);
        Assert.AreEqual("windows-account", result);
        Assert.AreEqual(1, silent);
        Assert.AreEqual(0, browser);
    }

    [TestMethod]
    public async Task ValidCachedIdentityDoesNotOpenBrowser()
    {
        var browser = false;
        var result = await EntraSignInFlow.AuthenticateAsync(() => Task.FromResult("cached"),
            () => { browser = true; return Task.FromResult("selected"); }, CancellationToken.None);
        Assert.AreEqual("cached", result);
        Assert.IsFalse(browser);
    }

    [TestMethod]
    public async Task FailedCachedIdentityOpensBrowserOnce()
    {
        var browser = 0;
        var result = await EntraSignInFlow.AuthenticateAsync(
            () => Task.FromException<string>(new MsalUiRequiredException("interaction_required", "interaction required")),
            () => { browser++; return Task.FromResult("selected"); }, CancellationToken.None);
        Assert.AreEqual("selected", result);
        Assert.AreEqual(1, browser);
    }

    [TestMethod]
    public async Task CancellationDoesNotOpenAnotherBrowser()
    {
        var browser = false;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => EntraSignInFlow.AuthenticateAsync(
            () => Task.FromException<string>(new OperationCanceledException()),
            () => { browser = true; return Task.FromResult("selected"); }, CancellationToken.None));
        Assert.IsFalse(browser);
    }

    [TestMethod]
    public async Task ExplicitAccountSwitchCanStillBypassSilentSignIn()
    {
        var silent = false;
        var result = await EntraSignInFlow.AuthenticateAsync(
            () => { silent = true; return Task.FromResult("cached"); },
            () => Task.FromResult("selected"), CancellationToken.None, forceInteractive: true);
        Assert.AreEqual("selected", result);
        Assert.IsFalse(silent);
    }

    [TestMethod]
    public async Task UnavailableWindowsBrokerFallsBackToBrowserOnce()
    {
        var browser = 0;
        var result = await EntraSignInFlow.AuthenticateAsync(
            () => Task.FromException<string>(new MsalClientException("broker_unavailable", "unavailable")),
            () => { browser++; return Task.FromResult("selected"); }, CancellationToken.None);
        Assert.AreEqual("selected", result);
        Assert.AreEqual(1, browser);
    }

    [TestMethod]
    public async Task ServiceFailuresDoNotStartBrowserOrHideDiagnostics()
    {
        var browser = false;
        var expected = new MsalServiceException("temporarily_unavailable", "service failure");
        var actual = await Assert.ThrowsExactlyAsync<MsalServiceException>(() => EntraSignInFlow.AuthenticateAsync(
            () => Task.FromException<string>(expected),
            () => { browser = true; return Task.FromResult("selected"); }, CancellationToken.None));
        Assert.AreSame(expected, actual);
        Assert.IsFalse(browser);
    }

    [TestMethod]
    public async Task CancellationBetweenSilentAndBrowserPreventsBrowserLaunch()
    {
        using var cancellation = new CancellationTokenSource();
        var browser = false;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => EntraSignInFlow.AuthenticateAsync(
            () =>
            {
                cancellation.Cancel();
                return Task.FromException<string>(new MsalUiRequiredException("interaction_required", "interaction required"));
            },
            () => { browser = true; return Task.FromResult("selected"); }, cancellation.Token));
        Assert.IsFalse(browser);
    }

    [TestMethod]
    public async Task BrowserFailureIsNotRetried()
    {
        var browser = 0;
        await Assert.ThrowsExactlyAsync<MsalClientException>(() => EntraSignInFlow.AuthenticateAsync(
            () => Task.FromException<string>(new MsalUiRequiredException("interaction_required", "interaction required")),
            () => { browser++; return Task.FromException<string>(new MsalClientException("authentication_canceled", "canceled")); },
            CancellationToken.None));
        Assert.AreEqual(1, browser);
    }
}
