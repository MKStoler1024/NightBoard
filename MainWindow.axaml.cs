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

public partial class MainWindow : Window
{
    private static readonly FontFamily FontUi = new("avares://NightBoard/Assets/Fonts#HarmonyOS Sans SC");
    private static readonly FontFamily FontLight = new("avares://NightBoard/Assets/Fonts#HarmonyOS Sans SC Light");
    private static readonly FontFamily FontMedium = new("avares://NightBoard/Assets/Fonts#HarmonyOS Sans SC Medium");

    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor NoCursor = new(StandardCursorType.None);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private const string CpuKey = "__cpu";
    private const string MemKey = "__mem";

    private sealed class ChartCard
    {
        public required Border Root { get; init; }
        public required Sparkline Chart { get; init; }
        public required TextBlock Value { get; init; }
        public required TextBlock Title { get; init; }
        public required Color Accent { get; init; }
    }

    private readonly SystemSampler _sampler = new(Program.GpuFilter);
    private readonly Dictionary<string, ChartCard> _charts = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _cursorIdle;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _slowTimer;

    private string _chartSignature = "";
    private string _alertSignature = "";
    private string _dailySignature = "";
    private string _hourlySignature = "";
    private double _scale = 1.0;
    private double _dim;
    private bool _autoTheme = Program.AutoTheme;
    private bool _hourlyScrolled;
    private AppSettings _settings = AppSettings.Load();
    private string _cdSignature = "";
    private string _calSignature = "";
    private string _festDay = "";
    private (string Name, DateTime Date) _festival;
    private (string Name, DateTime Date)[] _festivals = Array.Empty<(string Name, DateTime Date)>();
    private (string Name, DateTime At) _moonPhase = ("", DateTime.MinValue);
    private (string Name, DateTime Date) _term;

