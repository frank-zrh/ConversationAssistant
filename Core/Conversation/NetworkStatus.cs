using System.Net.NetworkInformation;

namespace ConversationAssistant.Core.Conversation;

public sealed class NetworkStatus : INetworkStatus
{
    public bool IsAvailable => NetworkInterface.GetIsNetworkAvailable();
}