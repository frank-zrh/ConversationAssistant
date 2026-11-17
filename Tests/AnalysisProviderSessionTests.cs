using ConversationAssistant.Core.Analysis;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Tests;

public sealed partial class PipelineTests
{
    [TestMethod]
    public async Task ProviderChangeRetainsHistoryButClearsRemoteContextAndCannotOccurWhileListening()
    {
        var fake = new Harness();
        fake.Conversation.StartConversation(new ConversationSettings { AutomaticAnalysis = false });
        try
        {
            var first = fake.Conversation.AskManually("What is the policy?");
            await WaitUntil(() => first.Status == QuestionStatus.Completed);
            Assert.IsNotNull(fake.Conversation.CurrentConversation!.WorkIqConversationId);
            Assert.ThrowsExactly<InvalidOperationException>(() => fake.Conversation.UpdateWorkspacePreferences(
                new ConversationSettings { AnalysisProvider = AnalysisProvider.Copilot }));
            await fake.Conversation.EndConversationAsync();
            fake.Conversation.UpdateWorkspacePreferences(new ConversationSettings { AnalysisProvider = AnalysisProvider.Copilot });
            Assert.IsNull(fake.Conversation.CurrentConversation!.WorkIqConversationId);
            Assert.AreEqual(AnalysisProvider.Copilot, fake.Conversation.CurrentConversation.Settings.AnalysisProvider);
            Assert.HasCount(1, fake.Conversation.AnswerHistory);
            var second = fake.Conversation.AskManually("Which policy applies next?");
            await WaitUntil(() => second.Status == QuestionStatus.Completed);
            Assert.IsNull(fake.Work.Calls.Last().ConversationId);
        }
        finally { await fake.Conversation.EndConversationAsync(); }
    }
}