    public MainWindow()
    {
        InitializeComponent();

        _cursorIdle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _cursorIdle.Tick += (_, _) =>
        {
            _cursorIdle.Stop();
            HideCursor();
        };
        AddHandler(PointerMovedEvent, (_, _) => ShowCursorTemporarily(), RoutingStrategies.Tunnel);

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => Tick();
        Tick();
        _clockTimer.Start();

        _slowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _slowTimer.Tick += (_, _) =>
        {
            WeatherService.Tick();
            IcsCalendar.Refresh();
        };
        _slowTimer.Start();

        _sampler.Updated += snap => Dispatcher.UIThread.Post(() => Apply(snap));
        _sampler.Start();

        WeatherBlock.PointerPressed += (_, e) =>
        {
            WeatherService.RefreshNow();
            Set(WxWind, "刷新中…");
            PulseIcon();
            e.Handled = true;
        };
        WeatherBlock.PointerEntered += (_, _) => Cursor = HandCursor;
        WeatherBlock.PointerExited += (_, _) => ShowCursorTemporarily();

        AlertPanel.PointerPressed += (_, e) =>
        {
            var alerts = WeatherService.Current.Alerts;
            if (alerts is { Count: > 0 })
            {
                new AlertWindow(alerts).ShowDialog(this);
                e.Handled = true;
            }
        };
        AlertPanel.PointerEntered += (_, _) => Cursor = HandCursor;
        AlertPanel.PointerExited += (_, _) => ShowCursorTemporarily();

        // 逐小时那条能横向滚：滚轮一转就横着走
        HourlyScroll.SizeChanged += (_, _) =>
        {
            Hourly.ViewportWidth = HourlyScroll.Viewport.Width;
            Hourly.InvalidateVisual();
        };

        HourlyScroll.ScrollChanged += (_, _) =>
        {
            Hourly.OffsetX = HourlyScroll.Offset.X;
            Hourly.ViewportWidth = HourlyScroll.Viewport.Width;
            Hourly.InvalidateVisual();
        };

        HourlyScroll.PointerWheelChanged += (_, e) =>
        {
            HourlyScroll.Offset = new Vector(
                Math.Max(0, HourlyScroll.Offset.X - e.Delta.Y * 60), 0);
            e.Handled = true;
        };

        Footer.PointerEntered += (_, _) => Footer.Opacity = 1;
        Footer.PointerExited += (_, _) => Footer.Opacity = 0.62;
        ModeButton.Click += (_, _) => ToggleMode();
        PrefsButton.Click += (_, _) => OpenSettings();
        QuitButton.Click += (_, _) => ConfirmExit();
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) SyncMode(); };
        SyncMode();
        SizeChanged += (_, _) => ApplyScale();
        PositionChanged += (_, _) => ApplyScale();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Closing += (_, _) => _sampler.Dispose();
        Palette.Changed += ApplyTheme;

        // 先把日程扫一遍，免得开板头十秒是空的
        IcsCalendar.Refresh(force: true);

        ApplyScale();
        ApplyTheme();
        Root.Opacity = 0;
        Opened += (_, _) =>
        {
            SyncMode();
            DispatcherTimer.RunOnce(PlayEntrance, TimeSpan.FromMilliseconds(250));
        };
        UpdateCursor();
        Focus();

        if (Program.ForceTopmost) Topmost = true;

        // 调试用：铺一遍所有图标
        if (Environment.GetEnvironmentVariable("NB_ICONS") == "1")
            DispatcherTimer.RunOnce(() => new IconSheetWindow().Show(this), TimeSpan.FromSeconds(1.5));

        // 调试用：直接弹预警详情
        if (Environment.GetEnvironmentVariable("NB_ALERTS") == "1")
            DispatcherTimer.RunOnce(() =>
            {
                var al = WeatherService.Current.Alerts;
                if (al is { Count: > 0 }) new AlertWindow(al).ShowDialog(this);
            }, TimeSpan.FromSeconds(2));

        // 调试用：直接弹退出确认
        if (Environment.GetEnvironmentVariable("NB_EXIT") == "1")
            DispatcherTimer.RunOnce(ConfirmExit, TimeSpan.FromSeconds(1.6));

        // 调试用：用假数据看一眼预警详情的样子
        if (Environment.GetEnvironmentVariable("NB_ALERTDEMO") == "1")
            DispatcherTimer.RunOnce(() =>
            {
                var demo = new List<WeatherAlert>
                {
                    new("哈尔滨市气象台发布暴雪橙色预警", "暴雪", "橙色",
                        "预计未来 6 小时内降雪量将达 10 毫米以上，可能对交通或者农牧业有较大影响。请有关单位和个人注意做好预防工作。", DateTime.Now.AddHours(-1)),
                    new("大风蓝色预警信号", "大风", "蓝色",
                        "预计今天夜间到明天白天，平均风力可达 5 级以上，阵风 7 级。请注意防范。", DateTime.Now.AddHours(-3)),
                };
                new AlertWindow(demo).ShowDialog(this);
            }, TimeSpan.FromSeconds(2));

        // 调试用：直接弹设置页
        if (Environment.GetEnvironmentVariable("NB_SETTINGS") == "1")
            DispatcherTimer.RunOnce(OpenSettings, TimeSpan.FromSeconds(1.5));

        if (Environment.GetEnvironmentVariable("NB_DIAG") == "1")
        {
            double delay = double.TryParse(Environment.GetEnvironmentVariable("NB_DIAGDELAY"), out var d) ? d : 6;
            DispatcherTimer.RunOnce(DumpLayout, TimeSpan.FromSeconds(delay));
        }
    }

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

    // ------------------------------------------------------------ 每秒
    private void Tick()
    {
        var now = DateTime.Now;

        string hh = now.ToString("HH"), mm = now.ToString("mm"), ss = now.ToString("ss");
        if (ClockH.Text != hh) ClockH.Text = hh;
        if (ClockM.Text != mm) ClockM.Text = mm;
        if (ClockS.Text != ss) ClockS.Text = ss;

        ClockColon.Opacity = now.Second % 2 == 0 ? 1.0 : 0.18;

        string date = now.ToString("yyyy.MM.dd") + "    星期" + "日一二三四五六"[(int)now.DayOfWeek];
        if (DateLine.Text != date) DateLine.Text = date;

        string lunar = Lunar.Describe(now);
        if (LunarLine.Text != lunar) LunarLine.Text = lunar;

        Hitokoto.Tick();
        var (quote, from) = Hitokoto.Current;
        string fromLine = string.IsNullOrWhiteSpace(from) ? "" : "—— " + from;

        if (QuoteText.Text != quote || QuoteFrom.Text != fromLine)
        {
            // 第一句直接落上去，之后每次换句都做一次淡出再淡入
            if (string.IsNullOrWhiteSpace(QuoteText.Text) || QuoteText.Text == " ")
            {
                QuoteText.Text = quote;
                QuoteFrom.Text = fromLine;
            }
            else
            {
                AnimateQuote(quote, fromLine);
            }
        }

        if (_autoTheme) UpdateAutoTheme(now);

        UpdateCountdown(now);
        UpdateCalendar(now);
        UpdateSun(now);
        UpdateWeatherUi(now);
        UpdateForecast(now);
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

    /// <summary>「距 春节  还有 68 天」。</summary>
    private void UpdateCountdown(DateTime now)
    {
        string name = (_settings.CountdownName ?? "").Trim();
        string dateText = (_settings.CountdownDate ?? "").Trim();

        string sig = name + "|" + dateText + "|" + now.Date.ToString("yyyyMMdd");
        if (sig == _cdSignature) return;
        _cdSignature = sig;

        if (name.Length == 0 || !DateTime.TryParse(dateText, out var target))
        {
            CountdownPanel.IsVisible = false;
            return;
        }

        CountdownPanel.IsVisible = true;
        int days = (target.Date - now.Date).Days;

        CdName.Text = name;

        if (days > 0)
        {
            CdMid.Text = "还有";
            CdValue.Text = days.ToString();
            CdUnit.Text = "天";
        }
        else if (days == 0)
        {
            CdMid.Text = "就是今天";
            CdValue.Text = "";
            CdUnit.Text = "";
        }
        else
        {
            CdMid.Text = "已过";
            CdValue.Text = (-days).ToString();
            CdUnit.Text = "天";
        }
    }

    /// <summary>节日 / 节气 / 下一件日程。</summary>
    private void UpdateCalendar(DateTime now)
    {
        // 节日和节气一天算一次就够（要翻几百天，每秒算太浪费）
        string today = now.ToString("yyyyMMdd");
        if (today != _festDay)
        {
            _festDay = today;
            _festivals = Festivals.Upcoming(now, 2).ToArray();
            _festival = _festivals.Length > 0 ? _festivals[0] : ("", default);
            _moonPhase = Astro.NextMoonPhase(now);
            _term = SolarTerms.Next(now);
            _calSignature = "";
        }

        int fd = _festival.Date == default ? 0 : (_festival.Date.Date - now.Date).Days;
        if (_festival.Name.Length > 0)
        {
            FestName.Text = _festival.Name;
            FestDate.Text = _festival.Date.ToString("MM-dd");
            FestLeft.Text = fd == 0 ? "就是今天" : $"{fd} 天后";
        }
        else
        {
            FestName.Text = "";
            FestDate.Text = "";
            FestLeft.Text = "";
        }

        int td = _term.Date == default ? 0 : (_term.Date.Date - now.Date).Days;
        if (_term.Name.Length > 0)
        {
            TermName.Text = _term.Name;
            TermDate.Text = _term.Date.ToString("MM-dd");
            TermLeft.Text = td == 0 ? "就是今天" : $"{td} 天后";
        }
        else
        {
            TermName.Text = "";
            TermDate.Text = "";
            TermLeft.Text = "";
        }

        var after = _festivals.Length > 1 ? _festivals[1] : ("", default);
        if (after.Item1.Length > 0)
        {
            AfterName.Text = after.Item1;
            AfterDate.Text = after.Item2.ToString("MM-dd");
            int ad = (after.Item2.Date - now.Date).Days;
            AfterLeft.Text = ad == 0 ? "就是今天" : $"{ad} 天后";
        }
        else
        {
            AfterName.Text = "";
            AfterDate.Text = "";
            AfterLeft.Text = "";
        }

        if (_moonPhase.Item1.Length > 0)
        {
            var span = _moonPhase.Item2 - now;
            MoonPhaseLabel.Text = $"下次{_moonPhase.Item1}";
            MoonPhaseDate.Text = _moonPhase.Item2.ToString(span.TotalDays >= 1 ? "MM-dd" : "HH:mm");
            MoonPhaseLeft.Text = span.TotalDays >= 1
                ? $"{(int)span.TotalDays} 天后"
                : $"{Math.Max(1, (int)span.TotalHours)} 小时后";
        }
        else
        {
            MoonPhaseLabel.Text = "下次月相";
            MoonPhaseDate.Text = "";
            MoonPhaseLeft.Text = "";
        }

        var ev = IcsCalendar.NextEvent(now);
        string sig = ev is null ? "" : $"{ev.Title}|{ev.Start:yyyyMMddHHmm}|{ev.End:HHmm}|{now.Hour}{now.Minute / 10}";
        if (sig == _calSignature) return;
        _calSignature = sig;

        if (ev is null)
        {
            EventCard.IsVisible = false;
            return;
        }

        EventCard.IsVisible = true;

        bool running = ev.AllDay ? ev.Start.Date <= now.Date : ev.Start <= now;
        if (running)
        {
            EvTime.Text = ev.AllDay ? "全天" : $"到 {ev.End:HH:mm}";
            EvTitle.Text = ev.Title;
            EvWhen.Text = "进行中";
        }
        else
        {
            var left = ev.Start - now;
            string span = left.TotalHours >= 1
                ? $"{(int)left.TotalHours} 小时 {left.Minutes} 分后"
                : $"{Math.Max(1, left.Minutes)} 分钟后";

            EvTime.Text = ev.Start.Date == now.Date
                ? ev.Start.ToString("HH:mm")
                : ev.Start.ToString("MM-dd HH:mm");
            EvTitle.Text = ev.Title;
            EvWhen.Text = span;
        }
    }

    private void UpdateSun(DateTime now)
    {
        var w = WeatherService.Current;
        Sun.Rise = w.Sunrise;
        Sun.Set = w.Sunset;
        Sun.Now = now;

        string caption;
        if (w.Sunrise == DateTime.MinValue || w.Sunset <= w.Sunrise)
        {
            caption = "";
        }
        else if (now < w.Sunrise)
        {
            caption = $"距天亮 {Fmt(w.Sunrise - now)}";
        }
        else if (now > w.Sunset)
        {
            caption = $"距天亮 {Fmt(w.Sunrise.AddDays(1) - now)}";
        }
        else
        {
            caption = $"距日落 {Fmt(w.Sunset - now)}";
        }

        if (Sun.Caption != caption) Sun.Caption = caption;
    }

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
        Set(WxCity, w.City);
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
                Fill = new SolidColorBrush(AlertChip.ColorOf(a.Level), 0.9),
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

    // ------------------------------------------------------------ 图表
    private ChartCard BuildCard(string key, string title, Color accent, string? tip = null)
    {
        var titleTb = new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush(Palette.Dim),
            FontFamily = FontMedium,
            LetterSpacing = 1.2,
            FontSize = 16 * _scale,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
        };
        Palette.Track(titleTb, Palette.Role.Dim);

        var valueTb = new TextBlock
        {
            Text = "—",
            Foreground = new SolidColorBrush(Palette.Ink),
            FontFamily = FontLight,
            FontSize = 27 * _scale,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
        };
        Palette.Track(valueTb, Palette.Role.Ink);

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(titleTb, 0);
        Grid.SetColumn(valueTb, 2);
        head.Children.Add(titleTb);
        head.Children.Add(valueTb);

        var chart = new Sparkline
        {
            Stroke = new SolidColorBrush(accent),
            Fill = null,
            Capacity = 360,
            // 固定 0–100% 满量程：曲线高度只反映负载，不随峰值来回伸缩
            AutoScale = false,
            GridBrush = new SolidColorBrush(Palette.Grid),
            PlateBrush = new SolidColorBrush(Palette.Plate),
            BaseBrush = new SolidColorBrush(Palette.Base),
            Margin = new Thickness(0, 10 * _scale, 0, 0),
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(head, 0);
        Grid.SetRow(chart, 1);
        grid.Children.Add(head);
        grid.Children.Add(chart);

        var root = new Border { Child = grid, Margin = new Thickness(0, 0, 0, 24 * _scale) };
        if (tip is not null) ToolTip.SetTip(root, tip);

        _charts[key] = new ChartCard
        {
            Root = root,
            Chart = chart,
            Value = valueTb,
            Title = titleTb,
            Accent = accent,
        };

        return _charts[key];
    }

    private void RebuildCharts(IReadOnlyList<GpuSample> gpus)
    {
        string sig = string.Join("|", gpus.Select(g => g.Key));
        if (sig == _chartSignature && _charts.Count > 0) return;
        _chartSignature = sig;

        ChartsHost.Children.Clear();
        ChartsHost.RowDefinitions.Clear();
        _charts.Clear();

        BuildCard(CpuKey, "CPU", Palette.Cpu);

        // 有几块卡就画几张，标题直接用卡名——不预设"一定有独显"
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in gpus)
        {
            string title = $"GPU · {g.Label}";
            if (!used.Add(title)) title = $"{title} · 2";
            BuildCard(g.Key, title, g.Kind == "igpu" ? Palette.Igpu : Palette.Dgpu, g.Name);
        }

        BuildCard(MemKey, "内存", Palette.Mem);

        int i = 0;
        foreach (var card in _charts.Values)
        {
            ChartsHost.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
            Grid.SetRow(card.Root, i++);
            ChartsHost.Children.Add(card.Root);
        }
    }

    // ------------------------------------------------------------ 数据
    private void Apply(Snapshot s)
    {
        RebuildCharts(s.Gpus);

        if (_charts.TryGetValue(CpuKey, out var cpuCard))
        {
            cpuCard.Chart.Values = s.CpuHist;
            cpuCard.Value.Text = $"{s.Cpu:F1}%";
        }

        foreach (var g in s.Gpus)
        {
            if (!_charts.TryGetValue(g.Key, out var card)) continue;
            card.Chart.Values = g.Hist;

            // 被系统关掉的卡不能显示成 0%——那不是闲，是没开机
            if (g.State == "run")
            {
                card.Value.Text = $"{g.Util:F1}%";
                card.Value.Foreground = new SolidColorBrush(Palette.Ink);
            }
            else
            {
                card.Value.Text = g.State == "sleep" ? "休眠" : "已关闭";
                card.Value.Foreground = new SolidColorBrush(Palette.Dim);
            }
        }

        if (_charts.TryGetValue(MemKey, out var memCard))
        {
            memCard.Chart.Values = s.MemHist;
            memCard.Value.Text = $"{s.MemPct:0}%   {s.MemUsedGb}/{s.MemTotalGb}G";
        }

        Set(AbText, s.AbOnline ? "AstrBot 运行中" : "AstrBot 未运行");
        AbLed.Fill = new SolidColorBrush(Color.Parse(s.AbOnline ? "#8FAE72" : "#B96A5A"));

        var abBits = new List<string>();
        if (s.AbOnline)
        {
            if (s.AbMemMb > 0) abBits.Add($"{s.AbMemMb:F0}MB");
            if (!string.IsNullOrEmpty(s.AbUptime)) abBits.Add(s.AbUptime);
        }
        Set(AbMeta, string.Join("  ·  ", abBits));

        // 电量
        if (s.BatteryPct == null)
        {
            Set(MetaBat, "—");
        }
        else if (s.BatteryPlugged == true)
        {
            Set(MetaBat, $"电量 {s.BatteryPct}%  已接通");
        }
        else if (s.BatterySecs is > 0)
        {
            var t = TimeSpan.FromSeconds(s.BatterySecs.Value);
            Set(MetaBat, $"电量 {s.BatteryPct}%  剩 {t.Hours}h{t.Minutes:D2}m");
        }
        else
        {
            Set(MetaBat, $"电量 {s.BatteryPct}%");
        }
    }

    private static void Set(TextBlock? tb, string text)
    {
        if (tb != null && tb.Text != text) tb.Text = text;
    }

    // ------------------------------------------------------------ 交互
    private WindowState _restoreMode = WindowState.Normal;
    private double _bottomGap = 48;
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
