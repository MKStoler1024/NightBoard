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

        DialogAppearance.Apply(this);
        Opened += (_, _) => DialogAppearance.Apply(this);
        Cursor = new Cursor(StandardCursorType.Arrow);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(false);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && FocusManager?.GetFocusedElement() != CityBox)
            {
                OnSave(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };
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

    private async void OnSearchCity(object? sender, RoutedEventArgs e)
    {
        if (!SearchButton.IsEnabled) return;
        Feedback.Text = "正在查找城市…";
        SearchButton.IsEnabled = false;
        var found = await WeatherService.SearchCitiesAsync(CityBox.Text ?? "");
        SearchButton.IsEnabled = true;

        CityList.ItemsSource = found;
        CityList.IsVisible = found.Count > 0;

        Feedback.Text = found.Count == 0 ? "没有找到城市，请检查名称或稍后再试。" : $"找到 {found.Count} 个结果，请选择城市。";
    }

    private void OnCityPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (CityList.SelectedItem is not CityCandidate c) return;

        Feedback.Text = "已选择城市，保存后生效。";
        _picked = c;
        CityBox.Text = c.Name;
        LatBox.Text = c.Lat.ToString("0.####", CultureInfo.InvariantCulture);
        LonBox.Text = c.Lon.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if ((!string.IsNullOrWhiteSpace(LatBox.Text) && (!TryNumber(LatBox.Text, out var la) || !double.IsFinite(la) || Math.Abs(la) > 90)) ||
            (!string.IsNullOrWhiteSpace(LonBox.Text) && (!TryNumber(LonBox.Text, out var lo) || !double.IsFinite(lo) || Math.Abs(lo) > 180)))
        { Feedback.Text = "纬度应在 −90 到 90 之间，经度应在 −180 到 180 之间。"; Feedback.BringIntoView(); return; }
        if (!string.IsNullOrWhiteSpace(CdNameBox.Text) && !DateTime.TryParseExact(CdDateBox.Text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        { Feedback.Text = "请填写有效的倒计时日期，格式为 yyyy-MM-dd。"; Feedback.BringIntoView(); return; }
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
