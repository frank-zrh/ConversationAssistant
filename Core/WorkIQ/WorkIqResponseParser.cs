using System.Text.Json;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.WorkIQ;

public static class WorkIqResponseParser
{
    public static WorkIqAnswer Parse(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new WorkIqException("Work IQ returned an unexpected JSON response.");
            if (root.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
                throw new WorkIqException("Work IQ could not answer this question. Retry or check your permissions.");
            if (!root.TryGetProperty("response", out var response) ||
                response.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(response.GetString()))
                throw new WorkIqException("Work IQ returned no answer.");
            var id = root.TryGetProperty("conversationId", out var conversation) &&
                conversation.ValueKind == JsonValueKind.String ? conversation.GetString() : null;
            var sources = new List<string>();
            if (root.TryGetProperty("sources", out var sourceArray) && sourceArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var source in sourceArray.EnumerateArray())
                {
                    if (source.ValueKind == JsonValueKind.String) sources.Add(source.GetString()!);
                    else if (source.ValueKind == JsonValueKind.Object &&
                        source.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                        sources.Add(title.GetString()!);
                }
            }
            return new WorkIqAnswer(response.GetString()!, sources, id);
        }
        catch (JsonException ex)
        {
            throw new WorkIqException("Work IQ returned invalid JSON. Update the official CLI.", ex);
        }
    }
}
