using ConversationAssistant.Core.Context;
using ConversationAssistant.Core.Conversation;
using ConversationAssistant.Core.Localization;
using ConversationAssistant.Core.Models;
using ConversationAssistant.Core.Persistence;
using ConversationAssistant.Core.Qwen;
using ConversationAssistant.Core.Transcript;
using ConversationAssistant_App.Audio;
using ConversationAssistant_App.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        services.AddSingleton(_ => LocalAppProfile.ForCurrentUser());
        services.AddSingleton<IQwenSettingsStore>(provider => new ProtectedQwenSettingsStore(
            provider.GetRequiredService<LocalAppProfile>().QwenSettingsFile));
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<QwenSpeechRecognitionService>();
        services.AddSingleton<ISpeechRecognitionService>(provider => provider.GetRequiredService<QwenSpeechRecognitionService>());
        services.AddSingleton(_ => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(2), MaxResponseContentBufferSize = 4 * 1024 * 1024
        });
        services.AddSingleton<QwenAnalysisClient>();
        services.AddSingleton<IWorkIqClient>(provider => provider.GetRequiredService<QwenAnalysisClient>());
        services.AddSingleton<ITranscriptEngine, TranscriptEngine>();
        services.AddSingleton<PromptBuilder>();
        services.AddSingleton<IContextBuilder, ContextBuilder>();
        services.AddSingleton<INetworkStatus, NetworkStatus>();
        services.AddSingleton(provider => new JsonConversationArchiveStore(provider.GetRequiredService<LocalAppProfile>().Conversations));
        services.AddSingleton<IConversationArchiveStore>(provider => provider.GetRequiredService<JsonConversationArchiveStore>());
        services.AddSingleton<ConversationSessionManager>();
        services.AddSingleton<MainViewModel>();
        Services = services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var logger = Services.GetRequiredService<ILogger<App>>();
        try { Services.GetRequiredService<LocalAppProfile>().EnsureCreated(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            var code = $"0x{error.HResult:X8}";
            logger.LogError("Could not initialize the local application profile: {ErrorType} ({Code})", error.GetType().Name, code);
            var texts = UiText.For(new ConversationSettings().DisplayLanguage);
            _window = new Window
            {
                Title = texts["AppTitle"],
                Content = new TextBlock
                {
                    Language = texts.LanguageTag, Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true, Text = texts.Format("ErrorLocalAppProfile", code)
                }
            };
            _window.Activate();
            return;
        }
        logger.LogInformation("Conversation Assistant started");
        _window = new MainWindow();
        _window.Activate();
    }
}
