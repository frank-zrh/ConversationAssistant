namespace ConversationAssistant.Core.Authentication;

public static class WorkIqAccountArguments
{
    public static IReadOnlyList<string> WithAccount(IReadOnlyList<string> arguments, string? account)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (account is null) return arguments;
        if (account.Length > 320 || !account.Contains('@') || account.Any(char.IsWhiteSpace) ||
            account.Any(char.IsControl))
            throw new InvalidOperationException("无法将 Entra 帐号匹配到 Work IQ。请使用组织工作帐号重新登录。");
        if (arguments.Contains("--account"))
            throw new InvalidOperationException("Work IQ 帐号不能被请求参数覆盖。");
        return [.. arguments, "--account", account];
    }
}
