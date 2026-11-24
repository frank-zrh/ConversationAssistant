namespace ConversationAssistant_App;

internal sealed class LocalAppProfile
{
    public LocalAppProfile(string localAppData)
    {
        if (!Path.IsPathFullyQualified(localAppData))
            throw new DirectoryNotFoundException("Windows did not provide an absolute Local AppData path.");
        Root = Path.Combine(Path.GetFullPath(localAppData), "ConversationAssistant");
    }

    public string Root { get; }
    public string Conversations => Path.Combine(Root, "Conversations");
    public string QwenSettingsFile => Path.Combine(Root, "qwen-settings.bin");

    public static LocalAppProfile ForCurrentUser() => new(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify));

    public void EnsureCreated() => Directory.CreateDirectory(Conversations);

    public string EnsureBrowserDataDirectory() => Directory.CreateDirectory(Path.Combine(Root, "WebView2")).FullName;
}