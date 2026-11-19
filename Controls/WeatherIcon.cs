using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NightBoard.Services;

namespace NightBoard.Controls;

/// <summary>
/// 天气图标：直接用 Lucide 的图标字体（lucide.ttf 内嵌，ISC 许可），
/// 按天气码取字形、按类别上色。跟 ClassIsland 的做法一致——
/// 自己描路径容易把相对坐标弄错，字体里现成的字形才是"原样的 Lucide"。
/// </summary>
public class WeatherIcon : Control
{
    private static readonly FontFamily IconFont =
        new("avares://NightBoard/Assets/Fonts#lucide");

    /// <summary>天气码 → Lucide 图标名。</summary>
    private static string IconFor(int code, bool night) => code switch
    {
        0 => night ? "moon" : "sun",
        1 => night ? "cloud-moon" : "cloud-sun",
        2 => night ? "cloud-moon" : "cloudy",
        3 => night ? "cloud-moon-rain" : "cloud-sun-rain",
        4 or 5 => "cloud-lightning",
        6 or 19 => "cloud-hail",
        7 or 21 => "cloud-drizzle",
        8 or 9 or 10 or 11 or 12 or 22 or 23 or 24 or 25 or 32 or 301 => "cloud-rain",
        13 or 14 or 15 or 16 or 17 or 26 or 27 or 28 or 34 or 302 => "cloud-snow",
        18 or 35 => "cloud-fog",
        20 or 31 or 33 => "tornado",
        29 or 30 or 53 => "haze",
        _ => "cloud",
    };

    /// <summary>图标名 → 字形码位（当前 lucide.ttf 的对应表）。</summary>
    private static readonly Dictionary<string, string> Glyphs = new()
    {
        ["sun"] = "\ue178",
        ["moon"] = "\ue11e",
        ["cloud"] = "\ue088",
        ["cloud-sun"] = "\ue216",
        ["cloud-moon"] = "\ue215",
        ["cloud-rain"] = "\ue08e",
        ["cloud-drizzle"] = "\ue08a",
        ["cloud-snow"] = "\ue090",
        ["cloud-lightning"] = "\ue08c",
        ["cloud-fog"] = "\ue214",
        ["cloud-hail"] = "\ue08b",
        ["snowflake"] = "\ue165",
        ["wind"] = "\ue1b0",
        ["tornado"] = "\ue218",
        ["haze"] = "\ue0f0",
        ["cloudy"] = "\ue217",
        ["cloud-sun-rain"] = "\ue2fb",
        ["cloud-moon-rain"] = "\ue2fa",
        ["sun-snow"] = "\ue372",
    };

    // 按天气类别上色，压饱和度，跟这块板子的调子配
    private static Color ColorFor(string icon, Color fallback)
    {
        bool light = Palette.IsLight;
        Color pick(string dark, string bright) => Color.Parse(light ? bright : dark);

        return icon switch
        {
            "sun" or "cloud-sun" or "cloud-sun-rain" or "sun-snow" => pick("#E0B65C", "#C08A1E"),
            "moon" or "cloud-moon" or "cloud-moon-rain" => pick("#C9D4E2", "#5F6C80"),
            "cloud" or "cloudy" => pick("#9A9387", "#6E675B"),
            "cloud-drizzle" or "cloud-rain" => pick("#7FA6C9", "#4A7FA5"),
            "cloud-snow" or "snowflake" or "cloud-hail" => pick("#BFD4E6", "#6F8FAA"),
            "cloud-lightning" => pick("#A99BD0", "#6A5B96"),
            "cloud-fog" or "haze" => pick("#B3A996", "#7B7166"),
            "wind" or "tornado" => pick("#CBA97D", "#96754A"),
            _ => fallback,
        };
    }

    public static readonly StyledProperty<int> CodeProperty =
        AvaloniaProperty.Register<WeatherIcon, int>(nameof(Code), 99);

