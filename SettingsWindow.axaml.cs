using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using NightBoard.Services;

namespace NightBoard;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; private set; } = new();

    private CityCandidate? _picked;

    /// <summary>保存后被外面读到的主题（auto / light / dark）。</summary>
    public new string Theme => Settings.Theme;

    public SettingsWindow()
    {
        InitializeComponent();

        // 看板是全屏置顶的，设置窗也得压在上面，否则点不到
        Topmost = true;
        Cursor = new Cursor(StandardCursorType.Arrow);

        // 圆角窗口：让系统不画背景，角落才透得出来
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        AddHandler(PointerPressedEvent, OnDragWindow, RoutingStrategies.Bubble);

        var s = AppSettings.Load();

        CityBox.Text = string.IsNullOrWhiteSpace(WeatherService.CityName)
            ? s.City
            : WeatherService.CityName;

        double lat = WeatherService.LatOverride ?? WeatherService.Latitude;
        double lon = WeatherService.LonOverride ?? WeatherService.Longitude;
        LatBox.Text = double.IsNaN(lat) ? "" : lat.ToString("0.####", CultureInfo.InvariantCulture);
        LonBox.Text = double.IsNaN(lon) ? "" : lon.ToString("0.####", CultureInfo.InvariantCulture);

        // 调试用：NB_CITYQ 直接搜一次
        var q = Environment.GetEnvironmentVariable("NB_CITYQ");
        if (!string.IsNullOrWhiteSpace(q))
        {
            CityBox.Text = q;
            Avalonia.Threading.DispatcherTimer.RunOnce(
                () => OnSearchCity(this, new RoutedEventArgs()), TimeSpan.FromMilliseconds(400));
        }

        CityBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) OnSearchCity(this, new RoutedEventArgs());
        };

        CdNameBox.Text = s.CountdownName;
        CdDateBox.Text = s.CountdownDate;

        ThemeBox.SelectedIndex = s.Theme switch
        {
            "light" => 2,
            "dark" => 1,
            _ => 0,
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Recenter();
    }

    /// <summary>CenterScreen 在 150% 缩放下会偏，索性自己按工作区算一次。内容变高后也要重算。</summary>
    private void Recenter()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;

        var wa = screen.WorkingArea;
        double s = screen.Scaling <= 0 ? 1 : screen.Scaling;

        // Position 在这里是按逻辑像素生效的（平台会再乘一遍缩放），所以先除回去
        double left = wa.X / s + Math.Max(0, (wa.Width / s - ClientSize.Width) / 2);
        double top = wa.Y / s + Math.Max(0, (wa.Height / s - ClientSize.Height) / 2);

        Position = new PixelPoint((int)Math.Round(left), (int)Math.Round(top));
    }

    private async void OnSearchCity(object? sender, RoutedEventArgs e)
    {
        SearchButton.IsEnabled = false;
        var found = await WeatherService.SearchCitiesAsync(CityBox.Text ?? "");
        SearchButton.IsEnabled = true;

        CityList.ItemsSource = found;
        CityList.IsVisible = found.Count > 0;

        // 列表一出现窗口就变高，重新居中一次
        Avalonia.Threading.Dispatcher.UIThread.Post(Recenter);
    }

    private void OnCityPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (CityList.SelectedItem is not CityCandidate c) return;

        _picked = c;
        CityBox.Text = c.Name;
        LatBox.Text = c.Lat.ToString("0.####", CultureInfo.InvariantCulture);
        LonBox.Text = c.Lon.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private void OnDragWindow(object? sender, PointerPressedEventArgs e)
    {
        // 只拖空白处；按钮、输入框已经处理过的就别抢
        if (e.Handled) return;
        if (e.Source is Button or TextBox or ComboBox or ListBox or ScrollViewer) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var s = new AppSettings
        {
            City = (CityBox.Text ?? "").Trim(),
            CountdownName = (CdNameBox.Text ?? "").Trim(),
            CountdownDate = (CdDateBox.Text ?? "").Trim(),
            Theme = ThemeBox.SelectedIndex switch
            {
                1 => "dark",
                2 => "light",
                _ => "auto",
            },
        };

        if (TryNumber(LatBox.Text, out double lat)) s.Lat = lat;
        if (TryNumber(LonBox.Text, out double lon)) s.Lon = lon;

        // 搜索选过、而且名字没再改，就带上 locationKey
        if (_picked != null && string.Equals(_picked.Name, s.City, StringComparison.Ordinal))
            s.Key = _picked.Key;

        Settings = s;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private static bool TryNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }
}
