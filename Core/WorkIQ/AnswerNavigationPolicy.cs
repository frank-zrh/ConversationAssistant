namespace ConversationAssistant.Core.WorkIQ;

public static class AnswerNavigationPolicy
{
    public static bool IsAppHtmlDocument(string uri, bool isUserInitiated, bool appRequested)
    {
        if (!appRequested || isUserInitiated) return false;
        var comma = uri.IndexOf(',');
        if (comma < 0) return false;
        var mediaType = uri[..comma];
        return mediaType.Equals("data:text/html", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("data:text/html;charset=utf-8", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("data:text/html;base64", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("data:text/html;charset=utf-8;base64", StringComparison.OrdinalIgnoreCase);
    }
}
