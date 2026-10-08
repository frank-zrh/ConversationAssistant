namespace ConversationAssistant.Core.Persistence;

public static class ConversationArchiveLimits
{
    public const long MaximumFileBytes = 64L * 1024 * 1024;
    public const int MaximumTextLength = 1024 * 1024;
    public const int MaximumSourceLength = 16 * 1024;
    public const int MaximumGroupNameLength = 120;
    public const int MaximumListLength = 100_000;
    public const int MaximumTotalItems = 500_000;
    public const int MaximumJsonDepth = 32;
}
