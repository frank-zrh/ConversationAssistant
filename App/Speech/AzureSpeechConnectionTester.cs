using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Settings;
using ConversationAssistant.Core.Speech;
using ConversationAssistant_App.Authentication;

namespace ConversationAssistant_App.Speech;

public sealed class AzureSpeechConnectionTester(ISpeechSettingsStore settings, ISpeechCredentialProvider identity)
{
    public async Task TestAsync(ConversationLanguage language, CancellationToken cancellationToken = default)
    {
        var configuration = settings.Load();
        if (configuration.Provider != SpeechProvider.AzureSpeech)
            throw new InvalidOperationException("请先选择 Azure AI Services / Speech 并保存。");
        configuration.ValidateForAzure();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var session = new AzureSpeechSession(configuration, language, identity.GetCredential(configuration));
            // Establish only the SDK connection: no microphone, audio writes or recognition start.
            await session.ConnectAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SpeechConnectionException(
                "Azure AI Speech [ConnectionTimeout] 20 秒内未能建立连接。请检查 Endpoint、Entra 登录、资源角色及网络访问规则。");
        }
        catch (Exception error) when (error is not SpeechConnectionException &&
            error is ApplicationException or ArgumentException or InvalidOperationException or
            System.Runtime.InteropServices.COMException)
        {
            throw new SpeechConnectionException(AzureSpeechDiagnostics.Initialization(error));
        }
    }
}