    public static readonly StyledProperty<bool> IsNightProperty =
        AvaloniaProperty.Register<WeatherIcon, bool>(nameof(IsNight));

    /// <summary>直接指定 Lucide 图标名（调试/预览用），设了就不看 Code。</summary>
    public static readonly StyledProperty<string?> IconNameProperty =
        AvaloniaProperty.Register<WeatherIcon, string?>(nameof(IconName));

    static WeatherIcon()
    {
        AffectsRender<WeatherIcon>(CodeProperty, IsNightProperty, IconNameProperty);
    }

    public int Code
    {
        get => GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public bool IsNight
    {
        get => GetValue(IsNightProperty);
        set => SetValue(IsNightProperty, value);
    }

    public string? IconName
    {
        get => GetValue(IconNameProperty);
        set => SetValue(IconNameProperty, value);
    }

    public static IEnumerable<string> Names => Glyphs.Keys;

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 8 || h < 8) return;

        string icon = string.IsNullOrEmpty(IconName) ? IconFor(Code, IsNight) : IconName!;
        if (!Glyphs.TryGetValue(icon, out var glyph)) glyph = Glyphs["cloud"];

        double side = Math.Min(w, h);
        double size = side * 0.92;

        var brush = new SolidColorBrush(ColorFor(icon, Palette.Dim));
        var text = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface(IconFont), size, brush);

        ctx.DrawText(text, new Point((w - text.Width) / 2, (h - text.Height) / 2));
    }
}

/// <summary>预警标签：三角感叹号 + 类型 + 等级，颜色按等级走。</summary>
public class AlertChip : Control
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<AlertChip, string>(nameof(Label), "");

    public static readonly StyledProperty<string> LevelProperty =
        AvaloniaProperty.Register<AlertChip, string>(nameof(Level), "蓝色");

    static AlertChip()
    {
        AffectsRender<AlertChip>(LabelProperty, LevelProperty);
    }

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public static Color ColorOf(string level) => level switch
    {
        "红色" => Color.Parse("#D9534F"),
        "橙色" => Color.Parse("#DE8A3C"),
        "黄色" => Color.Parse("#D9AE38"),
        _ => Color.Parse("#4A90D9"),
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        var text = new FormattedText(Label ?? "", System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 15, Brushes.White);
        return new Size(text.Width + 46, Math.Max(26, text.Height + 10));
    }

    public override void Render(DrawingContext ctx)
    {
        double h = Bounds.Height, w = Bounds.Width;
        if (w < 4 || h < 4) return;

        var color = ColorOf(Level);
        var brush = new SolidColorBrush(color, 0.16);
        var border = new Pen(new SolidColorBrush(color, 0.75), 1);

        ctx.DrawRectangle(brush, border, new Rect(0, 0.5, w, h - 1), 4, 4);

        double tx = 14, ty = h / 2, ts = h * 0.30;
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(new Point(tx, ty - ts), true);
            g.LineTo(new Point(tx + ts * 1.05, ty + ts * 0.85));
            g.LineTo(new Point(tx - ts * 1.05, ty + ts * 0.85));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(color), null, tri);

        var barPen = new Pen(new SolidColorBrush(Palette.IsLight ? Colors.White : Colors.Black), 1.8,
            lineCap: PenLineCap.Round);
        ctx.DrawLine(barPen, new Point(tx, ty - ts * 0.35), new Point(tx, ty + ts * 0.18));
        ctx.DrawEllipse(new SolidColorBrush(Palette.IsLight ? Colors.White : Colors.Black), null,
            new Point(tx, ty + ts * 0.55), 0.9, 0.9);

        var label = new FormattedText(Label ?? "", System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 15,
            new SolidColorBrush(Palette.IsLight ? Color.Parse("#2A2620") : Color.Parse("#D8CFBB")));
        ctx.DrawText(label, new Point(28, (h - label.Height) / 2));
    }
}
