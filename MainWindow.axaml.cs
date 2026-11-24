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

        // 换显示器、改缩放比之后重新算一遍 UI 倍率
        Screens.Changed += (_, _) => Dispatcher.UIThread.Post(ApplyScale);
        PositionChanged += (_, _) => Dispatcher.UIThread.Post(ApplyScale);

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








    private static readonly Dictionary<string, Avalonia.Media.SolidColorBrush> _alertBrushes = new();














    // ------------------------------------------------------------ 交互
    private WindowState _restoreMode = WindowState.Normal;
    private double _bottomGap = 48;









}
