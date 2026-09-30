using Azure.Core;
using Azure.Identity;
using ConversationAssistant.Core.Authentication;
using ConversationAssistant.Core.Settings;
using ConversationAssistant.Core.Speech;
using ConversationAssistant_App.Authentication;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class EntraIdentityTests
{
    [TestMethod]
    public void WorkIqLoginAndEveryAskArePinnedToAzureSignedInAccount()
    {
        const string account = "example@contoso.invalid";
        CollectionAssert.AreEqual(new[] { "auth", "login", "--account", account },
            WorkIqAccountArguments.WithAccount(["auth", "login"], account).ToArray());
        CollectionAssert.AreEqual(new[] { "ask", "--json", "-q", "hello", "--account", account },
            WorkIqAccountArguments.WithAccount(["ask", "--json", "-q", "hello"], account).ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            WorkIqAccountArguments.WithAccount(["ask", "--account", "other@contoso.invalid"], account));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            WorkIqAccountArguments.WithAccount(["ask"], "--fake-account"));
    }

    [TestMethod]
    public void CredentialsUseEncryptedCacheAndNeverOpenBrowserFromBackgroundTokenRequests()
    {
        var options = SpeechEntraIdentity.CreateOptions(new SpeechServiceConfiguration
        {
            TenantId = "11111111-1111-1111-1111-111111111111",
            ClientId = "22222222-2222-2222-2222-222222222222"
        }, null, "test");
        Assert.IsTrue(options.DisableAutomaticAuthentication);
        Assert.IsFalse(options.TokenCachePersistenceOptions.UnsafeAllowUnencryptedStorage);
        Assert.AreEqual("MeetingCopilot.Speech.test", options.TokenCachePersistenceOptions.Name);
        Assert.IsFalse(options.IsUnsafeSupportLoggingEnabled);
        Assert.IsFalse(options.Diagnostics.IsLoggingContentEnabled);
        Assert.AreEqual(new Uri("http://localhost"), options.RedirectUri);
        Assert.AreEqual("11111111-1111-1111-1111-111111111111", options.TenantId);
    }

    [TestMethod]
    public void SpeechCannotStartByReusingWorkIqIdentityOrLegacyKeyWithoutEntraSignIn()
    {
        var config = new SpeechServiceConfiguration
        {
            ServiceUri = "https://test.cognitiveservices.azure.com/", CloudAudioConsent = true
        };
        var identity = new SpeechEntraIdentity(new FakeSettings(config));
        Assert.IsFalse(identity.IsSignedIn);
        Assert.ThrowsExactly<SpeechConnectionException>(() => identity.GetCredential(config));
        Assert.IsNull(identity.AccountName);
    }

    [TestMethod]
    public async Task SpeechCredentialSupportsRenewalButRefusesGraphAudience()
    {
        var inner = new FakeCredential();
        var invalidated = false;
        var credential = new SpeechOnlyCredential(inner, () => invalidated = true);
        var request = new TokenRequestContext([SpeechEntraIdentity.CognitiveScope]);
        await credential.GetTokenAsync(request, CancellationToken.None);
        credential.GetToken(request, CancellationToken.None);
        Assert.AreEqual(2, inner.Calls);
        Assert.IsFalse(invalidated);
        Assert.ThrowsExactly<SpeechConnectionException>(() =>
            credential.GetToken(new TokenRequestContext(["https://graph.microsoft.com/.default"]), CancellationToken.None));
        Assert.AreEqual(2, inner.Calls);
        inner.Expired = true;
        var error = await Assert.ThrowsExactlyAsync<SpeechConnectionException>(async () =>
            await credential.GetTokenAsync(request, CancellationToken.None));
        Assert.IsTrue(invalidated);
        StringAssert.Contains(error.Message, "AADSTS65001");
        Assert.IsFalse(error.Message.Contains("synthetic-secret-token", StringComparison.Ordinal));
    }

    private sealed class FakeCredential : TokenCredential
    {
        public int Calls { get; private set; }
        public bool Expired { get; set; }
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken token)
        {
            Calls++;
            if (Expired) throw new AuthenticationFailedException("AADSTS65001 synthetic-secret-token");
            return new AccessToken("synthetic-secret-token", DateTimeOffset.UtcNow.AddMinutes(30));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token) =>
            ValueTask.FromResult(GetToken(context, token));
    }

    private sealed class FakeSettings(SpeechServiceConfiguration configuration) : ISpeechSettingsStore
    {
        public SpeechServiceConfiguration Load() => configuration;
        public void Save(SpeechServiceConfiguration value) => throw new NotSupportedException();
    }
}
