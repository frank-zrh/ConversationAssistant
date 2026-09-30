using System.Text;
using ConversationAssistant.Core.Settings;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class AzureSpeechSettingsTests
{
    private static SpeechServiceConfiguration Example() => new()
    {
        ServiceUri = "https://conversation-demo.cognitiveservices.azure.com/",
        TenantId = "11111111-1111-1111-1111-111111111111",
        ClientId = "22222222-2222-2222-2222-222222222222",
        CloudAudioConsent = true
    };

    [TestMethod]
    [DataRow("https://stttest1.cognitiveservices.azure.com/")]
    [DataRow("wss://stttest1.cognitiveservices.azure.com/")]
    public void EntraUsesCustomResourceEndpointInsteadOfSubscriptionKey(string value)
    {
        Assert.AreEqual("https://stttest1.cognitiveservices.azure.com/",
            AzureSpeechEndpoint.GetSdkUri(value).AbsoluteUri);
        new SpeechServiceConfiguration
        {
            ServiceUri = value, CloudAudioConsent = true,
            TenantId = "11111111-1111-1111-1111-111111111111"
        }.ValidateForAzure();
    }

    [TestMethod]
    [DataRow("http://conversation-demo.cognitiveservices.azure.com")]
    [DataRow("https://example.com")]
    [DataRow("https://conversation-demo.cognitiveservices.azure.com.evil.example")]
    [DataRow("https://localhost")]
    [DataRow("https://conversation-demo.cognitiveservices.azure.com?language=en-US")]
    [DataRow("https://conversation-demo.cognitiveservices.azure.com#key")]
    [DataRow("https://conversation-demo.cognitiveservices.azure.com/speechtotext/v3.2/transcriptions")]
    public void InvalidEndpointsDoNotReceiveCredentials(string uri) =>
        Assert.ThrowsExactly<InvalidOperationException>(() => AzureSpeechEndpoint.Parse(uri));

    [TestMethod]
    public void EntraRejectsRegionalHostAndInvalidIdentityConfiguration()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => new SpeechServiceConfiguration
        {
            ServiceUri = "https://eastasia.stt.speech.microsoft.com/", CloudAudioConsent = true
        }.ValidateForAzure());
        Assert.ThrowsExactly<InvalidOperationException>(() => new SpeechServiceConfiguration
        { TenantId = "not-a-guid" }.ValidateIdentity());
        Assert.ThrowsExactly<InvalidOperationException>(() => new SpeechServiceConfiguration
        { ClientId = "never-put-a-secret-here" }.ValidateIdentity());
        var saved = Example();
        var updated = SpeechServiceConfiguration.FromInput(SpeechProvider.AzureSpeech,
            saved.ServiceUri, saved.TenantId, saved.ClientId, true);
        Assert.IsTrue(saved.HasSameIdentity(updated));
        Assert.ThrowsExactly<InvalidOperationException>(() => SpeechServiceConfiguration.FromInput(
            SpeechProvider.AzureSpeech, saved.ServiceUri, "", "", false));
    }

    [TestMethod]
    public void ResourceTenantIsRequiredForAzureButNotForOfflineMode()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => new SpeechServiceConfiguration
        {
            ServiceUri = "https://stttest1.cognitiveservices.azure.com/", CloudAudioConsent = true
        }.ValidateForAzure());
        new SpeechServiceConfiguration { Provider = SpeechProvider.OfflineWhisper }.ValidateIdentity();
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new SpeechServiceConfiguration { TenantId = "eastus" }.ValidateIdentity());
    }

    [TestMethod]
    public void EncryptedSettingsRoundTripAndLegacyKeyIsNeverUsedOrResaved()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ConversationAssistant-entra-test-" + Guid.NewGuid());
        var path = Path.Combine(folder, "speech.bin");
        try
        {
            var store = new ProtectedSpeechSettingsStore(path);
            Assert.AreEqual(SpeechProvider.AzureSpeech, store.Load().Provider);
            var original = Example();
            store.Save(original);
            Assert.IsFalse(Encoding.UTF8.GetString(File.ReadAllBytes(path))
                .Contains(original.ServiceUri, StringComparison.Ordinal));
            var reopened = new ProtectedSpeechSettingsStore(path).Load();
            Assert.IsTrue(original.HasSameIdentity(reopened));
            Assert.AreEqual(original.ServiceUri, reopened.ServiceUri);
            Assert.IsTrue(reopened.CloudAudioConsent);

            // Verify that files protected before the product rename remain readable.
            var protectedFile = new CurrentUserProtectedFile(path, "MeetingCopilot.AzureSpeech.Settings.v1");
            protectedFile.Write(Encoding.UTF8.GetBytes("""
                {"Provider":0,"ServiceUri":"https://old.cognitiveservices.azure.com/",
                 "ApiKey":"legacy-synthetic-key-must-not-be-reused","CloudAudioConsent":true}
                """));
            var legacy = store.Load();
            Assert.ThrowsExactly<InvalidOperationException>(() => legacy.ValidateForAzure());
            Assert.AreEqual("", legacy.ClientId);
            var migrated = SpeechServiceConfiguration.FromInput(SpeechProvider.AzureSpeech,
                legacy.ServiceUri, original.TenantId, "", legacy.CloudAudioConsent);
            store.Save(migrated);
            var json = Encoding.UTF8.GetString(protectedFile.Read()!);
            Assert.IsFalse(json.Contains("ApiKey", StringComparison.Ordinal));
            Assert.IsFalse(json.Contains("legacy-synthetic", StringComparison.Ordinal));
            Assert.HasCount(1, Directory.GetFiles(folder));
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.ThrowsExactly<InvalidDataException>(() => store.Load());
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }
}
