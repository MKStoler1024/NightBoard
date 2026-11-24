using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NightBoard.Controls;
using NightBoard.Services;

namespace NightBoard;

public partial class MainWindow
{

    private static string Fmt(TimeSpan d)
    {
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours} 小时 {d.Minutes} 分";
        return $"{d.Minutes} 分";
    }


    // ------------------------------------------------------------ 天气
    private void UpdateWeatherUi(DateTime now)
    {
        var w = WeatherService.Current;
        if (w.City.Length == 0) return;

        if (WxIcon.Code != w.Code) WxIcon.Code = w.Code;

        bool night = w.Sunrise == DateTime.MinValue || w.Sunset <= w.Sunrise
            ? now.Hour < 6 || now.Hour >= 18
            : now < w.Sunrise || now > w.Sunset;
        if (WxIcon.IsNight != night) WxIcon.IsNight = night;

        Set(WxTemp, $"{Math.Round(w.Temp)}°");
        Set(WxText, w.Text);

        Set(WxFeels, $"{Math.Round(w.Feels)}°");
        Set(WxHum, $"{w.Humidity}%");
        Set(WxAqi, $"{w.Aqi}  {w.AqiLevel}");
        Set(WxWind, string.IsNullOrEmpty(w.Wind) ? "—" : w.Wind);
        Set(WxCity, WeatherService.IsStale ? $"{w.City} · {WeatherService.AgeText}" : w.City);
        Set(WxMoon, MoonText(now));

        RebuildAlerts(w);
    }


    /// <summary>「上弦月 42%」这样的一行。</summary>
    private static string MoonText(DateTime now)
    {
        double phase = Astro.MoonPhase(now);
        int lit = (int)Math.Round((1 - Math.Cos(phase * 2 * Math.PI)) / 2 * 100);

        string name = phase switch
        {
            < 0.02 or > 0.98 => "新月",
            < 0.23 => "娥眉月",
            < 0.27 => "上弦月",
            < 0.48 => "盈凸月",
            < 0.52 => "满月",
            < 0.73 => "亏凸月",
            < 0.77 => "下弦月",
            _ => "残月",
        };

        return $"{name}  {lit}%";
    }


    private static Avalonia.Media.SolidColorBrush AlertBrush(string level)
    {
        if (_alertBrushes.TryGetValue(level, out var cached)) return cached;
        var brush = new Avalonia.Media.SolidColorBrush(AlertChip.ColorOf(level), 0.9);
        _alertBrushes[level] = brush;
        return brush;
    }


    private void RebuildAlerts(WeatherNow w)
    {
        var alerts = w.Alerts ?? (IReadOnlyList<WeatherAlert>)Array.Empty<WeatherAlert>();
        string sig = string.Join("|", alerts.Select(a => a.Title + a.Level)) + "|" + _scale.ToString("F2");
        if (sig == _alertSignature) return;
        _alertSignature = sig;

        AlertPanel.Children.Clear();
        foreach (var a in alerts)
        {
            // 一个点标等级，后面跟字。夜里看着安静。
            var dot = new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 5 * _scale,
                Height = 5 * _scale,
                Fill = AlertBrush(a.Level),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7 * _scale, 0),
            };

            var text = new TextBlock
            {
                Text = string.IsNullOrEmpty(a.Type) ? a.Title : $"{a.Type}{a.Level}预警",
                Foreground = new SolidColorBrush(Palette.Dim),
                FontSize = 14 * _scale,
                LetterSpacing = 1,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Palette.Track(text, Palette.Role.Dim);

            var item = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Margin = new Thickness(0, 0, 22 * _scale, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            item.Children.Add(dot);
            item.Children.Add(text);
            AlertPanel.Children.Add(item);
        }
    }


    // ------------------------------------------------------------ 预报
    private void UpdateForecast(DateTime now)
    {
        var hours = WeatherService.Hourly;
        string sig = $"{hours.Count}|{(hours.Count > 0 ? hours[0].Time.ToString("MMddHH") : "")}|{_scale:F2}";
        if (sig != _hourlySignature)
        {
            _hourlySignature = sig;
            Hourly.Hours = hours;
            Hourly.Slot = 26 * _scale;
            Hourly.InvalidateMeasure();

            // 头一次拿到数据，滚到"现在"附近
            if (!_hourlyScrolled && hours.Count > 0)
            {
                _hourlyScrolled = true;
                Dispatcher.UIThread.Post(() =>
                {
                    int idx = 0;
                    for (int i = 0; i < hours.Count; i++)
                        if (hours[i].Time.Date == now.Date && hours[i].Time.Hour == now.Hour) { idx = i; break; }
                    HourlyScroll.Offset = new Vector(Math.Max(0, (idx - 1) * 26 * _scale), 0);
                }, DispatcherPriority.Background);
            }
        }

        // 均温也摆在天气栏里
        if (hours.Count > 0)
        {
            double sum = 0;
            foreach (var h in hours) sum += h.Temp;
            Set(WxAvg, $"{Math.Round(sum / hours.Count)}°");
        }

        string daily = BuildDailyText();
        if (daily != _dailySignature)
        {
            _dailySignature = daily;
            DailyLine.Text = daily;
        }
    }


    private static string BuildDailyText()
    {
        var days = WeatherService.Daily;
        if (days.Count == 0) return "";

        var names = new[] { "明天", "后天", "大后天" };
        var parts = new List<string>();

        for (int i = 0; i < days.Count && i < 3; i++)
        {
            string name = i < names.Length
                ? names[i]
                : "周" + "日一二三四五六"[(int)days[i].Date.DayOfWeek];

            parts.Add($"{name}  {Math.Round(days[i].High)}° / {Math.Round(days[i].Low)}°   {WeatherService.TextOf(days[i].Code)}");
        }

        return string.Join("      ", parts);
    }

}
