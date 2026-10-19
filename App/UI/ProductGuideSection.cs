using ConversationAssistant.Core.WorkIQ;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ConversationAssistant_App.UI;

/// <summary>Native, selectable product brief with real table columns and no browser/network.</summary>
public sealed class ProductGuideSection : ContentControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(ProductGuideSection),
        new PropertyMetadata("", OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ProductGuideSection()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        IsTabStop = false;
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((ProductGuideSection)sender).Render();

    private void Render()
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var block in ProductGuidePresentation.Parse(Text))
        {
            if (!block.IsTable)
            {
                panel.Children.Add(CellText(block.Text, false));
                continue;
            }
            var columns = block.Rows.Max(row => row.Count);
            var grid = new Grid { MinWidth = columns * 145 };
            for (var column = 0; column < columns; column++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var row = 0; row < block.Rows.Count; row++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (var column = 0; column < columns; column++)
                {
                    var value = column < block.Rows[row].Count ? block.Rows[row][column] : "";
                    var cell = new Border
                    {
                        Padding = new Thickness(8),
                        BorderThickness = new Thickness(0.5),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)),
                        Child = CellText(value, row == 0)
                    };
                    if (row == 0)
                        cell.Background = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128));
                    Grid.SetRow(cell, row);
                    Grid.SetColumn(cell, column);
                    grid.Children.Add(cell);
                }
            }
            panel.Children.Add(new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Disabled,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = grid
            });
        }
        Content = panel;
    }

    private static TextBlock CellText(string text, bool header) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        FontSize = 13,
        FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal
    };
}
