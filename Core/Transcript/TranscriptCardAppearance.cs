using ConversationAssistant.Core.Models;

namespace ConversationAssistant.Core.Transcript;

public sealed record TranscriptCardAppearance(uint BackgroundArgb, string StatusText)
{
    public static TranscriptCardAppearance For(QuestionStatus? status) => status switch
    {
        QuestionStatus.Completed => new(0xFFE4F4E7, "已有回复 · 点击查看"),
        QuestionStatus.Pending => new(0xFFFFF4CC, "等待回复 · 已进入队列"),
        QuestionStatus.Processing => new(0xFFFFF4CC, "正在获取回复"),
        QuestionStatus.Failed => new(0xFFEADDD3, "获取失败 · 点击重试"),
        QuestionStatus.Cancelled => new(0xFFFFFFFF, "已取消 · 点击重新获取"),
        QuestionStatus.Ignored => new(0xFFFFFFFF, "未获取回复 · 点击获取"),
        _ => new(0xFFFFFFFF, "未获取回复 · 点击获取")
    };
}
