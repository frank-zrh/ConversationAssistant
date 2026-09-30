using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using ConversationAssistant.Core.Settings;
using ConversationAssistant.Core.Speech;

namespace ConversationAssistant_App.Authentication;

public interface ISpeechCredentialProvider
{
    TokenCredential GetCredential(SpeechServiceConfiguration settings);
}

public sealed partial class SpeechEntraIdentity(ISpeechSettingsStore settings) : ISpeechCredentialProvider
{
    public const string CognitiveScope = "https://cognitiveservices.azure.com/.default";
    private readonly SemaphoreSlim _signInGate = new(1);
    private TokenCredential? _credential;
    private SpeechServiceConfiguration? _identitySettings;
    public bool IsSignedIn { get; private set; }
    public string? AccountName { get; private set; }
    public event Action? StateChanged;

    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        await _signInGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Invalidate();
            var configuration = settings.Load();
            configuration.ValidateIdentity();
            var cacheId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                configuration.TenantId.ToLowerInvariant() + "|" + configuration.ClientId.ToLowerInvariant())))[..20];
            // Retain the original path and DPAPI purpose for existing sign-in records.
            var recordFile = new CurrentUserProtectedFile(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MeetingCopilot", $"entra-account-{cacheId}.bin"), "MeetingCopilot.Speech.EntraAccount.v1");
            AuthenticationRecord? record = null;
            var plaintext = recordFile.Read();
            if (plaintext is not null)
            {
                try
                {
                    using var memory = new MemoryStream(plaintext);
                    record = AuthenticationRecord.Deserialize(memory, cancellationToken);
                }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
            }

            var options = CreateOptions(configuration, record, cacheId);
            var browser = new InteractiveBrowserCredential(options);
            var context = new TokenRequestContext([CognitiveScope]);
            if (record is not null)
            {
                try { await browser.GetTokenAsync(context, cancellationToken).ConfigureAwait(false); }
                catch (AuthenticationRequiredException)
                {
                    record = await browser.AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                record = await browser.AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);
            }

            if (!string.Equals(record.TenantId, configuration.TenantId, StringComparison.OrdinalIgnoreCase))
                throw new SpeechConnectionException(
                    "Entra 返回的帐号租户不符合配置的资源 Tenant ID，请核对目录并重新登录。");

            using var recordStream = new MemoryStream();
            record.Serialize(recordStream, cancellationToken);
            var serialized = recordStream.ToArray();
            try { recordFile.Write(serialized); }
            finally { CryptographicOperations.ZeroMemory(serialized); }
            _identitySettings = configuration;
            _credential = new SpeechOnlyCredential(browser, Invalidate);
            AccountName = record.Username;
            IsSignedIn = true;
            StateChanged?.Invoke();
        }
        catch (Exception error) when (error is AuthenticationFailedException or CredentialUnavailableException)
        {
            throw new SpeechConnectionException(DescribeFailure(error));
        }
        catch (System.Text.Json.JsonException)
        {
            throw new SpeechConnectionException("Entra 帐号缓存格式无效。请关闭 Conversation Assistant 并清除应用的 entra-account 缓存后重新登录。");
        }
        finally { _signInGate.Release(); }
    }

    public TokenCredential GetCredential(SpeechServiceConfiguration configuration)
    {
        if (!IsSignedIn || _credential is null || _identitySettings is null ||
            !configuration.HasSameIdentity(_identitySettings))
            throw new SpeechConnectionException("请先点击 Microsoft 登录，完成 Azure Entra 授权；不能使用 Work IQ 令牌或旧 Key 代替。");
        return _credential;
    }

    public void Invalidate()
    {
        IsSignedIn = false;
        AccountName = null;
        _credential = null;
        _identitySettings = null;
        StateChanged?.Invoke();
    }

    internal static InteractiveBrowserCredentialOptions CreateOptions(
        SpeechServiceConfiguration configuration, AuthenticationRecord? record, string cacheId)
    {
        configuration.ValidateIdentity();
        var options = new InteractiveBrowserCredentialOptions
        {
            TenantId = configuration.TenantId,
            DisableAutomaticAuthentication = true,
            AuthenticationRecord = record,
            IsUnsafeSupportLoggingEnabled = false,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                // The cache name must stay stable across product renames.
                Name = "MeetingCopilot.Speech." + cacheId,
                UnsafeAllowUnencryptedStorage = false
            }
        };
        if (configuration.ClientId.Length > 0)
        {
            options.ClientId = configuration.ClientId;
            options.RedirectUri = new Uri("http://localhost");
        }
        options.Diagnostics.IsLoggingContentEnabled = false;
        return options;
    }

    [GeneratedRegex(@"\bAADSTS\d{3,12}\b", RegexOptions.CultureInvariant)]
    private static partial Regex EntraErrorCode();

    internal static string DescribeFailure(Exception error)
    {
        var code = EntraErrorCode().Match(error.Message);
        return $"Azure Entra 登录失败{(code.Success ? " [" + code.Value + "]" : "")}。" +
            "请检查租户、应用 Client ID、管理员同意及条件访问策略；未配置 Client ID 时使用的 SDK 开发客户端可能被组织限制。";
    }
}

internal sealed class SpeechOnlyCredential(TokenCredential inner, Action invalidate) : TokenCredential
{
    private static void ValidateScope(TokenRequestContext context)
    {
        if (context.Scopes.Length != 1 || context.Scopes[0] != SpeechEntraIdentity.CognitiveScope)
            throw new SpeechConnectionException("拒绝为非 Azure Cognitive Services 范围获取 Speech 令牌。");
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        ValidateScope(requestContext);
        try { return inner.GetToken(requestContext, cancellationToken); }
        catch (Exception error) when (error is AuthenticationFailedException or CredentialUnavailableException)
        {
            invalidate();
            throw new SpeechConnectionException(SpeechEntraIdentity.DescribeFailure(error));
        }
    }

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        ValidateScope(requestContext);
        try { return await inner.GetTokenAsync(requestContext, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is AuthenticationFailedException or CredentialUnavailableException)
        {
            invalidate();
            throw new SpeechConnectionException(SpeechEntraIdentity.DescribeFailure(error));
        }
    }
}
