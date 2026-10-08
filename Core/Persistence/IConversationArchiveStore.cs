namespace ConversationAssistant.Core.Persistence;

public interface IConversationArchiveStore
{
    string GetPath(Guid sessionId);
    Task<string> SaveAsync(ConversationArchive archive, CancellationToken cancellationToken = default);
    Task<ConversationArchive> LoadAsync(string path, CancellationToken cancellationToken = default);
}
