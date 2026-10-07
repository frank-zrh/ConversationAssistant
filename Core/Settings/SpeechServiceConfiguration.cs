namespace ConversationAssistant.Core.Settings;

public enum SpeechProvider { AzureSpeech, OfflineWhisper }

public sealed class SpeechServiceConfiguration
{
    public SpeechProvider Provider { get; init; } = SpeechProvider.AzureSpeech;
    public string ServiceUri { get; init; } = "";
    public string TenantId { get; init; } = "";
    public string ClientId { get; init; } = "";
    public bool CloudAudioConsent { get; init; }

    public override string ToString() => $"Speech configuration: {Provider} (credentials redacted)";

    public void ValidateForAzure()
    {
        if (string.IsNullOrWhiteSpace(ServiceUri))
            throw new InvalidOperationException("请在 Settings 中保存 Azure AI 资源 Endpoint，然后点击 Microsoft 登录进行 Entra 授权。");
        var endpoint = AzureSpeechEndpoint.Parse(ServiceUri);
        if (!endpoint.IdnHost.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Entra 模式需要 Azure 公有云资源的自定义 Endpoint（*.cognitiveservices.azure.com），不是区域 Speech 主机。");
        ValidateIdentity();
        if (!CloudAudioConsent)
            throw new InvalidOperationException("请在 Settings 中确认允许将麦克风音频发送到 Azure Speech。");
    }

    public void ValidateIdentity()
    {
        if (Provider == SpeechProvider.AzureSpeech && string.IsNullOrWhiteSpace(TenantId))
            throw new InvalidOperationException(
                "请填写 Azure AI 资源所在目录的 Tenant ID，再保存并重新 Microsoft 登录；不能默认使用 Work IQ/M365 的租户。");
        if (TenantId.Length > 0 && (!Guid.TryParse(TenantId, out var tenant) || tenant == Guid.Empty))
            throw new InvalidOperationException("Tenant ID 应为 Azure AI 资源所在目录的 GUID，不是区域名称或 Client ID。");
        if (ClientId.Length > 0 && (!Guid.TryParse(ClientId, out var client) || client == Guid.Empty))
            throw new InvalidOperationException("Client ID 应为应用注册的 GUID；不能填写 Key 或客户端密码。");
    }

    public bool HasSameIdentity(SpeechServiceConfiguration other) =>
        string.Equals(TenantId, other.TenantId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ClientId, other.ClientId, StringComparison.OrdinalIgnoreCase);

    public static SpeechServiceConfiguration FromInput(
        SpeechProvider provider, string serviceUri, string tenantId, string clientId, bool consent)
    {
        if (!Enum.IsDefined(provider)) throw new ArgumentOutOfRangeException(nameof(provider));
        var uri = serviceUri.Trim();
        if (uri.Length > 0) uri = AzureSpeechEndpoint.Parse(uri).AbsoluteUri;
        var result = new SpeechServiceConfiguration
        {
            Provider = provider, ServiceUri = uri, TenantId = tenantId.Trim(), ClientId = clientId.Trim(),
            CloudAudioConsent = consent
        };
        result.ValidateIdentity();
        if (provider == SpeechProvider.AzureSpeech) result.ValidateForAzure();
        return result;
    }
}

public static class AzureSpeechEndpoint
{
    private static readonly string[] Suffixes =
    [
        ".speech.microsoft.com", ".cognitiveservices.azure.com",
        ".speech.azure.cn", ".cognitiveservices.azure.cn",
        ".speech.azure.us", ".cognitiveservices.azure.us"
    ];

    public static Uri Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "wss") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.Port != 443 || uri.IsLoopback ||
            !Suffixes.Any(suffix => uri.IdnHost.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "Endpoint 必须是 Azure AI Services / Speech 的 HTTPS/WSS 地址，不能包含 Key、查询参数或片段。");
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length > 0 &&
            path != "/speech/recognition/conversation/cognitiveservices/v1" &&
            path != "/stt/speech/recognition/conversation/cognitiveservices/v1")
            throw new InvalidOperationException(
                "请粘贴 Azure AI Services / Speech 资源的根 Endpoint，或完整实时识别地址；不支持批量转写/翻译的 REST 地址。");
        return uri;
    }

    public static bool IsHostOnly(Uri uri) => uri.AbsolutePath is "" or "/";

    public static Uri GetSdkUri(string value)
    {
        var portalUri = Parse(value);
        // Let ConversationTranscriber select its transport instead of pinning a saved v1 recognition path.
        return new UriBuilder(portalUri) { Scheme = "https", Port = -1, Path = "/" }.Uri;
    }

    public static bool AreEquivalent(string first, string second) =>
        GetSdkUri(first).Equals(GetSdkUri(second));
}
