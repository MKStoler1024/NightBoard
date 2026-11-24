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

    // ------------------------------------------------------------ 动画
    private static double Ease(double p) => 1 - Math.Pow(1 - p, 3);


    private static void Tween(double seconds, Action<double> step, Action? done = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double p = Math.Min(1, watch.Elapsed.TotalSeconds / seconds);
            step(p);
            if (p < 1) return;
            timer.Stop();
            done?.Invoke();
        };
        timer.Start();
    }


    /// <summary>开机：整块从略下方浮上来，淡入。</summary>
    private void PlayEntrance()
    {
        var shift = new TranslateTransform(0, 14);
        Root.RenderTransform = shift;
        Root.Opacity = 0;

        Tween(1.2, p =>
        {
            double e = Ease(p);
            Root.Opacity = e;
            shift.Y = 14 * (1 - e);
        });
    }


    /// <summary>一言换句：先淡出并微微上移，换完字再落下来淡入。</summary>
    private void AnimateQuote(string quote, string from)
    {
        var up = new TranslateTransform(0, 0);
        QuoteText.RenderTransform = up;
        QuoteFrom.RenderTransform = up;

        Tween(0.22, p =>
        {
            double e = Ease(p);
            QuoteText.Opacity = 1 - e;
            QuoteFrom.Opacity = 1 - e;
            up.Y = -6 * e;
        }, () =>
        {
            QuoteText.Text = quote;
            QuoteFrom.Text = from;

            var down = new TranslateTransform(0, 6);
            QuoteText.RenderTransform = down;
            QuoteFrom.RenderTransform = down;

            Tween(0.38, p =>
            {
                double e = Ease(p);
                QuoteText.Opacity = e;
                QuoteFrom.Opacity = e;
                down.Y = 6 * (1 - e);
            });
        });
    }


    /// <summary>点天气时图标轻轻一跳。</summary>
    private void PulseIcon()
    {
        var scale = new ScaleTransform(1, 1);
        WxIcon.RenderTransform = scale;
        WxIcon.RenderTransformOrigin = RelativePoint.Center;

        Tween(0.30, p =>
        {
            double s = 1 + 0.10 * Math.Sin(p * Math.PI);
            scale.ScaleX = scale.ScaleY = s;
        });
    }


    // ------------------------------------------------------------ 缩放
    private void ApplyScale()
    {
        double h = Bounds.Height - _bottomGap - 44;
        if (h < 80) return;

        double s = _scale = Math.Min(h / 990.0, Bounds.Width / 1600.0) * Program.UiScale;

        Root.Margin = new Thickness(80 * s, 46 * s, 80 * s, _bottomGap + 40 * s);
        Footer.Margin = new Thickness(0, 0, 80 * s, _bottomGap + 14 * s);
        DockRule.Height = 14 * s;
        DockRule.Margin = new Thickness(5 * s, 0);
        Footer.Padding = new Thickness(3 * s);
        Footer.CornerRadius = new CornerRadius(99 * s);
        foreach (var b in new[] { ModeButton, PrefsButton, QuitButton })
        {
            b.FontSize = 13 * s;
            b.Padding = new Thickness(13 * s, 6 * s);
            b.MinHeight = 30 * s;
            b.CornerRadius = new CornerRadius(18 * s);
        }

        double clock = 172 * s;
        ClockH.FontSize = clock;
        ClockM.FontSize = clock;
        ClockColon.FontSize = clock * 0.86;
        ClockColon.Margin = new Thickness(8 * s, 0);
        ClockS.FontSize = clock * 0.36;
        ClockS.Margin = new Thickness(16 * s, 0, 0, 14 * s);

        GhostH.FontSize = clock;
        GhostM.FontSize = clock;
        GhostColon.FontSize = clock * 0.86;
        GhostColon.Margin = new Thickness(8 * s, 0);
        GhostS.FontSize = clock * 0.36;
        GhostS.Margin = new Thickness(16 * s, 0, 0, 14 * s);

        DateLine.FontSize = 17 * s;
        DateLine.Margin = new Thickness(4 * s, 16 * s, 0, 0);
        LunarLine.FontSize = 15 * s;
        LunarLine.Margin = new Thickness(4 * s, 6 * s, 0, 0);

        WxIcon.Width = 76 * s;
        WxIcon.Height = 76 * s;
        WxTemp.FontSize = 54 * s;
        WxText.FontSize = 20 * s;

        foreach (var tb in new[] { WxFeels, WxHum, WxAqi, WxWind, WxCity, WxMoon, WxAvg })
            tb.FontSize = 15 * s;

        Sun.Width = 430 * s;
        Sun.MinHeight = 100 * s;
        Sun.Height = 196 * s;

        CdName.FontSize = 20 * s;
        CdValue.FontSize = 31 * s;
        CdValue.FontFamily = FontMedium;
        CdMid.FontSize = 14 * s;
        CdUnit.FontSize = 14 * s;

        foreach (var tb in new[] { FestName, TermName })
            tb.FontSize = 16 * s;

        EvLabel.FontSize = 12 * s;
        EvTitle.FontSize = 16 * s;
        EvTitle.FontFamily = FontMedium;
        EvTime.FontSize = 15 * s;
        EvWhen.FontSize = 13 * s;

        foreach (var run in new[] { FestLabel, TermLabel, FestDate, TermDate, FestLeft, TermLeft, EvWhen })
            run.FontSize = 13.5 * s;

        AbText.FontSize = 15 * s;
        AbMeta.FontSize = 15 * s;
        MetaBat.FontSize = 15 * s;
        MetaSep.FontSize = 15 * s;
        AbLed.Width = AbLed.Height = 8 * s;

        Hourly.Height = 104 * s;
        Hourly.Slot = 26 * s;

        ChartsHost.Margin = new Thickness(64 * s, 0, 0, 0);

        foreach (var card in _charts.Values)
        {
            card.Title.FontSize = 16 * s;
            card.Value.FontSize = 27 * s;
            card.Root.Margin = new Thickness(0, 0, 0, 24 * s);
        }

        QuoteRule.Margin = new Thickness(0, 20 * s, 0, 0);
        QuoteRule.Width = 150 * s;
        QuoteText.FontSize = 22 * s;
        QuoteText.LineHeight = 22 * s * 1.6;
        QuoteText.Margin = new Thickness(0, 14 * s, 0, 0);
        QuoteFrom.FontSize = 14 * s;
        QuoteFrom.Margin = new Thickness(0, 8 * s, 0, 0);

        _alertSignature = "";
        _hourlySignature = "";
    }


    private static void Set(TextBlock? tb, string text)
    {
        if (tb != null && tb.Text != text) tb.Text = text;
    }


    private void UpdateCursor()
    {
        Cursor = WindowState == WindowState.FullScreen ? NoCursor : ArrowCursor;
    }


    /// <summary>鼠标一动就把指针露出来，停三秒再藏——全屏时不用摸黑找鼠标。</summary>
    private void ShowCursorTemporarily()
    {
        if (!ReferenceEquals(Cursor, ArrowCursor) && !ReferenceEquals(Cursor, HandCursor))
            Cursor = ArrowCursor;

        _cursorIdle.Stop();
        _cursorIdle.Start();
    }


    private void HideCursor()
    {
        if (WindowState == WindowState.FullScreen) Cursor = NoCursor;
    }


    private async void OpenSettings()
    {
        var win = new SettingsWindow();
        bool saved = await win.ShowDialog<bool>(this);
        if (!saved) return;

        var s = win.Settings;
        s.Save();
        _settings = s;
        _cdSignature = "";
        s.Apply();

        _autoTheme = s.Theme == "auto";
        if (_autoTheme) UpdateAutoTheme(DateTime.Now);
        else ApplyThemeAnimated(s.Theme == "light");

        WeatherService.Reload();

        // 换了城市/坐标，天气和警报都得重画
        _alertSignature = "";
        _chartSignature = "";
        _hourlySignature = "";
        _dailySignature = "";
        Sun.InvalidateVisual();
    }


    private void ToggleMode()
    {
        if (WindowState == WindowState.FullScreen) WindowState = _restoreMode;
        else { _restoreMode = WindowState; WindowState = WindowState.FullScreen; }
    }


    private void SyncMode()
    {
        bool full = WindowState == WindowState.FullScreen;
        ModeButton.Content = full ? "窗口化" : "全屏";
        _bottomGap = full ? 48 : 0;
        UpdateCursor();
        ApplyScale();
    }


    private async void ConfirmExit()
    {
        var stay = new Button { Classes = { "board", "primary" }, Content = "继续看板" };
        var quit = new Button { Classes = { "board", "text" }, Content = "退出夜板" };

        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { quit, stay },
        };

        var title = new TextBlock
        {
            Text = "退出夜板？",
            FontSize = 20,
            Foreground = new SolidColorBrush(Palette.Ink),
            FontFamily = FontMedium,
        };

        var body = new TextBlock
        {
            Text = "看板会关掉，下次从快捷方式再开。",
            FontSize = 13,
            Foreground = new SolidColorBrush(Palette.Dim),
        };

        var stack = new StackPanel { Spacing = 10, Children = { title, body } };
        var panel = new StackPanel { Spacing = 22, Children = { stack, actions } };

        var card = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Palette.Line),
            Background = new SolidColorBrush(Palette.IsLight ? Color.Parse("#F6F2E9") : Color.Parse("#0C0B09")),
            Padding = new Thickness(26, 22),
            Child = panel,
        };

        var dialog = new Window
        {
            SystemDecorations = SystemDecorations.None,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            Background = Brushes.Transparent,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = FontUi,
            RequestedThemeVariant = Palette.IsLight ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark,
            Content = card,
            Topmost = Topmost,
        };

        dialog.PointerPressed += (_, e) =>
        {
            if (e.Handled) return;
            if (e.Source is Button) return;
            if (e.GetCurrentPoint(dialog).Properties.IsLeftButtonPressed) dialog.BeginMoveDrag(e);
        };

        stay.Click += (_, _) => dialog.Close(false);
        quit.Click += (_, _) => dialog.Close(true);
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) dialog.Close(false);
            else if (e.Key == Key.Enter) dialog.Close(false);
        };
        dialog.Opened += (_, _) => stay.Focus();

        if (await dialog.ShowDialog<bool>(this)) Close();
    }


    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                ToggleMode();
                e.Handled = true;
                break;

            case Key.Escape:
                if (WindowState == WindowState.FullScreen)
                {
                    ToggleMode();
                }
                e.Handled = true;
                break;

            case Key.S:
                OpenSettings();
                e.Handled = true;
                break;

            case Key.T:
                _autoTheme = false;
                ApplyThemeAnimated(!Palette.IsLight);
                e.Handled = true;
                break;

            case Key.A:
                _autoTheme = !_autoTheme;
                if (_autoTheme) UpdateAutoTheme(DateTime.Now);
                e.Handled = true;
                break;

            case Key.OemMinus:
            case Key.Subtract:
                _dim = Math.Min(0.9, _dim + 0.1);
                DimLayer.Opacity = _dim;
                e.Handled = true;
                break;

            case Key.OemPlus:
            case Key.Add:
                _dim = Math.Max(0, _dim - 0.1);
                DimLayer.Opacity = _dim;
                e.Handled = true;
                break;

            case Key.D0:
            case Key.NumPad0:
                _dim = 0;
                DimLayer.Opacity = 0;
                e.Handled = true;
                break;

            case Key.Q when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                ConfirmExit();
                e.Handled = true;
                break;
        }
    }


    // ------------------------------------------------------------ 诊断
    private void DumpLayout()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"window={Bounds.Width:F0}x{Bounds.Height:F0} scale={_scale:F3} light={Palette.IsLight}");
        sb.AppendLine($"renderScaling={RenderScaling:F3} clockFont={ClockH.FontSize:F1}");

        void Dump(Visual? v, string name)
        {
            if (v is null) { sb.AppendLine($"{name}: null"); return; }
            var b = v.Bounds;
            var p = v.TranslatePoint(new Point(0, 0), this);
            sb.AppendLine($"{name}: {b.Width:F0}x{b.Height:F0} @ ({p?.X:F0},{p?.Y:F0})");
        }

        Dump(ClockH, "ClockH");
        Dump(WeatherBlock, "WeatherBlock");
        Dump(Hourly, "Hourly");
        Dump(Sun, "Sun");
        Dump(ChartsHost, "ChartsHost");

        var w = WeatherService.Current;
        sb.AppendLine($"weather: {w.City} {w.Temp}° {w.Text} aqi={w.Aqi} wind={w.Wind}");
        sb.AppendLine($"hourly={WeatherService.Hourly.Count} daily={WeatherService.Daily.Count}");

        foreach (var g in _sampler.GpuAdapters)
            sb.AppendLine($"gpu {g.Label} {g.Name} present={g.Present} state={g.State} " +
                          $"util={g.Util:F1} counterAge={(DateTime.UtcNow - g.LastCounterUtc).TotalSeconds:F0}s");

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nb-diag.txt"), sb.ToString());
    }

}
