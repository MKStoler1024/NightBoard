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

    /// <summary>换主题：先压暗，换完色再提亮，像一次呼吸。</summary>
    private void ApplyThemeAnimated(bool light)
    {
        if (Palette.IsLight == light) return;

        Tween(0.16, p => Root.Opacity = 1 - 0.85 * Ease(p), () =>
        {
            Palette.Apply(light, force: true);
            Tween(0.28, p => Root.Opacity = 0.15 + 0.85 * Ease(p));
        });
    }


    private void UpdateAutoTheme(DateTime now)
    {
        var w = WeatherService.Current;
        bool light;
        if (w.Sunrise != DateTime.MinValue && w.Sunset > w.Sunrise)
            light = now >= w.Sunrise && now <= w.Sunset;
        else
            light = now.Hour >= 6 && now.Hour < 18;

        ApplyThemeAnimated(light);
    }


    // ------------------------------------------------------------ 主题
    private void ApplyTheme()
    {
        bool light = Palette.IsLight;

        RequestedThemeVariant = light ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        DialogAppearance.UseDarkTitleBar(this);
        Background = new SolidColorBrush(Palette.Bg);
        DimLayer.Background = new SolidColorBrush(Palette.Bg);

        ClockH.Foreground = new SolidColorBrush(Palette.Ink);
        ClockM.Foreground = new SolidColorBrush(Palette.Ink);
        ClockColon.Foreground = new SolidColorBrush(Palette.Ink);
        ClockS.Foreground = new SolidColorBrush(Palette.Faint);

        var ghost = new SolidColorBrush(Palette.Ghost);
        GhostH.Foreground = ghost;
        GhostColon.Foreground = ghost;
        GhostM.Foreground = ghost;
        GhostS.Foreground = ghost;
        GhostS.Opacity = light ? 0.5 : 0.75;

        DateLine.Foreground = new SolidColorBrush(Palette.Value);
        LunarLine.Foreground = new SolidColorBrush(Palette.Faint);

        WxTemp.Foreground = new SolidColorBrush(Palette.Ink);
        WxText.Foreground = new SolidColorBrush(Palette.Dim);
        foreach (var tb in new[] { WxFeels, WxHum, WxAqi, WxWind, WxCity, WxMoon, WxAvg })
            tb.Foreground = new SolidColorBrush(Palette.Value);
        // 层次：名字 Ink → 数值 Value → 标签 Dim → 陪衬 Faint
        foreach (var run in new[] { FestName, TermName, CdName, CdValue, AfterName })
            run.Foreground = new SolidColorBrush(Palette.Ink);

        foreach (var run in new[] { EvTitle, EvTime, FestDate, TermDate, FestLeft, TermLeft, EvWhen, AfterDate, AfterLeft, MoonPhaseDate, MoonPhaseLeft })
            run.Foreground = new SolidColorBrush(Palette.Value);

        foreach (var run in new[] { FestLabel, TermLabel, EvLabel, CdMid, CdUnit, AfterLabel, MoonPhaseLabel })
            run.Foreground = new SolidColorBrush(Palette.Dim);



        // 折线左侧的淡出：从背景色渐到透明，遮住被裁掉的那截
        HourlyFade.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, Palette.Bg.R, Palette.Bg.G, Palette.Bg.B), 0),
                new GradientStop(Palette.Bg, 0.9),
            },
        };

        AbText.Foreground = new SolidColorBrush(Palette.Dim);
        AbMeta.Foreground = new SolidColorBrush(Palette.Value);
        MetaBat.Foreground = new SolidColorBrush(Palette.Value);
        MetaSep.Foreground = new SolidColorBrush(Palette.Line);
        DailyLine.Foreground = new SolidColorBrush(Palette.Value);

        QuoteRule.Background = new SolidColorBrush(Palette.Line);
        QuoteText.Foreground = new SolidColorBrush(Palette.Value);
        QuoteFrom.Foreground = new SolidColorBrush(Palette.Faint);

        foreach (var card in _charts.Values)
        {
            card.Title.Foreground = new SolidColorBrush(Palette.Dim);
            card.Value.Foreground = new SolidColorBrush(Palette.Ink);
            card.Chart.Stroke = new SolidColorBrush(AccentNow(card.Accent));
            card.Chart.PlateBrush = new SolidColorBrush(Palette.Plate);
            card.Chart.GridBrush = new SolidColorBrush(Palette.Grid);
            card.Chart.BaseBrush = new SolidColorBrush(Palette.Base);
        }

        // 事件卡片：深色下比底色略亮，浅色下比底色略深，边框再提一档
        EventCard.Background = new SolidColorBrush(light ? Color.Parse("#EDE5D3") : Color.Parse("#17140F"));
        EventCard.BorderBrush = new SolidColorBrush(light ? Color.Parse("#D5CBB6") : Color.Parse("#2E2820"));

        WxIcon.InvalidateVisual();
        Sun.InvalidateVisual();
        Hourly.InvalidateVisual();
        AlertPanel.InvalidateVisual();

        foreach (var c in _charts.Values) c.Chart.InvalidateVisual();
    }


    /// <summary>强调色在两套主题下各自取一版，保持色相不变。</summary>
    private static Color AccentNow(Color dark)
    {
        if (!Palette.IsLight) return dark;
        double h = 0, sat = 0, val = 0;
        ToHsv(dark, out h, out sat, out val);
        return FromHsv(h, Math.Min(1, sat * 0.9), val * 0.62);
    }


    private static void ToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        v = max;
        double d = max - min;
        s = max == 0 ? 0 : d / max;
        if (d == 0) { h = 0; return; }
        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }


    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

}
