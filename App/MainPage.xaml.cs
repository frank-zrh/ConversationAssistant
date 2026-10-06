using System.ComponentModel;
using ConversationAssistant.Core.WorkIQ;
using ConversationAssistant_App.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace ConversationAssistant_App;

public sealed partial class MainPage : Page
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly AnswerMarkdownFormatter _answerFormatter = new();
    private readonly AnswerRenderQueue _answerDocuments = new();
    private readonly RemoteImageLoader _imageLoader = new();
    private readonly CancellationTokenSource _pageCancellation = new();
    private readonly Dictionary<string, string> _approvedImages = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> _remoteImages = new Dictionary<string, string>();
    private AnswerRow? _observedAnswer;
    private Guid? _renderedQuestionId;
    private bool _answerViewReady;
    private bool _initializingAnswerView;
    private bool _linkDialogOpen;
    private bool _appNavigationRequested;
    private bool _renderVerified;
    private bool _preferRawAnswer;
    private long _renderRevision;
    private ulong? _answerNavigationId;
    private string? _renderStatusKey;

    public MainPage()
    {
        InitializeComponent();
        _viewModel = ((App)Application.Current).Services.GetRequiredService<MainViewModel>();
        DataContext = _viewModel;
        UpdateLanguage();
        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.TranscriptScrollRequested += () => TranscriptScroll.ChangeView(
            null, TranscriptScroll.ScrollableHeight, null);
        _timer.Tick += (_, _) => _viewModel.Tick();
        _timer.Start();
        RenderAnswer();
        Loaded += InitializeAnswerView;
        Unloaded += async (_, _) =>
        {
            _timer.Stop();
            _pageCancellation.Cancel();
            _answerDocuments.Clear();
            _imageLoader.Dispose();
            _viewModel.PropertyChanged -= OnViewModelChanged;
            if (_observedAnswer is not null) _observedAnswer.PropertyChanged -= OnAnswerChanged;
            AnswerWebView.Close();
            _approvedImages.Clear();
            await _viewModel.EndAsync();
        };
    }

    private async void InitializeAnswerView(object sender, RoutedEventArgs e)
    {
        if (_answerViewReady || _initializingAnswerView) return;
        _initializingAnswerView = true;
        try
        {
            var browserData = Path.Combine(Path.GetTempPath(),
                "ConversationAssistant-WebView2-" + Guid.NewGuid().ToString("N"));
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, browserData, null);
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = "ConversationAssistantRichAnswers";
            options.IsInPrivateModeEnabled = true;
            await AnswerWebView.EnsureCoreWebView2Async(environment, options);
            var core = AnswerWebView.CoreWebView2;
            core.Settings.IsScriptEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            AnswerWebView.NavigationStarting += AnswerNavigationStarting;
            AnswerWebView.NavigationCompleted += AnswerNavigationCompleted;
            _answerViewReady = true;
            RenderAnswer();
        }
        catch (Exception error) when (error is InvalidOperationException or
            System.Runtime.InteropServices.COMException or FileNotFoundException)
        {
            _renderStatusKey = "RenderUnavailable";
            UpdateAnswerVisibility();
            _viewModel.ShowRenderingError(error.Message);
        }
        finally { _initializingAnswerView = false; }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Texts))
        {
            UpdateLanguage();
            RenderAnswer();
            return;
        }
        if (e.PropertyName != nameof(MainViewModel.SelectedAnswer)) return;
        if (_observedAnswer is not null) _observedAnswer.PropertyChanged -= OnAnswerChanged;
        _observedAnswer = _viewModel.SelectedAnswer;
        if (_observedAnswer is not null) _observedAnswer.PropertyChanged += OnAnswerChanged;
        RenderAnswer();
    }

    private void UpdateLanguage()
    {
        Language = _viewModel.Texts.LanguageTag;
        var flyout = (Flyout)AssistantLayout.Resources["SettingsFlyout"];
        ((FrameworkElement)flyout.Content).Language = Language;
    }

    private void OnAnswerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnswerRow.DisplayMarkdown))
            RenderAnswer();
    }

    private void RenderAnswer()
    {
        var answer = _viewModel.SelectedAnswer;
        if (_renderedQuestionId != answer?.Id)
        {
            _renderedQuestionId = answer?.Id;
            _approvedImages.Clear();
        }
        var language = _viewModel.Texts.Language;
        var markdown = answer?.DisplayMarkdown ?? AnswerPresentation.Compose(null, null, [], language);
        AnswerFallbackText.Text = markdown.Length <= 200_000 ? markdown :
            markdown[..200_000] + "\n\n" + _viewModel.Texts["RenderTruncated"];
        var revision = ++_renderRevision;
        _renderVerified = false;
        _remoteImages = new Dictionary<string, string>();
        if (_answerViewReady) _renderStatusKey = null;
        UpdateAnswerVisibility();
        if (!_answerViewReady) return;
        try
        {
            var rendered = _answerFormatter.Format(markdown, _approvedImages, language);
            _remoteImages = rendered.RemoteImages;
            _answerDocuments.Enqueue(revision, AnswerHtmlPage.Build(rendered, revision, language));
            _renderStatusKey = "RenderFormatting";
            UpdateAnswerVisibility();
            StartNextAnswerDocument();
            _ = WatchAnswerNavigationAsync(revision);
        }
        catch (Exception error) when (error is ArgumentException or
            System.Runtime.InteropServices.COMException)
        {
            _renderStatusKey = "RenderFailed";
            UpdateAnswerVisibility();
            _viewModel.ShowRenderingError(error.Message);
        }
    }

    private void StartNextAnswerDocument()
    {
        if (!_answerViewReady || _pageCancellation.IsCancellationRequested ||
            _answerDocuments.StartNext() is not { } document) return;
        _answerNavigationId = null;
        _appNavigationRequested = true;
        try { AnswerWebView.NavigateToString(document.Html); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            System.Runtime.InteropServices.COMException)
        {
            _answerDocuments.Complete(document.Revision);
            _appNavigationRequested = false;
            _renderStatusKey = "RenderFailed";
            UpdateAnswerVisibility();
            _viewModel.ShowRenderingError(error.Message);
        }
    }

    private void AnswerNavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // NavigateToString is a data:text/html navigation, not an about:blank navigation.
        if (AnswerNavigationPolicy.IsAppHtmlDocument(e.Uri, e.IsUserInitiated, _appNavigationRequested))
        {
            _appNavigationRequested = false;
            _answerNavigationId = e.NavigationId;
            return;
        }
        e.Cancel = true;
        if (e.IsUserInitiated &&
            AnswerMarkdownFormatter.IsSafeRemoteUri(e.Uri, allowMail: true, out var uri))
            _ = ConfirmExternalLinkAsync(uri!);
    }

    private async void AnswerNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.NavigationId != _answerNavigationId || _pageCancellation.IsCancellationRequested ||
            _answerDocuments.Active is not { } document) return;
        var revision = document.Revision;
        try
        {
            if (revision != _renderRevision) return;
            if (!args.IsSuccess)
                throw new InvalidOperationException(_viewModel.Texts.Format("ErrorRichNavigation", args.WebErrorStatus));
            // Execute only this fixed readiness check, never content supplied by Work IQ.
            var visible = await AnswerWebView.ExecuteScriptAsync($$"""
                (() => {
                    const main = document.querySelector('main');
                    return document.querySelector('meta[name="mc-render-id"]')?.content === '{{revision}}'
                        && !!main && (!!main.innerText.trim() || !!main.querySelector('img'));
                })()
                """);
            if (revision != _renderRevision || _pageCancellation.IsCancellationRequested) return;
            if (visible != "true")
                throw new InvalidOperationException(_viewModel.Texts["ErrorRichEmpty"]);
            _renderVerified = true;
            _renderStatusKey = null;
            UpdateAnswerVisibility();
        }
        catch (Exception error) when (error is InvalidOperationException or
            System.Runtime.InteropServices.COMException)
        {
            if (revision != _renderRevision || _pageCancellation.IsCancellationRequested) return;
            _renderVerified = false;
            _renderStatusKey = "RenderFailed";
            UpdateAnswerVisibility();
            _viewModel.ShowRenderingError(error.Message);
        }
        finally
        {
            if (_answerDocuments.Complete(revision))
            {
                _answerNavigationId = null;
                _appNavigationRequested = false;
                StartNextAnswerDocument();
            }
        }
    }

    private async Task WatchAnswerNavigationAsync(long revision)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), _pageCancellation.Token); }
        catch (OperationCanceledException) when (_pageCancellation.IsCancellationRequested) { return; }
        if (revision == _renderRevision && !_renderVerified)
        {
            _renderStatusKey = "RenderSlow";
            UpdateAnswerVisibility();
        }
    }

    private void UpdateAnswerVisibility()
    {
        var rich = _answerViewReady && _renderVerified && !_preferRawAnswer;
        // Keep the WebView laid out during navigation; hide it underneath the readable fallback.
        AnswerWebView.Visibility = Visibility.Visible;
        AnswerWebView.Opacity = rich ? 1 : 0;
        AnswerWebView.IsHitTestVisible = rich;
        AnswerFallback.Visibility = rich ? Visibility.Collapsed : Visibility.Visible;
        AnswerViewButton.Content = _viewModel.Texts[_preferRawAnswer ? "FormattedView" : "ViewOriginal"];
        AnswerRenderStatus.Text = _renderStatusKey is null ? "" : _viewModel.Texts[_renderStatusKey];
        AnswerRenderStatus.Visibility = string.IsNullOrEmpty(AnswerRenderStatus.Text)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AnswerView_Click(object sender, RoutedEventArgs e)
    {
        _preferRawAnswer = !_preferRawAnswer;
        UpdateAnswerVisibility();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var flyout = (Flyout)AssistantLayout.Resources["SettingsFlyout"];
        ((FrameworkElement)flyout.Content).DataContext = _viewModel;
        flyout.ShowAt((FrameworkElement)sender);
    }

    private async void SaveSpeechSettings_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.SaveSpeechSettingsAsync();
    }

    private async void TestSpeechConnection_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.TestSpeechConnectionAsync();
    }

    private async Task ConfirmExternalLinkAsync(Uri uri)
    {
        if (_linkDialogOpen) return;
        _linkDialogOpen = true;
        try
        {
            var image = _remoteImages.ContainsKey(uri.AbsoluteUri);
            var inline = image && AnswerMarkdownFormatter.CanFetchInlineImage(uri);
            var selectedQuestion = _viewModel.SelectedAnswer?.Id;
            var texts = _viewModel.Texts;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Language = texts.LanguageTag,
                Title = texts[image ? "DialogImageTitle" : "DialogLinkTitle"],
                Content = new TextBlock
                {
                    Text = uri.AbsoluteUri + "\n" + texts[image ? "DialogImageWarning" : "DialogLinkWarning"],
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = texts[inline ? "LoadInline" : image ? "OpenImageBrowser" : "OpenLink"],
                SecondaryButtonText = inline ? texts["OpenBrowser"] : "",
                CloseButtonText = texts["Cancel"]
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && inline)
            {
                var data = await _imageLoader.LoadAsync(uri, _pageCancellation.Token);
                if (_viewModel.SelectedAnswer?.Id != selectedQuestion) return;
                if (_approvedImages.Values.Sum(value => value.Length) + data.Length > 700_000)
                    throw new InvalidDataException(_viewModel.Texts["ErrorInlineImagesLimit"]);
                _approvedImages[uri.AbsoluteUri] = data;
                RenderAnswer();
            }
            else if (result == ContentDialogResult.Secondary ||
                result == ContentDialogResult.Primary)
                await Launcher.LaunchUriAsync(uri);
        }
        catch (OperationCanceledException) when (_pageCancellation.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            _viewModel.ShowRenderingError(_viewModel.Texts["ErrorImageTimeout"]);
        }
        catch (Exception error) when (error is InvalidOperationException or
            System.Runtime.InteropServices.COMException or IOException or HttpRequestException)
        {
            _viewModel.ShowRenderingError(error.Message);
        }
        finally { _linkDialogOpen = false; }
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await _viewModel.StartAsync();
    private async void End_Click(object sender, RoutedEventArgs e) => await _viewModel.EndAsync();
    private async void Pause_Click(object sender, RoutedEventArgs e) => await _viewModel.PauseAsync();
    private async void Resume_Click(object sender, RoutedEventArgs e) => await _viewModel.ResumeAsync();
    private async void SignIn_Click(object sender, RoutedEventArgs e) => await _viewModel.SignInAsync();
    private void Retry_Click(object sender, RoutedEventArgs e) => _viewModel.Retry();
    private void Dismiss_Click(object sender, RoutedEventArgs e) => _viewModel.Dismiss();
    private void Scroll_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleScroll();
    private void ClearTranscript_Click(object sender, RoutedEventArgs e) => _viewModel.ClearTranscript();
    private void AnalyzeConversation_Click(object sender, RoutedEventArgs e) => _viewModel.AnalyzeConversation();
    private void SuggestedQuestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SuggestedQuestionRow question })
            _viewModel.OpenSuggestedQuestion(question.Id);
        else
            _viewModel.ShowRenderingError(_viewModel.Texts["ErrorQuestionCard"]);
    }
    private void TranscriptCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TranscriptRow card })
            _viewModel.OpenTranscriptCard(card.Id);
        else
            _viewModel.ShowRenderingError(_viewModel.Texts["ErrorTranscriptCard"]);
    }
    private void TranscriptCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: TranscriptRow card })
        {
            AskBox.Text = card.Text;
            AskBox.Focus(FocusState.Programmatic);
            AskBox.Select(AskBox.Text.Length, 0);
        }
        else
            _viewModel.ShowRenderingError(_viewModel.Texts["ErrorTranscriptCard"]);
    }
    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => _viewModel.RefreshDevices();
    private async void MicSettings_Click(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"));

    private void CopyTranscript_Click(object sender, RoutedEventArgs e) =>
        Copy(string.Join(Environment.NewLine, _viewModel.TranscriptItems
            .Select(x => $"{x.Time}  {x.Text}")));

    private void CopyAnswer_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedAnswer is { } answer) Copy(answer.Answer);
    }

    private static void Copy(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    private void Ask_Click(object sender, RoutedEventArgs e) => Ask();

    private void AskBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter &&
            InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down))
        {
            Ask();
            e.Handled = true;
        }
    }

    private void Ask()
    {
        if (_viewModel.Ask(AskBox.Text)) AskBox.Text = "";
    }
}
