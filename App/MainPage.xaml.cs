using System.ComponentModel;
using ConversationAssistant.Core.WorkIQ;
using ConversationAssistant_App.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace ConversationAssistant_App;

public sealed partial class MainPage : Page
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _pageCancellation = new();
    private AnswerRow? _observedAnswer;
    private bool _linkDialogOpen;
    private bool _renderVerified;
    private bool _preferRawAnswer;
    private string? _renderStatusKey;

    public MainPage()
    {
        InitializeComponent();
        _viewModel = ((App)Application.Current).Services.GetRequiredService<MainViewModel>();
        DataContext = _viewModel;
        UpdateLanguage();
        ActualThemeChanged += (_, _) => _viewModel.RefreshTheme();
        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.TranscriptScrollRequested += () => TranscriptScroll.ChangeView(
            null, TranscriptScroll.ScrollableHeight, null);
        _timer.Tick += (_, _) => _viewModel.Tick();
        _timer.Start();
        RenderAnswer();
        AnswerRichText.LinkRequested += uri => _ = ConfirmExternalLinkAsync(uri);
        Loaded += Records_Loaded;
        Loaded += ScheduleStartupSignIn;
        Unloaded += async (_, _) =>
        {
            _timer.Stop();
            _pageCancellation.Cancel();
            _viewModel.PropertyChanged -= OnViewModelChanged;
            if (_observedAnswer is not null) _observedAnswer.PropertyChanged -= OnAnswerChanged;
            await _viewModel.EndAsync();
        };
    }

    private void ScheduleStartupSignIn(object sender, RoutedEventArgs e) =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            async () => await _viewModel.SignInAtStartupAsync(_pageCancellation.Token));


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
        var language = _viewModel.Texts.Language;
        var markdown = answer?.DisplayMarkdown ?? AnswerPresentation.Compose(null, null, [], language);
        AnswerFallbackText.Text = markdown.Length <= 200_000 ? markdown :
            markdown[..200_000] + "\n\n" + _viewModel.Texts["RenderTruncated"];
        _renderVerified = false;
        try
        {
            AnswerRichText.Render(markdown);
            _renderVerified = true;
            _renderStatusKey = null;
        }
        catch (Exception error) when (error is ArgumentException or
            System.Runtime.InteropServices.COMException)
        {
            _renderStatusKey = "RenderFailed";
            _viewModel.ShowRenderingError(error.Message);
        }
        UpdateAnswerVisibility();
    }

    private void UpdateAnswerVisibility()
    {
        var rich = _renderVerified && !_preferRawAnswer;
        AnswerRichScroll.Visibility = rich ? Visibility.Visible : Visibility.Collapsed;
        AnswerFallback.Visibility = rich ? Visibility.Collapsed : Visibility.Visible;
        AnswerViewMenuItem.Text = _viewModel.Texts[_preferRawAnswer ? "FormattedView" : "ViewOriginal"];
        AnswerRenderStatus.Text = _renderStatusKey is null ? "" : _viewModel.Texts[_renderStatusKey];
        AnswerRenderStatus.Visibility = string.IsNullOrEmpty(AnswerRenderStatus.Text)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AnswerView_Click(object sender, RoutedEventArgs e)
    {
        _preferRawAnswer = !_preferRawAnswer;
        UpdateAnswerVisibility();
    }

    private void ActionMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu) return;
        foreach (var item in menu.Items) item.DataContext = _viewModel;
        UpdateAnswerVisibility();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var flyout = (Flyout)AssistantLayout.Resources["SettingsFlyout"];
        ((FrameworkElement)flyout.Content).DataContext = _viewModel;
        flyout.ShowAt((FrameworkElement)sender);
    }


    private async Task ConfirmExternalLinkAsync(Uri uri)
    {
        if (_linkDialogOpen) return;
        _linkDialogOpen = true;
        try
        {
            var texts = _viewModel.Texts;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Language = texts.LanguageTag,
                Title = texts["DialogLinkTitle"],
                Content = new TextBlock
                {
                    Text = uri.AbsoluteUri + "\n" + texts["DialogLinkWarning"],
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = texts["OpenLink"],
                CloseButtonText = texts["Cancel"]
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
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
    private async void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (!await _viewModel.NewChatAsync()) return;
        AskBox.Text = "";
        TranscriptTabs.SelectedItem = LiveTranscriptTab;
        AnalysisTabs.SelectedIndex = 0;
        await _viewModel.RefreshHistoryAsync(_pageCancellation.Token);
    }
    private async void End_Click(object sender, RoutedEventArgs e) => await _viewModel.EndAsync();
    private async void Pause_Click(object sender, RoutedEventArgs e) => await _viewModel.PauseAsync();
    private async void Resume_Click(object sender, RoutedEventArgs e) => await _viewModel.ResumeAsync();
    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanSignIn) return;
        await _viewModel.SignInAsync();
        if (_pageCancellation.IsCancellationRequested || string.IsNullOrEmpty(_viewModel.LastError) ||
            _linkDialogOpen) return;
        _linkDialogOpen = true;
        try
        {
            var texts = _viewModel.Texts;
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Language = texts.LanguageTag,
                Title = texts["SignIn"],
                Content = new TextBlock { Text = _viewModel.LastError, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = texts["Cancel"]
            }.ShowAsync();
        }
        finally { _linkDialogOpen = false; }
    }
    private void Retry_Click(object sender, RoutedEventArgs e) => _viewModel.Retry();
    private void Dismiss_Click(object sender, RoutedEventArgs e) => _viewModel.Dismiss();
    private void Scroll_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleScroll();
    private void ClearTranscript_Click(object sender, RoutedEventArgs e) => _viewModel.ClearTranscript();
    private void AnalyzeConversation_Click(object sender, RoutedEventArgs e) => _viewModel.AnalyzeConversation();
    private void ToggleAnswerPanel_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleAnswerPanel();
    private void AnswerPanelToggle_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        _viewModel.CollapseAnswerPanel();
    }
    private void SuggestedQuestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SuggestedQuestionRow question })
            _viewModel.OpenSuggestedQuestion(question.Id);
        else
            _viewModel.ShowRenderingError(_viewModel.Texts["ErrorQuestionCard"]);
    }
    private void SuggestedQuestion_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: SuggestedQuestionRow question })
        {
            AskBox.Text = question.Question;
            AskBox.Focus(FocusState.Programmatic);
            AskBox.Select(AskBox.Text.Length, 0);
        }
        else
            _viewModel.ShowRenderingError(_viewModel.Texts["ErrorQuestionCard"]);
    }
    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => _viewModel.RefreshDevices();
    private async void MicSettings_Click(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"));

    private void CopyTranscript_Click(object sender, RoutedEventArgs e) =>
        Copy(string.Join(Environment.NewLine, _viewModel.TranscriptItems
            .Select(x => x.CopyText)));

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
