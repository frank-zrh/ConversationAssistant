using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Settings;
using ConversationAssistant_App.Speech;
using Microsoft.CognitiveServices.Speech;
using ConversationAssistant_App.Authentication;
using Azure.Core;
using System.Net;
using System.Net.Sockets;

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
        Assert.AreEqual("true", config.GetProperty(PropertyId.SpeechServiceResponse_DiarizeIntermediateResults));
        Assert.AreEqual(AzureSpeechEndpoint.GetSdkUri(uri).AbsoluteUri,
            config.GetProperty(PropertyId.SpeechServiceConnection_Endpoint));
        Assert.IsEmpty(config.GetProperty(PropertyId.SpeechServiceConnection_Key));
        using var session = new AzureSpeechSessionFactory(new SyntheticProvider(credential)).Create(settings, language);
        session.Write(new byte[3200]);
        // Do not call StartAsync: native setup is tested without uploading test audio or any token.
    }

    [TestMethod]
    [DataRow(ConversationLanguage.Chinese)]
    [DataRow(ConversationLanguage.English)]
    public async Task TranscriberStartupWaitsForConnectionAndSupportsCancellation(ConversationLanguage language)
    {
        await AssertWaitsForConnectionAsync(language, async (configuration, cancellationToken) =>
        {
            using var session = new AzureSpeechSession(configuration, language);
            await session.StartAsync(cancellationToken);
        });
    }

    [TestMethod]
    [DataRow(ConversationLanguage.Chinese)]
    [DataRow(ConversationLanguage.English)]
    public async Task ConnectionOnlyProbeWaitsForConnectionAndSupportsCancellation(ConversationLanguage language)
    {
        await AssertWaitsForConnectionAsync(language, AzureSpeechConnectionTester.ConnectAsync);
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
    [DataRow(false)]
    [DataRow(true)]
    public void NativeNotFoundIncludesStartupStageWithoutExposingSdkDetails(bool transcription)
    {
        const string secret = "SYNTHETIC_SECRET_DO_NOT_DISPLAY";
        var stage = transcription ? AzureSpeechDiagnostics.StartupStage.Transcription :
            AzureSpeechDiagnostics.StartupStage.Connection;
        var error = new ApplicationException(
            $"SPXERR_NOT_FOUND; Authorization: Bearer {secret}; https://private.example/",
            new InvalidOperationException(secret));

        var message = AzureSpeechDiagnostics.Initialization(error, stage);

        StringAssert.Contains(message, "[SPXERR_NOT_FOUND]");
        StringAssert.Contains(message, transcription ? "[Transcription]" : "[Connection]");
        Assert.IsFalse(message.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(message.Contains("private.example", StringComparison.Ordinal));
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

    private static async Task AssertWaitsForConnectionAsync(ConversationLanguage language,
        Func<SpeechConfig, CancellationToken, Task> connect)
    {
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var settings = new SpeechServiceConfiguration
        {
            ServiceUri = "https://synthetic-resource.cognitiveservices.azure.com/", CloudAudioConsent = true,
            TenantId = "11111111-1111-1111-1111-111111111111"
        };
        var configuration = AzureSpeechSessionFactory.CreateConfiguration(settings, language, new SyntheticCredential());
        configuration.SetProxy("127.0.0.1", ((IPEndPoint)proxy.LocalEndpoint).Port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = proxy.AcceptTcpClientAsync(deadline.Token).AsTask();
        var connecting = connect(configuration, deadline.Token);
        try
        {
            await Task.WhenAny(connecting, accepting);
            if (connecting.IsCompleted) await connecting;
            // Hold the proxy connection locally without forwarding it to Azure or completing TLS.
            await accepting;
            Assert.IsFalse(connecting.IsCompleted, "Startup must wait for the service connection.");
            deadline.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => connecting);
        }
        finally
        {
            deadline.Cancel();
            if (accepting.IsCompletedSuccessfully) accepting.Result.Dispose();
        }
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
