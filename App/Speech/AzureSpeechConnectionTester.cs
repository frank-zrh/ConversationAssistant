using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Settings;
using ConversationAssistant.Core.Speech;
using ConversationAssistant_App.Authentication;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

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
        var stage = AzureSpeechDiagnostics.StartupStage.Initialization;
        try
        {
            var sdkConfiguration = AzureSpeechSessionFactory.CreateConfiguration(
                configuration, language, identity.GetCredential(configuration));
            stage = AzureSpeechDiagnostics.StartupStage.Connection;
            await ConnectAsync(sdkConfiguration, deadline.Token).ConfigureAwait(false);
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
            throw new SpeechConnectionException(AzureSpeechDiagnostics.Initialization(error, stage));
        }
    }

    internal static async Task ConnectAsync(SpeechConfig configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var format = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
        using var input = AudioInputStream.CreatePushStream(format);
        using var audio = AudioConfig.FromStreamInput(input);
        // Unlike ConversationTranscriber, SpeechRecognizer supports connection-only preopening.
        using var recognizer = new SpeechRecognizer(configuration, audio);
        using var connection = Connection.FromRecognizer(recognizer);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Connected += (_, _) => connected.TrySetResult();
        recognizer.Canceled += (_, args) => connected.TrySetException(new SpeechConnectionException(
            AzureSpeechDiagnostics.Cancellation(args.ErrorCode, args.ErrorDetails)));
        // No microphone, audio writes or recognition start are needed to test the connection.
        connection.Open(forContinuousRecognition: true);
        await connected.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
