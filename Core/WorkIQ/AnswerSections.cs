namespace ConversationAssistant.Core.WorkIQ;

public sealed record AnswerSections(string SuggestedAnswer, string KeyPoints, string Sources,
    string UnderstoodQuestion = "")
{
    public static AnswerSections Parse(string answer)
    {
        var suggested = new List<string>();
        var understood = new List<string>();
        var points = new List<string>();
        var sources = new List<string>();
        var target = suggested;
        foreach (var line in answer.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var heading = line.Trim().Trim('#', '*', ':', ' ').ToUpperInvariant();
            if (heading is "UNDERSTOOD QUESTION" or "理解后的问题" or "重构后的问题")
            { target = understood; continue; }
            if (heading == "SUGGESTED ANSWER") { target = suggested; continue; }
            if (heading == "KEY POINTS") { target = points; continue; }
            if (heading is "SOURCES / CONTEXT" or "SOURCES" or "CONTEXT")
            { target = sources; continue; }
            target.Add(line);
        }
        return new(string.Join(Environment.NewLine, suggested).Trim(),
            string.Join(Environment.NewLine, points).Trim(),
            string.Join(Environment.NewLine, sources).Trim(),
            string.Join(Environment.NewLine, understood).Trim());
    }
}
