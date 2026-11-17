using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.WorkIQ;

namespace ConversationAssistant.Tests;

[TestClass]
public sealed class WorkIqExecutableLocatorTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Work IQ locator tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void NpmCmdOnPathResolvesBundledNativeExecutable()
    {
        var prefix = Path.Combine(_root, "npm");
        WriteFile(Path.Combine(prefix, "workiq.cmd"));
        var native = WriteNpmBinary(prefix);
        Assert.AreEqual(native, Resolve(paths: [prefix]));
    }

    [TestMethod]
    public void ExplicitCmdOverrideResolvesNativeWithoutExecutingCmdContents()
    {
        var prefix = Path.Combine(_root, "custom npm prefix");
        var launcher = WriteFile(Path.Combine(prefix, "workiq.cmd"), "exit /b 99 & echo must never run");
        var native = WriteNpmBinary(prefix);
        Assert.AreEqual(native, Resolve(configured: launcher));
    }

    [TestMethod]
    public void DefaultUserNpmPrefixWorksWithoutRefreshedPath()
    {
        var appData = Path.Combine(_root, "Roaming");
        var native = WriteNpmBinary(Path.Combine(appData, "npm"));
        Assert.AreEqual(native, Resolve(appData: appData));
    }

    [TestMethod]
    public void QuotedNpmPathWithSpacesIsSupported()
    {
        var prefix = Path.Combine(_root, "npm prefix with spaces");
        var native = WriteNpmBinary(prefix);
        Assert.AreEqual(native, Resolve(paths: [$"\"{prefix}\""]));
    }

    [TestMethod]
    public void LocalNpmBinLauncherResolvesSiblingPackage()
    {
        var nodeModules = Path.Combine(_root, "project", "node_modules");
        var bin = Path.Combine(nodeModules, ".bin");
        var launcher = WriteFile(Path.Combine(bin, "workiq.cmd"));
        var native = WriteFile(Path.Combine(nodeModules, "@microsoft", "workiq", "bin", "win-x64", "workiq.exe"));
        Assert.AreEqual(native, Resolve(paths: [bin]));
        Assert.AreEqual(native, Resolve(configured: launcher));
    }

    [TestMethod]
    public void DirectExecutableOnPathStillWorks()
    {
        var directory = Path.Combine(_root, "native");
        var native = WriteFile(Path.Combine(directory, "workiq.exe"));
        Assert.AreEqual(native, Resolve(paths: [directory]));
    }

    [TestMethod]
    public void ExplicitNativeAndLegacyOverridesTakePrecedence()
    {
        var native = WriteFile(Path.Combine(_root, "configured", "workiq.exe"));
        var legacy = WriteFile(Path.Combine(_root, "legacy", "workiq.exe"));
        Assert.AreEqual(native, Resolve(configured: native, legacy: legacy));
        Assert.AreEqual(legacy, Resolve(legacy: legacy));
    }

    [TestMethod]
    public void MissingNativeDoesNotFallBackToExecutingCmd()
    {
        var prefix = Path.Combine(_root, "incomplete");
        var launcher = WriteFile(Path.Combine(prefix, "workiq.cmd"));
        var error = Assert.ThrowsExactly<WorkIqException>(() => Resolve(configured: launcher));
        StringAssert.Contains(error.Message, "npm.cmd install --global @microsoft/workiq");
        Assert.ThrowsExactly<WorkIqException>(() => Resolve(paths: [prefix]));
    }

    [TestMethod]
    public void InvalidOverrideIsNotSilentlyIgnored()
    {
        var native = WriteFile(Path.Combine(_root, "on-path", "workiq.exe"));
        Assert.ThrowsExactly<WorkIqException>(() =>
            Resolve(configured: Path.Combine(_root, "missing.exe"), paths: [Path.GetDirectoryName(native)!]));
        Assert.ThrowsExactly<WorkIqException>(() => Resolve(configured: "workiq.cmd"));
    }

    [TestMethod]
    public void Arm64BinaryIsNotSelectedForX64Application()
    {
        var prefix = Path.Combine(_root, "npm");
        WriteFile(Path.Combine(prefix, "node_modules", "@microsoft", "workiq", "bin", "win-arm64", "workiq.exe"));
        Assert.ThrowsExactly<WorkIqException>(() => Resolve(paths: [prefix]));
        var x64 = WriteNpmBinary(prefix);
        Assert.AreEqual(x64, Resolve(paths: [prefix]));
    }

    [TestMethod]
    public void ScoutFallbackIsPreserved()
    {
        var local = Path.Combine(_root, "Local");
        var native = WriteFile(Path.Combine(local, "Programs", "scout", "resources", "app.asar.unpacked",
            "node_modules", "@microsoft", "workiq", "bin", "win-x64", "workiq.exe"));
        Assert.AreEqual(native, Resolve(localAppData: local));
    }

    [TestMethod]
    public void RelativePathEntriesAreNotSearched()
    {
        Assert.ThrowsExactly<WorkIqException>(() => Resolve(paths: ["", ".", "node_modules/.bin"]));
    }

    private static string Resolve(string? configured = null, string? legacy = null,
        string[]? paths = null, string? appData = null, string? localAppData = null) =>
        WorkIqExecutableLocator.Resolve(configured, legacy, paths ?? [], appData, localAppData);

    private static string WriteNpmBinary(string prefix) => WriteFile(
        Path.Combine(prefix, "node_modules", "@microsoft", "workiq", "bin", "win-x64", "workiq.exe"));

    private static string WriteFile(string path, string content = "test fixture, never executed")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
