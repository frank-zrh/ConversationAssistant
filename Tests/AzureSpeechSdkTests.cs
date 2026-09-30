using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Settings;
using ConversationAssistant_App.Speech;
using Microsoft.CognitiveServices.Speech;
using ConversationAssistant_App.Authentication;
using Azure.Core;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class AzureSpeechSdkTests
{
    [TestMethod]
    [DataRow("https://stttest1.cognitiveservices.azure.com/", ConversationLanguage.Chinese, "zh-CN")]
    [DataRow("https://synthetic-resource.cognitiveservices.azure.com/", ConversationLanguage.Chinese, "zh-CN")]
    [DataRow("https://synthetic-resource.cognitiveservices.azure.com/", ConversationLanguage.English, "en-US")]
    [DataRow("wss://synthetic-resource.cognitiveservices.azure.com/stt/speech/recognition/conversation/cognitiveservices/v1",
        ConversationLanguage.Chinese, "zh-CN")]
    public void NativeSdkConfigAndPcmStreamCanBeCreatedWithoutConnecting(
        string uri, ConversationLanguage language, string culture)
    {
        var settings = new SpeechServiceConfiguration
        {
            ServiceUri = uri, CloudAudioConsent = true,
            TenantId = "11111111-1111-1111-1111-111111111111"
        };
        var credential = new SyntheticCredential();
        var config = AzureSpeechSessionFactory.CreateConfiguration(settings, language, credential);
        Assert.AreEqual(culture, config.SpeechRecognitionLanguage);
        Assert.AreEqual("800", config.GetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs));
        Assert.AreEqual(AzureSpeechEndpoint.GetSdkUri(uri).AbsoluteUri,
            config.GetProperty(PropertyId.SpeechServiceConnection_Endpoint));
        Assert.IsEmpty(config.GetProperty(PropertyId.SpeechServiceConnection_Key));
        using var session = new AzureSpeechSessionFactory(new SyntheticProvider(credential)).Create(settings, language);
        session.Write(new byte[3200]);
        // Do not call StartAsync: native setup is tested without uploading test audio or any token.
    }

    [TestMethod]
    public void AzureErrorsExposeCodesButNeverRawKeyHeadersOrDetails()
    {
        const string secret = "SYNTHETIC_SECRET_DO_NOT_DISPLAY";
        var text = AzureSpeechDiagnostics.Cancellation(CancellationErrorCode.ConnectionFailure,
            $"HTTP status: 403; Ocp-Apim-Subscription-Key: {secret}; wss://private.example/?token={secret}");
        StringAssert.Contains(text, "ConnectionFailure/HTTP 403");
        Assert.IsFalse(text.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("private.example", StringComparison.Ordinal));
        var native = AzureSpeechDiagnostics.Initialization(new InvalidOperationException(
            $"SPXERR_INVALID_ARG; key={secret}"));
        StringAssert.Contains(native, "SPXERR_INVALID_ARG");
        Assert.IsFalse(native.Contains(secret, StringComparison.Ordinal));
        StringAssert.Contains(AzureSpeechDiagnostics.Cancellation(
            CancellationErrorCode.AuthenticationFailure, $"Authentication error (401). key={secret}"),
            "AuthenticationFailure/HTTP 401");
    }

    [TestMethod]
    public void TenantMismatch400ExplainsDirectoryProblemWithoutExposingTokensOrIds()
    {
        const string id = "33333333-3333-3333-3333-333333333333";
        const string secret = "synthetic-token-not-to-display";
        var message = AzureSpeechDiagnostics.Cancellation(CancellationErrorCode.BadRequest,
            $"HTTP 400. Token tenant {id} does not match resource tenant. Authorization={secret}");
        StringAssert.Contains(message, "[TenantMismatch]");
        StringAssert.Contains(message, "资源所在订阅的目录 ID");
        Assert.IsFalse(message.Contains(id, StringComparison.Ordinal));
        Assert.IsFalse(message.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(message.Contains("不要填写", StringComparison.Ordinal));
        var generic = AzureSpeechDiagnostics.Cancellation(CancellationErrorCode.BadRequest, "HTTP 400.");
        StringAssert.Contains(generic, "不能仅凭 HTTP 400");
        Assert.IsFalse(generic.Contains("[TenantMismatch]", StringComparison.Ordinal));
    }

    private sealed class SyntheticProvider(TokenCredential credential) : ISpeechCredentialProvider
    {
        public TokenCredential GetCredential(SpeechServiceConfiguration settings) => credential;
    }

    private sealed class SyntheticCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken token) =>
            new("synthetic-token-not-valid", DateTimeOffset.UtcNow.AddMinutes(20));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken token) =>
            ValueTask.FromResult(GetToken(requestContext, token));
    }
}
