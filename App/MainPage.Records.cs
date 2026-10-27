using ConversationAssistant_App.UI;
using System.Diagnostics;
using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace ConversationAssistant_App;

public sealed partial class MainPage
{
    private void CreateGroup_Click(object sender, RoutedEventArgs e) => _viewModel.CreateGroup();
    private async void SaveRecord_Click(object sender, RoutedEventArgs e) => await _viewModel.SaveRecordAsync();

    private async void ViewSession_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanViewSession) return;
        try
        {
            var path = await _viewModel.PrepareSessionLogAsync();
            if (path is null || !File.Exists(path)) throw new FileNotFoundException();
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe"))
            {
                UseShellExecute = false
            };
            start.ArgumentList.Add(path);
            using var process = Process.Start(start);
            if (process is null) _viewModel.ShowRenderingError(_viewModel.Texts["ErrorSessionLogOpen"]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidOperationException or Win32Exception)
        {
            _viewModel.ShowRenderingError(_viewModel.Texts["ErrorSessionLogOpen"]);
        }
    }

    private void Group_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TranscriptGroupRow } element) return;
        e.Handled = true;
        ShowGroupMenu(element, e.GetPosition(element));
    }

    private void Group_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Application && !(e.Key == VirtualKey.F10 &&
            InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down)))
            return;
        if (sender is not FrameworkElement { DataContext: TranscriptGroupRow } element) return;
        e.Handled = true;
        ShowGroupMenu(element);
    }

    private void ShowGroupMenu(FrameworkElement element, Point? position = null)
    {
        if (element.DataContext is not TranscriptGroupRow group) return;
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = _viewModel.Texts["RenameGroup"] };
        rename.Click += async (_, _) => await RenameGroupAsync(group);
        var analyze = new MenuFlyoutItem { Text = _viewModel.Texts["AnalyzeGroup"] };
        analyze.Click += (_, _) => _viewModel.AnalyzeGroup(group.Id);
        menu.Items.Add(rename);
        menu.Items.Add(analyze);
        if (position is { } point) menu.ShowAt(element, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(element);
    }

    private async Task RenameGroupAsync(TranscriptGroupRow group)
    {
        if (_linkDialogOpen) return;
        _linkDialogOpen = true;
        try
        {
            var input = new TextBox
            {
                Text = group.Name, MaxLength = 120, Header = _viewModel.Texts["GroupName"],
                PlaceholderText = _viewModel.Texts["GroupName"]
            };
            var validation = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(input);
            content.Children.Add(validation);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Language = _viewModel.Texts.LanguageTag,
                Title = _viewModel.Texts["RenameGroup"], Content = content,
                PrimaryButtonText = _viewModel.Texts["SaveGroupName"],
                CloseButtonText = _viewModel.Texts["Cancel"],
                DefaultButton = ContentDialogButton.Primary
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                args.Cancel = !_viewModel.RenameGroup(group.Id, input.Text);
                validation.Text = args.Cancel ? _viewModel.LastError : "";
            };
            await dialog.ShowAsync();
        }
        finally { _linkDialogOpen = false; }
    }

    private async void Records_Loaded(object sender, RoutedEventArgs e) =>
        await _viewModel.RefreshHistoryAsync(_pageCancellation.Token);

    private async void TranscriptTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && TranscriptTabs.SelectedItem == HistoryTab)
            await _viewModel.RefreshHistoryAsync(_pageCancellation.Token);
    }

    private async void RefreshHistory_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.RefreshHistoryAsync(_pageCancellation.Token);

    private async void HistoryRecord_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ConversationHistoryRow record || !_viewModel.CanImportConversation) return;
        if (await _viewModel.ImportAsync(record.Path))
        {
            TranscriptTabs.SelectedItem = LiveTranscriptTab;
            await _viewModel.RefreshHistoryAsync(_pageCancellation.Token);
        }
    }

    private async void OpenRecordsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.GetDirectoryName(_viewModel.RecordPath);
            if (string.IsNullOrWhiteSpace(directory)) directory = ConversationRecordLocation.DirectoryPath;
            Directory.CreateDirectory(directory);
            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            if (!await Launcher.LaunchFolderAsync(folder))
                _viewModel.ShowRenderingError(_viewModel.Texts["ErrorRecordsFolder"]);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or
            System.Runtime.InteropServices.COMException)
        {
            _viewModel.ShowRenderingError(_viewModel.Texts["ErrorRecordsFolder"]);
        }
    }
}
