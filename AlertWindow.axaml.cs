using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using NightBoard.Controls;
using NightBoard.Services;

namespace NightBoard;

/// <summary>点预警标签弹出的详情：标题、等级、发布时间、正文。</summary>
public partial class AlertWindow : Window
{
    public AlertWindow() : this(Array.Empty<WeatherAlert>()) { }

    public AlertWindow(IReadOnlyList<WeatherAlert> alerts)
    {
        InitializeComponent();
        Topmost = true;
        Cursor = new Cursor(StandardCursorType.Arrow);

        // 圆角窗口：背景交给里面的 Border
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Handled) return;
            if (e.Source is Button or TextBox or ComboBox or ListBox or ScrollViewer) return;
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        }, RoutingStrategies.Bubble);

        foreach (var a in alerts)
        {
            var color = AlertChip.ColorOf(a.Level);

            var head = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 9,
            };
            head.Children.Add(new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(color),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
            head.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(a.Title) ? $"{a.Type}{a.Level}预警" : a.Title,
                FontSize = 17,
                FontFamily = new FontFamily("avares://NightBoard/Assets/Fonts#HarmonyOS Sans SC Medium"),
                Foreground = new SolidColorBrush(Color.Parse("#F4EBD8")),
                TextWrapping = TextWrapping.Wrap,
            });

            var meta = new TextBlock
            {
                Text = a.PubTime == DateTime.MinValue
                    ? $"{a.Type} · {a.Level}预警"
                    : $"{a.Type} · {a.Level}预警 · {a.PubTime:MM-dd HH:mm} 发布",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#6E6557")),
                Margin = new Thickness(16, 6, 0, 0),
            };

            var body = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(a.Detail) ? "（这条预警没有正文）" : a.Detail.Trim(),
                FontSize = 13,
                LineHeight = 22,
                Foreground = new SolidColorBrush(Color.Parse("#C8BFAC")),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            };

            var block = new StackPanel();
            block.Children.Add(head);
            block.Children.Add(meta);
            block.Children.Add(body);
            List.Children.Add(block);
        }

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;

        var wa = screen.WorkingArea;
        double s = screen.Scaling <= 0 ? 1 : screen.Scaling;
        double left = wa.X / s + Math.Max(0, (wa.Width / s - ClientSize.Width) / 2);
        double top = wa.Y / s + Math.Max(0, (wa.Height / s - ClientSize.Height) / 2);

        Position = new PixelPoint((int)Math.Round(left), (int)Math.Round(top));
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
