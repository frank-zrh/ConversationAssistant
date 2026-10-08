using ConversationAssistant.Core.Context;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Persistence;
using ConversationAssistant.Core.Settings;
using ConversationAssistant.Core.Speech;
using ConversationAssistant.Core.Transcript;
using ConversationAssistant_App.Audio;
using ConversationAssistant_App.Authentication;
using ConversationAssistant_App.Speech;
using ConversationAssistant_App.UI;
using ConversationAssistant_App.WorkIQ;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace ConversationAssistant_App;

public partial class App : Application
{
    private Window? _window;
    public IServiceProvider Services { get; }

    public App()
    {
        InitializeComponent();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddDebug());
        services.AddSingleton<OfflineWhisperModelManager>();
        // Keep the original data directory so the rename preserves existing protected settings.
        services.AddSingleton<ISpeechSettingsStore>(_ => new ProtectedSpeechSettingsStore(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MeetingCopilot", "speech-settings.bin")));
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<OfflineWhisperSpeechRecognitionService>();
        services.AddSingleton<SpeechEntraIdentity>();
        services.AddSingleton<ISpeechCredentialProvider>(provider => provider.GetRequiredService<SpeechEntraIdentity>());
        services.AddSingleton<IAzureSpeechSessionFactory, AzureSpeechSessionFactory>();
        services.AddSingleton<AzureSpeechConnectionTester>();
        services.AddSingleton<AzureSpeechRecognitionService>();
        services.AddSingleton<ISpeechRecognitionService>(provider => new SelectableSpeechRecognitionService(
            provider.GetRequiredService<ISpeechSettingsStore>(),
            provider.GetRequiredService<AzureSpeechRecognitionService>(),
            provider.GetRequiredService<OfflineWhisperSpeechRecognitionService>()));
        services.AddSingleton<ITranscriptEngine, TranscriptEngine>();
        services.AddSingleton<PromptBuilder>();
        services.AddSingleton<IContextBuilder, ContextBuilder>();
        services.AddSingleton<WorkIqProcess>();
        services.AddSingleton<INetworkStatus, NetworkStatus>();
        services.AddSingleton<IAuthenticationService, AuthenticationService>();
        services.AddSingleton<IWorkIqClient, WorkIqClient>();
        services.AddSingleton<IConversationArchiveStore>(_ =>
            new JsonConversationArchiveStore(ConversationRecordLocation.DirectoryPath));
        services.AddSingleton<ConversationSessionManager>();
        services.AddSingleton<MainViewModel>();
        Services = services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services.GetRequiredService<ILogger<App>>().LogInformation("Conversation Assistant started");
        _window = new MainWindow();
        _window.Activate();
    }
}
