using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Settings;
using ConversationAssistant.Core.Transcript;
using ConversationAssistant.Core.WorkIQ;
using ConversationAssistant_App.Speech;
using Microsoft.CognitiveServices.Speech;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class LocalizationTests
{
    private static readonly UiText English = UiText.For(ConversationLanguage.English);
    private static readonly UiText Chinese = UiText.For(ConversationLanguage.Chinese);

    [TestMethod]
    public void ResourceSetsHaveMatchingKeysAndFormatArguments()
    {
        CollectionAssert.AreEquivalent(English.Keys.ToArray(), Chinese.Keys.ToArray());
        Assert.IsGreaterThan(200, English.Count);
        foreach (var key in English.Keys)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(English[key]), key);
            Assert.IsFalse(string.IsNullOrWhiteSpace(Chinese[key]), key);
            var count = CompositeFormat.Parse(English[key]).MinimumArgumentCount;
            Assert.AreEqual(count, CompositeFormat.Parse(Chinese[key]).MinimumArgumentCount, key);
            var arguments = Enumerable.Range(0, count).Select(index => (object)$"value-{index}").ToArray();
            foreach (var texts in new[] { English, Chinese })
            {
                var formatted = texts.Format(key, arguments);
                foreach (var argument in arguments) StringAssert.Contains(formatted, (string)argument, key);
            }
        }
    }

    [TestMethod]
    public void ExplicitLanguageDoesNotDependOnOrChangeThreadCulture()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.AreEqual("Start Conversation", English["StartConversation"]);
            Assert.AreEqual("开始对话", Chinese["StartConversation"]);
            Assert.AreEqual("zh-CN", Chinese.LanguageTag);
            Assert.AreEqual("en-US", English.LanguageTag);
            Assert.AreEqual("de-DE", CultureInfo.CurrentUICulture.Name);
            Assert.AreSame(English, UiText.For(ConversationLanguage.English));
            Assert.AreSame(Chinese, UiText.For(ConversationLanguage.Chinese));
        }
        finally { CultureInfo.CurrentUICulture = original; }
    }

    [TestMethod]
    public void MissingKeysAndUnsupportedInterfaceLanguagesFailExplicitly()
    {
        Assert.Throws<MissingManifestResourceException>(() => _ = English["DoesNotExist"]);
        Assert.Throws<MissingManifestResourceException>(() => _ = Chinese["DoesNotExist"]);
        Assert.IsFalse(Chinese.TryGetValue("DoesNotExist", out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => UiText.For(ConversationLanguage.Auto));
        Assert.Throws<ArgumentOutOfRangeException>(() => UiText.For((ConversationLanguage)100));
    }

    [TestMethod]
    public void StoredMessagesResolveInTheCurrentlySelectedLanguage()
    {
        var status = new UiMessage("AnalysisCompletedNew", 7);
        Assert.AreEqual(Chinese.Format("AnalysisCompletedNew", 7), status.Resolve(Chinese));
        Assert.AreEqual(English.Format("AnalysisCompletedNew", 7), status.Resolve(English));
        Assert.AreNotEqual(status.Resolve(Chinese), status.Resolve(English));
    }

    [TestMethod]
    public void AllApplicationDiagnosticTemplatesRoundTripInBothLanguages()
    {
        foreach (var source in new[] { English, Chinese })
        {
            var target = source == English ? Chinese : English;
            foreach (var key in source.Keys.Where(key => key.StartsWith("Error", StringComparison.Ordinal)))
            {
                var count = CompositeFormat.Parse(source[key]).MinimumArgumentCount;
                var arguments = Enumerable.Range(0, count).Select(index => (object)$"diagnostic-{index}").ToArray();
                var message = UiMessage.FromDiagnostic(source.Format(key, arguments));
                Assert.IsNotNull(message, key);
                Assert.AreEqual(key, message.Key, key);
                Assert.AreEqual(target.Format(key, arguments), message.Resolve(target), key);
            }
        }
    }

    [TestMethod]
    public void DiagnosticDetailsAndParametersSurviveLanguageChanges()
    {
        const string detail = "\nVendor trace: 示例 0x80004005";
        var error = UiMessage.FromDiagnostic(English["ErrorCliStart"] + detail)!;
        Assert.AreEqual(Chinese["ErrorCliStart"] + detail, error.Resolve(Chinese));
        Assert.AreEqual(English["ErrorCliStart"] + detail, error.Resolve(English));

        var parameter = UiMessage.FromDiagnostic(English["ErrorRenderLimit"] + " (Parameter 'markdown')")!;
        Assert.AreEqual("ErrorRenderLimit", parameter.Key);
        StringAssert.Contains(parameter.Resolve(Chinese), Chinese.Format("DiagnosticParameter", "markdown"));
        StringAssert.Contains(parameter.Resolve(English), English.Format("DiagnosticParameter", "markdown"));
    }

    [TestMethod]
    public void UnknownExternalErrorsKeepTheirOriginalDiagnosticText()
    {
        const string vendorMessage = "SDK 0x1234: Original 外部诊断";
        var message = UiMessage.FromDiagnostic(vendorMessage)!;
        Assert.AreEqual(English.Format("DiagnosticUnknown", vendorMessage), message.Resolve(English));
        Assert.AreEqual(Chinese.Format("DiagnosticUnknown", vendorMessage), message.Resolve(Chinese));
        Assert.IsNull(UiMessage.FromDiagnostic(""));
        Assert.IsNull(UiMessage.FromDiagnostic(null));
    }

    [TestMethod]
    public void MissingOfflineSpeakerModelGuidanceFollowsTheListeningLanguage()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"missing-speaker-models-{Guid.NewGuid():N}");
        var error = Assert.Throws<FileNotFoundException>(() => new OfflineSpeakerModelManager(directory).Validate());
        var message = UiMessage.FromDiagnostic(error.Message)!;

        Assert.AreEqual("ErrorSpeakerModelMissing", message.Key);
        Assert.AreEqual(English["ErrorSpeakerModelMissing"], message.Resolve(English));
        Assert.AreEqual(Chinese["ErrorSpeakerModelMissing"], message.Resolve(Chinese));
        StringAssert.Contains(message.Resolve(Chinese), "Install-OfflineSpeakerModels.ps1");
        StringAssert.Contains(message.Resolve(Chinese), "监听时不会自动下载模型");
    }

    [TestMethod]
    [DataRow("Offline speaker separation returned invalid turn boundaries. Pause and resume; reinstall the speaker models if this repeats.",
        "ErrorSpeakerBoundaries")]
    [DataRow("Offline speaker separation requires short 16 kHz mono audio windows (at most 15 seconds). Pause and resume after checking the audio source.",
        "ErrorSpeakerWindow")]
    [DataRow("Offline speaker separation requires normalized 16 kHz mono PCM audio. Select a supported microphone or loopback source.",
        "ErrorSpeakerPcm")]
    [DataRow("Offline speaker separation reached its session limit. Pause and resume to start a new speaker session.",
        "ErrorSpeakerSessionLimit")]
    [DataRow("Offline speaker separation requires the Windows x64 build. Install that build or select Azure Speech explicitly.",
        "ErrorSpeakerPlatform")]
    [DataRow("Offline speaker models are incompatible. Run App\\Speech\\Install-OfflineSpeakerModels.ps1 and rebuild.",
        "ErrorSpeakerModelsIncompatible")]
    [DataRow("Offline speaker separation could not load its local models or native runtime. Reinstall the Windows x64 app and Microsoft Visual C++ x64 runtime, and run App\\Speech\\Install-OfflineSpeakerModels.ps1 before rebuilding.",
        "ErrorSpeakerRuntime")]
    [DataRow("Offline speaker separation returned invalid timing. Pause and resume; reinstall the speaker models if this repeats.",
        "ErrorSpeakerTiming")]
    [DataRow("Offline speaker separation could not extract a valid voice embedding. Check the audio source and reinstall the speaker models if this repeats.",
        "ErrorSpeakerEmbedding")]
    [DataRow("Offline speech recognition or speaker separation stopped. Check the audio source and CPU load, then pause and resume. Reinstall the local speaker models if this repeats.",
        "ErrorWhisperSpeakerStopped")]
    public void OfflineSpeakerDiagnosticsTranslateWithoutLosingRecoveryGuidance(string diagnostic, string key)
    {
        var message = UiMessage.FromDiagnostic(diagnostic)!;
        Assert.AreEqual(key, message.Key);
        Assert.AreEqual(English[key], message.Resolve(English));
        Assert.AreEqual(Chinese[key], message.Resolve(Chinese));
    }

    [TestMethod]
    public void ExistingSettingsErrorsAreTranslatedWithoutChangingExceptionContracts()
    {
        var missingEndpoint = Assert.Throws<InvalidOperationException>(() =>
            new SpeechServiceConfiguration().ValidateForAzure());
        Assert.AreEqual(English["ErrorEndpointMissing"], English.LocalizeDiagnostic(missingEndpoint.Message));
        var invalidEndpoint = Assert.Throws<InvalidOperationException>(() => AzureSpeechEndpoint.Parse("invalid"));
        Assert.AreEqual(English["ErrorEndpointInvalid"], English.LocalizeDiagnostic(invalidEndpoint.Message));
        var tenant = Assert.Throws<InvalidOperationException>(() =>
            new SpeechServiceConfiguration { TenantId = "not-a-guid" }.ValidateIdentity());
        Assert.AreEqual(English["ErrorTenantInvalid"], English.LocalizeDiagnostic(tenant.Message));
    }

    [TestMethod]
    [DataRow(CancellationErrorCode.AuthenticationFailure, "ErrorAzureAuth")]
    [DataRow(CancellationErrorCode.Forbidden, "ErrorAzureForbidden")]
    [DataRow(CancellationErrorCode.BadRequest, "ErrorAzureBadRequest")]
    [DataRow(CancellationErrorCode.TooManyRequests, "ErrorAzureThrottled")]
    [DataRow(CancellationErrorCode.ConnectionFailure, "ErrorAzureConnection")]
    [DataRow(CancellationErrorCode.ServiceTimeout, "ErrorAzureConnection")]
    [DataRow(CancellationErrorCode.ServiceUnavailable, "ErrorAzureConnection")]
    [DataRow(CancellationErrorCode.ServiceError, "ErrorAzureService")]
    [DataRow(CancellationErrorCode.NoError, "ErrorAzureStopped")]
    public void AzureDiagnosticsTranslateGuidanceAndPreserveTechnicalCodes(CancellationErrorCode code, string key)
    {
        var original = AzureSpeechDiagnostics.Cancellation(code, "HTTP 400");
        var message = UiMessage.FromDiagnostic(original)!;
        Assert.AreEqual(key, message.Key);
        Assert.AreEqual(English.Format(key, $"Azure AI Speech [{code}/HTTP 400]"), message.Resolve(English));
        Assert.AreEqual(Chinese.Format(key, $"Azure AI Speech [{code}/HTTP 400]"), message.Resolve(Chinese));
    }

    [TestMethod]
    public void AzureTenantAndInitializationDiagnosticsKeepSafeCodes()
    {
        var tenant = UiMessage.FromDiagnostic(AzureSpeechDiagnostics.Cancellation(CancellationErrorCode.BadRequest,
            "HTTP 400 Tenant provided in token does not match resource token"))!;
        Assert.AreEqual("ErrorAzureTenantMismatch", tenant.Key);
        StringAssert.Contains(tenant.Resolve(English), "[TenantMismatch]");
        StringAssert.Contains(tenant.Resolve(English), "[BadRequest/HTTP 400]");
        var initialization = UiMessage.FromDiagnostic(AzureSpeechDiagnostics.Initialization(
            new InvalidOperationException("SPXERR_CONNECTION_FAILURE")))!;
        Assert.AreEqual("ErrorAzureSdkInit", initialization.Key);
        StringAssert.Contains(initialization.Resolve(English), "[SPXERR_CONNECTION_FAILURE]");
    }

    [TestMethod]
    public void EveryCardAndAnswerStatusIsLocalizedWithoutChangingColors()
    {
        foreach (var status in Enum.GetValues<QuestionStatus>())
        {
            var englishCard = TranscriptCardAppearance.For(status, ConversationLanguage.English);
            var chineseCard = TranscriptCardAppearance.For(status, ConversationLanguage.Chinese);
            Assert.AreEqual(englishCard.BackgroundArgb, chineseCard.BackgroundArgb);
            Assert.AreNotEqual(englishCard.StatusText, chineseCard.StatusText);
            Assert.IsFalse(Regex.IsMatch(englishCard.StatusText, @"\p{IsCJKUnifiedIdeographs}"));
            Assert.IsTrue(Regex.IsMatch(chineseCard.StatusText, @"\p{IsCJKUnifiedIdeographs}"));
            Assert.AreNotEqual(English["QuestionStatus" + status], Chinese["QuestionStatus" + status]);
            Assert.AreNotEqual(AnswerPresentation.Compose("", status, [], ConversationLanguage.English),
                AnswerPresentation.Compose("", status, [], ConversationLanguage.Chinese));
        }
    }

    [TestMethod]
    public void AnswerContentRemainsVerbatimWhileApplicationHeadingsChange()
    {
        const string answer = "## Work IQ 原始标题\nAn unchanged answer.";
        const string citation = "https://example.com/source";
        Assert.AreEqual(answer, AnswerPresentation.Compose(answer, QuestionStatus.Completed, [], ConversationLanguage.Chinese));
        var chinese = AnswerPresentation.Compose(answer, QuestionStatus.Completed, [citation], ConversationLanguage.Chinese);
        StringAssert.StartsWith(chinese, answer);
        StringAssert.Contains(chinese, "## " + Chinese["AdditionalSources"]);
        StringAssert.Contains(chinese, citation);
        var english = AnswerPresentation.Compose(answer, QuestionStatus.Completed, [citation], ConversationLanguage.English);
        StringAssert.StartsWith(english, answer);
        StringAssert.Contains(english, "## " + English["AdditionalSources"]);
        Assert.AreEqual(Chinese["AnswerSelect"], AnswerPresentation.Compose(null, null, [], ConversationLanguage.Chinese));
    }

    [TestMethod]
    public void ImageChromeAndDocumentLanguageFollowTheInterfaceWithoutLoadingRemoteContent()
    {
        const string markdown = "![Original 图片 caption](https://example.com/picture.png)";
        foreach (var texts in new[] { Chinese, English })
        {
            var rendered = new AnswerMarkdownFormatter().Format(markdown, language: texts.Language);
            var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(
                AnswerHtmlPage.Build(rendered, 42, texts.Language));
            Assert.AreEqual(texts.LanguageTag, document.DocumentElement.GetAttribute("lang"));
            StringAssert.Contains(document.Body!.TextContent, texts.Format("ImagePrefix", "Original 图片 caption"));
            StringAssert.Contains(document.Body.TextContent, texts.Format("ImageOpenHost", "example.com"));
            Assert.AreEqual(0, document.QuerySelectorAll("img").Length);
            Assert.AreEqual("Original 图片 caption", rendered.RemoteImages["https://example.com/picture.png"]);
            StringAssert.Contains(document.QuerySelector("meta[http-equiv]")!.GetAttribute("content")!, "script-src 'none'");
        }
    }

    [TestMethod]
    public void MainPageOwnsNoUnlocalizedControlLabels()
    {
        var page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainPage.xaml"));
        var textAttributes = new[] { "Text", "Content", "Header", "PlaceholderText", "OnContent", "OffContent",
            "ToolTipService.ToolTip", "AutomationProperties.Name", "AutomationProperties.HelpText" };
        foreach (var attribute in page.Descendants().Attributes()
            .Where(attribute => textAttributes.Contains(attribute.Name.LocalName)))
        {
            if (attribute.Value == "https://your-resource.cognitiveservices.azure.com/") continue;
            if (attribute.Value.StartsWith("{TemplateBinding ", StringComparison.Ordinal)) continue;
            StringAssert.StartsWith(attribute.Value, "{Binding ", attribute.ToString());
            var key = Regex.Match(attribute.Value, @"Texts\[(\w+)\]");
            if (key.Success)
                Assert.IsTrue(English.ContainsKey(key.Groups[1].Value) && Chinese.ContainsKey(key.Groups[1].Value),
                    attribute.ToString());
        }
    }
}
