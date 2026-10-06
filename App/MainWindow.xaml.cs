using System.ComponentModel;
using ConversationAssistant_App.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace ConversationAssistant_App;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = ((App)Application.Current).Services.GetRequiredService<MainViewModel>();
        _viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => _viewModel.PropertyChanged -= OnViewModelChanged;
        UpdateLanguage();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));

        RootFrame.Loaded += ApplyInitialWindowSize;
        RootFrame.Navigate(typeof(MainPage));
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Texts)) UpdateLanguage();
    }

    private void UpdateLanguage()
    {
        Title = _viewModel.Texts["AppTitle"];
        AppTitleBar.Title = Title;
        ((FrameworkElement)Content).Language = _viewModel.Texts.LanguageTag;
    }

    private void ApplyInitialWindowSize(object sender, RoutedEventArgs e)
    {
        RootFrame.Loaded -= ApplyInitialWindowSize;
        var scale = RootFrame.XamlRoot.RasterizationScale;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        // AppWindow uses physical pixels; keep the two-pane workspace sized in XAML pixels.
        var width = Math.Min((int)Math.Ceiling(1280 * scale), area.Width);
        var height = Math.Min((int)Math.Ceiling(800 * scale), area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
    }
}
