using System.Xml.Linq;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class SelfContainedDeploymentTests
{
    private static XDocument Load(string file) => XDocument.Load(Path.Combine(AppContext.BaseDirectory, file));

    [TestMethod]
    public void ClickOnceBundlesRuntimesWithoutSoftwareBootstrapper()
    {
        var profile = Load("ClickOnceProfile.xml");
        Assert.IsTrue(profile.Descendants("SelfContained").Any(item => item.Value.Equals("true", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(profile.Descendants("WindowsAppSDKSelfContained").Any(item => item.Value.Equals("true", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual("x64", profile.Descendants("Platform").Single().Value);
        Assert.AreEqual("False", profile.Descendants("BootstrapperEnabled").Single().Value);
        Assert.IsFalse(profile.Descendants("BootstrapperPackage").Any());
    }

    [TestMethod]
    public void AppHasNoCloudSpeechSapiCliOrGpuRuntimeDependency()
    {
        var project = Load("AppProject.xml");
        var packages = project.Descendants("PackageReference").Select(item => (string)item.Attribute("Include")!).ToArray();
        foreach (var removed in new[] { "Microsoft.CognitiveServices.Speech", "System.Speech", "Whisper.net.Runtime.Vulkan" })
            Assert.DoesNotContain(removed, packages);
        Assert.Contains("Whisper.net.Runtime", packages);
        Assert.Contains("Whisper.net.Runtime.NoAvx", packages);
        Assert.IsTrue(project.Descendants("Target").Any(item => (string?)item.Attribute("Name") == "ValidateDeploymentPayload"));
        Assert.IsTrue(project.Descendants("Content").Any(item => ((string?)item.Attribute("Include"))?.Contains("Runtime\\VC", StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public void RuntimeCompositionIsWhisperAndHostedWorkIqOnly()
    {
        var composition = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AppComposition.txt"));
        StringAssert.Contains(composition, "ISpeechRecognitionService, OfflineWhisperSpeechRecognitionService");
        StringAssert.Contains(composition, "new WorkIqHttpClient");
        foreach (var removed in new[] { "WorkIqProcess", "AzureSpeech", "SpeechEntraIdentity", "CopilotChatClient", "ISpeechSettingsStore" })
            Assert.IsFalse(composition.Contains(removed, StringComparison.Ordinal), removed);
    }

    [TestMethod]
    public void PageHasNativeAnswerViewAndNoAzureOrProviderSetupControls()
    {
        var page = Load("MainPage.xaml");
        Assert.IsTrue(page.Descendants().Any(item => item.Name.LocalName == "NativeAnswerView"));
        Assert.IsFalse(page.Descendants().Any(item => item.Name.LocalName == "WebView2"));
        var text = page.ToString();
        foreach (var removed in new[] { "SpeechServiceUri", "SpeechTenantId", "SpeechClientId", "CloudAudioConsent", "AnalysisProviderPicker", "CopilotClientId" })
            Assert.IsFalse(text.Contains(removed, StringComparison.Ordinal), removed);
    }
}
