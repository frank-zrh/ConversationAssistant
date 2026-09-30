using System.Net.NetworkInformation;
using ConversationAssistant.Core.Conversation;

namespace ConversationAssistant_App.WorkIQ;

public sealed class NetworkStatus : INetworkStatus
{
    public bool IsAvailable => NetworkInterface.GetIsNetworkAvailable();
}
