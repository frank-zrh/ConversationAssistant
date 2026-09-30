using System.Text.RegularExpressions;
using Microsoft.CognitiveServices.Speech;

namespace ConversationAssistant_App.Speech;

internal static partial class AzureSpeechDiagnostics
{
    [GeneratedRegex(@"\b(?:HTTP(?:\s+status)?|status(?:\s+code)?|response(?:\s+code)?|authentication\s+error)\s*[:=(]?\s*(400|401|403|404|408|429|500|502|503|504)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpCode();

    [GeneratedRegex(@"\bSPXERR_[A-Z0-9_]{1,64}\b", RegexOptions.CultureInvariant)]
    private static partial Regex NativeCode();

    public static string Cancellation(CancellationErrorCode code, string? details)
    {
        var status = HttpCode().Match(details ?? "");
        var label = Enum.IsDefined(code) ? code.ToString() : "Unknown";
        var prefix = $"Azure AI Speech [{label}" +
            (status.Success ? $"/HTTP {status.Groups[1].Value}" : "") + "]";
        if (IsTenantMismatch(details))
            return prefix + " [TenantMismatch] Entra 令牌的租户与 Azure 资源所属目录不一致。" +
                "请在 Settings 将 Tenant ID 填为该 Azure AI 资源所在订阅的目录 ID，保存后重新 Microsoft 登录。" +
                "它不一定等于 Work IQ/M365 的租户；资源目录还需为该帐号授予 Speech User 角色。";
        var guidance = code switch
        {
            CancellationErrorCode.AuthenticationFailure =>
                "Entra 认证失败：请重新登录并确认令牌属于 Azure Cognitive Services；不会回退到资源 Key。",
            CancellationErrorCode.Forbidden =>
                "访问被拒绝：请确认当前登录用户在该资源具有 Cognitive Services Speech User 或 Cognitive Services Speech Contributor 角色，并检查租户和网络访问规则。",
            CancellationErrorCode.BadRequest =>
                "服务拒绝了请求，不能仅凭 HTTP 400 判定 Endpoint 填错。请核对资源 Tenant ID、Entra 授权及识别参数。",
            CancellationErrorCode.TooManyRequests =>
                "服务限流：请检查 Speech 配额，稍后再连接。",
            CancellationErrorCode.ConnectionFailure or CancellationErrorCode.ServiceTimeout or
                CancellationErrorCode.ServiceUnavailable =>
                "连接不可用：请检查 Endpoint、DNS、代理、防火墙及 WebSocket 网络访问。",
            CancellationErrorCode.ServiceError =>
                "服务返回错误：请确认此 Azure AI Services 资源支持 Speech，再检查资源状态及网络配置。",
            _ => "会话停止：请检查 Endpoint、Entra 授权和资源配置，再尝试连接。"
        };
        return prefix + " " + guidance;
    }

    internal static bool IsTenantMismatch(string? details)
    {
        if (string.IsNullOrWhiteSpace(details)) return false;
        return details.Contains("Tenant provided in token does not match resource token", StringComparison.OrdinalIgnoreCase) ||
            details.Contains("does not match resource tenant", StringComparison.OrdinalIgnoreCase);
    }

    public static string Initialization(Exception error)
    {
        var native = NativeCode().Match(error.Message);
        var code = native.Success ? native.Value : $"HRESULT 0x{error.HResult:X8}";
        return $"Azure AI Speech [{code}] SDK 初始化或连接失败；请检查 Endpoint、Entra 授权和本机运行时。";
    }
}
