using ConversationAssistant.Core.Qwen;
using ConversationAssistant_App;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class LocalAppProfileTests
{
    [TestMethod]
    public void FirstRun_CreatesApplicationAndBrowserFoldersBelowLocalAppData()
    {
        using var sandbox = new ProfileSandbox();
        var profile = new LocalAppProfile(sandbox.LocalAppData);

        profile.EnsureCreated();
        var browserDirectory = profile.EnsureBrowserDataDirectory();

        var expectedRoot = Path.Combine(sandbox.LocalAppData, "ConversationAssistant");
        Assert.AreEqual(expectedRoot, profile.Root);
        Assert.AreEqual(Path.Combine(expectedRoot, "Conversations"), profile.Conversations);
        Assert.AreEqual(Path.Combine(expectedRoot, "qwen-settings.bin"), profile.QwenSettingsFile);
        Assert.AreEqual(Path.Combine(expectedRoot, "WebView2"), browserDirectory);
        Assert.IsTrue(Directory.Exists(profile.Conversations));
        Assert.IsTrue(Directory.Exists(browserDirectory));
        Assert.IsFalse(File.Exists(profile.QwenSettingsFile));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Reopen_ReusesExistingKeyRecordAndBrowserFolder()
    {
        using var sandbox = new ProfileSandbox();
        var previousRoot = Path.Combine(sandbox.LocalAppData, "ConversationAssistant");
        var recordDirectory = Directory.CreateDirectory(Path.Combine(previousRoot, "Conversations"));
        var existingRecord = Path.Combine(recordDirectory.FullName, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(existingRecord, "existing record");
        var store = new ProtectedQwenSettingsStore(Path.Combine(previousRoot, "qwen-settings.bin"));
        store.Save(new QwenSettings { ApiKey = QwenAnalysisTests.WorkspaceTestKey });
        var browserDirectory = new LocalAppProfile(sandbox.LocalAppData).EnsureBrowserDataDirectory();
        var browserMarker = Path.Combine(browserDirectory, "browser-profile-test.txt");
        File.WriteAllText(browserMarker, "existing browser data");

        var reopened = new LocalAppProfile(sandbox.LocalAppData);
        reopened.EnsureCreated();

        Assert.AreEqual(browserDirectory, reopened.EnsureBrowserDataDirectory());
        Assert.AreEqual("existing browser data", File.ReadAllText(browserMarker));
        Assert.AreEqual("existing record", File.ReadAllText(Path.Combine(reopened.Conversations, Path.GetFileName(existingRecord))));
        Assert.AreEqual(QwenAnalysisTests.WorkspaceTestKey, new ProtectedQwenSettingsStore(reopened.QwenSettingsFile).Load().RequireApiKey());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("relative-folder")]
    public void MissingAbsoluteLocalAppData_DoesNotFallBackToWorkingDirectory(string folder)
    {
        Assert.ThrowsExactly<DirectoryNotFoundException>(() => new LocalAppProfile(folder));
    }

    private sealed class ProfileSandbox : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("conversation-profile-tests-");
        public string LocalAppData => Path.Combine(_root.FullName, "AppData", "Local");
        public void Dispose() => _root.Delete(recursive: true);
    }
}