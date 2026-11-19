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
        DialogAppearance.Apply(this);
        Opened += (_, _) => DialogAppearance.Apply(this);
        Cursor = new Cursor(StandardCursorType.Arrow);

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
                Foreground = DialogAppearance.Brush("#F4EBD8", "#1E1B16"),
                TextWrapping = TextWrapping.Wrap,
            });

            var meta = new TextBlock
            {
                Text = a.PubTime == DateTime.MinValue
                    ? $"{a.Type} · {a.Level}预警"
                    : $"{a.Type} · {a.Level}预警 · {a.PubTime:MM-dd HH:mm} 发布",
                FontSize = 12,
                Foreground = DialogAppearance.Brush("#B6AC99", "#6B6459"),
                Margin = new Thickness(16, 6, 0, 0),
            };

            var body = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(a.Detail) ? "（这条预警没有正文）" : a.Detail.Trim(),
                FontSize = 13,
                LineHeight = 22,
                Foreground = DialogAppearance.Brush("#C8BFAC", "#3B342A"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            };

            var block = new StackPanel();
            block.Children.Add(head);
            block.Children.Add(meta);
            block.Children.Add(body);
            List.Children.Add(new Border { Background = DialogAppearance.Brush("#191713", "#EEE7DB"), CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Child = block });
        }

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
