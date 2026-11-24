using System;
using System.Linq;
using Avalonia;

namespace NightBoard;

internal static class Program
{
    /// <summary>由 --topmost 触发：窗口压在所有窗口之上。</summary>
    public static bool ForceTopmost { get; private set; }

    /// <summary>all / igpu / dgpu —— 多卡机器上只显示指定类型。</summary>
    public static string GpuFilter { get; private set; } = "all";

    /// <summary>整体字号/间距倍率，1.0 为基准（1080p 高度）。</summary>
    public static double UiScale { get; private set; } = 1.0;

    [STAThread]
    public static void Main(string[] args)
    {
        Services.CrashLog.Install();

        var list = args.ToList();

        // 先吃用户设置，命令行参数随后可以临时压过它
        var settings = Services.AppSettings.Load();
        settings.Apply();
        if (settings.Theme is "light" or "dark")
        {
            AutoTheme = false;
            Services.Palette.Apply(settings.Theme == "light", force: true);
        }

        if (list.Remove("--topmost") || Environment.GetEnvironmentVariable("NB_TOPMOST") == "1")
            ForceTopmost = true;

        int gi = list.IndexOf("--gpu");
        if (gi >= 0 && gi + 1 < list.Count)
        {
            string v = list[gi + 1].ToLowerInvariant();
            if (v is "all" or "igpu" or "dgpu") GpuFilter = v;
            list.RemoveRange(gi, 2);
        }

        int si = list.IndexOf("--scale");
        if (si >= 0 && si + 1 < list.Count)
        {
            if (double.TryParse(list[si + 1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double k))
                UiScale = Math.Clamp(k, 0.6, 2.0);
            list.RemoveRange(si, 2);
        }

        int ci = list.IndexOf("--city");
        if (ci >= 0 && ci + 1 < list.Count)
        {
            Services.WeatherService.CityName = list[ci + 1];
            list.RemoveRange(ci, 2);
        }

        int ti = list.IndexOf("--theme");
        if (ti >= 0 && ti + 1 < list.Count)
        {
            string v = list[ti + 1].ToLowerInvariant();
            if (v is "light" or "dark")
            {
                // 明确指定主题时就不跟日出日落，否则下一秒就被自动主题覆盖掉
                AutoTheme = false;
                Services.Palette.Apply(v == "light", force: true);
            }
            else
            {
                AutoTheme = true;
            }
            list.RemoveRange(ti, 2);
        }

        Services.WeatherService.Warmup();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(list.ToArray());
    }

    /// <summary>主题是否跟随日出日落。</summary>
    public static bool AutoTheme { get; private set; } = true;

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect();

        if (OperatingSystem.IsWindows())
        {
            builder = builder.With(new Win32PlatformOptions
            {
                // 夜间看板不需要透明/圆角这类合成开销
                CompositionMode = new[] { Win32CompositionMode.RedirectionSurface },
            });
        }

        return builder.LogToTrace();
    }
}
