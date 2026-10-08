namespace ConversationAssistant_App.UI;

internal static class ConversationRecordLocation
{
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ConversationAssistant", "Conversations");
}
