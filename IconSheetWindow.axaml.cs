using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NightBoard.Controls;

namespace NightBoard;

/// <summary>调试用：把内嵌的 Lucide 图标全部铺出来看一遍（NB_ICONS=1）。</summary>
public partial class IconSheetWindow : Window
{
    public IconSheetWindow()
    {
        InitializeComponent();
        Topmost = true;
        Background = new SolidColorBrush(Color.Parse("#0C0B09"));

        foreach (var name in WeatherIcon.Names)
        {
            var icon = new WeatherIcon
            {
                IconName = name,
                Width = 74,
                Height = 74,
                Margin = new Thickness(10),
            };

            var label = new TextBlock
            {
                Text = name,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#7D7364")),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };

            var cell = new StackPanel { Margin = new Thickness(12) };
            cell.Children.Add(icon);
            cell.Children.Add(label);
            Sheet.Children.Add(cell);
        }
    }
}
