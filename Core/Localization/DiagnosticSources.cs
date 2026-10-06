namespace ConversationAssistant.Core.Localization;

internal static class DiagnosticSources
{
    // Keep exception contracts and diagnostics unchanged while translating them at the UI boundary.
    public static readonly (string Key, string Text)[] Legacy =
    [
        ("ErrorAccountMatch", "无法将 Entra 帐号匹配到 Work IQ。请使用组织工作帐号重新登录。"),
        ("ErrorAuthNoAccount", "Entra 登录未返回可匹配的工作帐号，Work IQ 登录未继续。"),
        ("ErrorDecrypt", "无法解密当前用户的配置。请使用原 Windows 用户，或重新登录/保存设置。"),
        ("ErrorSpeechSettingsInvalid", "Speech 配置无效，请重新填写并保存。"),
        ("ErrorSpeechSettingsFormat", "Speech 配置格式无效，请重新填写并保存。"),
        ("ErrorEndpointMissing", "请在 Settings 中保存 Azure AI 资源 Endpoint，然后点击 Microsoft 登录进行 Entra 授权。"),
        ("ErrorEndpointHost", "Entra 模式需要 Azure 公有云资源的自定义 Endpoint（*.cognitiveservices.azure.com），不是区域 Speech 主机。"),
        ("ErrorCloudConsent", "请在 Settings 中确认允许将麦克风音频发送到 Azure Speech。"),
        ("ErrorTenantMissing", "请填写 Azure AI 资源所在目录的 Tenant ID，再保存并重新 Microsoft 登录；不能默认使用 Work IQ/M365 的租户。"),
        ("ErrorTenantInvalid", "Tenant ID 应为 Azure AI 资源所在目录的 GUID，不是区域名称或 Client ID。"),
        ("ErrorClientInvalid", "Client ID 应为应用注册的 GUID；不能填写 Key 或客户端密码。"),
        ("ErrorEndpointInvalid", "Endpoint 必须是 Azure AI Services / Speech 的 HTTPS/WSS 地址，不能包含 Key、查询参数或片段。"),
        ("ErrorEndpointPath", "请粘贴 Azure AI Services / Speech 资源的根 Endpoint，或完整实时识别地址；不支持批量转写/翻译的 REST 地址。"),
        ("ErrorSpeechStartup", "Speech 服务在启动期间断开，请重试。"),
        ("ErrorSpeechResume", "Speech 服务在恢复期间断开，请重试。"),
        ("ErrorAzureConnectTimeout", "连接 Azure Speech 超时。请检查 Endpoint、Entra 登录、资源角色和网络，然后重试。"),
        ("ErrorAzureStopTimeout", "停止 Azure Speech 超时，连接已释放。"),
        ("ErrorAzureAudioDisconnected", "Azure Speech 音频连接不可用。请恢复监听以重新连接。"),
        ("ErrorAzureStopFailed", "Azure Speech 停止时发生错误，连接将释放。"),
        ("ErrorAzureSelect", "请先选择 Azure AI Services / Speech 并保存。"),
        ("ErrorAzureConnectionTimeout", "Azure AI Speech [ConnectionTimeout] 20 秒内未能建立连接。请检查 Endpoint、Entra 登录、资源角色及网络访问规则。"),
        ("ErrorEntraMismatch", "Entra 返回的帐号租户不符合配置的资源 Tenant ID，请核对目录并重新登录。"),
        ("ErrorEntraCache", "Entra 帐号缓存格式无效。请关闭 Conversation Assistant 并清除应用的 entra-account 缓存后重新登录。"),
        ("ErrorEntraSignInRequired", "请先点击 Microsoft 登录，完成 Azure Entra 授权；不能使用 Work IQ 令牌或旧 Key 代替。"),
        ("ErrorEntraFailure", "Azure Entra 登录失败{0}。请检查租户、应用 Client ID、管理员同意及条件访问策略；未配置 Client ID 时使用的 SDK 开发客户端可能被组织限制。"),
        ("ErrorEntraScope", "拒绝为非 Azure Cognitive Services 范围获取 Speech 令牌。"),
        ("ErrorAzureTenantMismatch", "{0} [TenantMismatch] Entra 令牌的租户与 Azure 资源所属目录不一致。请在 Settings 将 Tenant ID 填为该 Azure AI 资源所在订阅的目录 ID，保存后重新 Microsoft 登录。它不一定等于 Work IQ/M365 的租户；资源目录还需为该帐号授予 Speech User 角色。"),
        ("ErrorAzureAuth", "{0} Entra 认证失败：请重新登录并确认令牌属于 Azure Cognitive Services；不会回退到资源 Key。"),
        ("ErrorAzureForbidden", "{0} 访问被拒绝：请确认当前登录用户在该资源具有 Cognitive Services Speech User 或 Cognitive Services Speech Contributor 角色，并检查租户和网络访问规则。"),
        ("ErrorAzureBadRequest", "{0} 服务拒绝了请求，不能仅凭 HTTP 400 判定 Endpoint 填错。请核对资源 Tenant ID、Entra 授权及识别参数。"),
        ("ErrorAzureThrottled", "{0} 服务限流：请检查 Speech 配额，稍后再连接。"),
        ("ErrorAzureConnection", "{0} 连接不可用：请检查 Endpoint、DNS、代理、防火墙及 WebSocket 网络访问。"),
        ("ErrorAzureService", "{0} 服务返回错误：请确认此 Azure AI Services 资源支持 Speech，再检查资源状态及网络配置。"),
        ("ErrorAzureStopped", "{0} 会话停止：请检查 Endpoint、Entra 授权和资源配置，再尝试连接。"),
        ("ErrorAzureSdkInit", "Azure AI Speech [{0}] SDK 初始化或连接失败；请检查 Endpoint、Entra 授权和本机运行时。")
    ];
}
