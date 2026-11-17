using ConversationAssistant.Core.Context;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Persistence;
using ConversationAssistant.Core.Speech;
using ConversationAssistant.Core.Transcript;
using ConversationAssistant.Core.WorkIQ;
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
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<ISpeechRecognitionService, OfflineWhisperSpeechRecognitionService>();
        services.AddSingleton<WorkIqEntraIdentity>();
        services.AddSingleton<IWorkIqTokenProvider>(provider => provider.GetRequiredService<WorkIqEntraIdentity>());
        services.AddSingleton(provider => new WorkIqHttpClient(
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) },
            provider.GetRequiredService<IWorkIqTokenProvider>()));
        services.AddSingleton<ITranscriptEngine, TranscriptEngine>();
        services.AddSingleton<PromptBuilder>();
        services.AddSingleton<IContextBuilder, ContextBuilder>();
        services.AddSingleton<WorkIqDiagnostics>();
        services.AddSingleton<INetworkStatus, NetworkStatus>();
        services.AddSingleton<IAuthenticationService, AuthenticationService>();
        services.AddSingleton<IWorkIqClient>(provider => new LoggedWorkIqClient(
            provider.GetRequiredService<WorkIqHttpClient>(),
            provider.GetRequiredService<WorkIqDiagnostics>()));
        services.AddSingleton(_ => new JsonConversationArchiveStore(ConversationRecordLocation.DirectoryPath));
        services.AddSingleton<IConversationArchiveStore>(provider => provider.GetRequiredService<JsonConversationArchiveStore>());
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
