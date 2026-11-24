using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NightBoard.Services;

namespace NightBoard.Controls;

/// <summary>
/// 日轨图：横轴一整天（0–24 时），纵轴是地平高度。
///
/// 曲线不是画出来的装饰——每一格都是按当前城市的经纬度和当地时间算出来的：
/// 太阳走低精度天文公式，月亮用月球黄经黄纬的主项。
/// 所以冬天太阳拱得低、夏天拱得高；月亮每天晚升约五十分钟，图上看得见。
///
/// 拿到经纬度之前（刚启动、或断网没有城市缓存）退回一天一条正弦的近似，
/// 位置仍是准的，只是峰高按半日正弦估。
///
/// 纵轴不是固定刻度：按当天太阳与月亮的实际极值自适应压缩振幅，
/// 月亮高挂、太阳沉得深时也不会把圆点顶出画布。
/// </summary>
public class SunArc : Control
{
    public static readonly StyledProperty<DateTime> RiseProperty =
        AvaloniaProperty.Register<SunArc, DateTime>(nameof(Rise));

    public static readonly StyledProperty<DateTime> SetProperty =
        AvaloniaProperty.Register<SunArc, DateTime>(nameof(Set));

    public static readonly StyledProperty<DateTime> NowProperty =
        AvaloniaProperty.Register<SunArc, DateTime>(nameof(Now));

    public static readonly StyledProperty<string> CaptionProperty =
        AvaloniaProperty.Register<SunArc, string>(nameof(Caption), "");

    static SunArc()
    {
        AffectsRender<SunArc>(RiseProperty, SetProperty, NowProperty, CaptionProperty);
    }

    public SunArc()
    {
        ClipToBounds = true;
    }

    public DateTime Rise { get => GetValue(RiseProperty); set => SetValue(RiseProperty, value); }
    public DateTime Set { get => GetValue(SetProperty); set => SetValue(SetProperty, value); }
    public DateTime Now { get => GetValue(NowProperty); set => SetValue(NowProperty, value); }
    public string Caption { get => GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    /// <summary>一天 24 小时、每 0.1 时一个点的地平高度（°）。</summary>
    private const int Steps = 241;

    private static void FillAltitudes(double lat, double lon, DateTime day, bool geo,
        DateTime rise, DateTime set, double[] sun, double[] moon)
    {
        double riseH = rise.TimeOfDay.TotalHours;
        double dayHours = (set - rise).TotalHours;
        if (dayHours <= 0.1 || dayHours >= 23.9) dayHours = 12;

        for (int i = 0; i < Steps; i++)
        {
            var t = day.AddHours(i * 0.1);
            if (geo)
            {
                sun[i] = Astro.SunAltitude(t, lat, lon);
                moon[i] = Astro.MoonAltitude(t, lat, lon);
            }
            else
            {
                double hour = i * 0.1;
                sun[i] = 60 * SineAltitude(hour, riseH, dayHours);
                // 近似：按朔望月推月升，可见 12.4 时
                double phase = Astro.MoonIllumination(t);
                moon[i] = 60 * SineAltitude(hour, riseH + phase * 12, 12.4);
            }
        }
    }

    /// <summary>正午在天顶、夜半在地底的近似高度（-1…1）。</summary>
    private static double SineAltitude(double hourOfDay, double riseHour, double visibleHours)
    {
        double d = hourOfDay - riseHour;
        while (d < 0) d += 24;
        while (d >= 24) d -= 24;

        if (d <= visibleHours) return Math.Sin(Math.PI * d / visibleHours);
        return -Math.Sin(Math.PI * (d - visibleHours) / (24 - visibleHours));
    }

    // ---------- 绘图资源缓存：每秒重绘时不再新建笔刷/文字 ----------
    private bool _cacheReady;
    private bool _cacheLight;
    private Color _textColor;
    private Color _captionColor;
    private Pen? _gridPen, _horizonPen, _nowPen;
    private Pen? _sunDimPen, _sunBrightPen, _moonDimPen, _moonBrightPen;
    private LinearGradientBrush? _veilLight, _veilDark;
    private SolidColorBrush? _sunGlowOn, _sunGlowOff, _sunLit, _sunDim;
    private SolidColorBrush? _moonGlowOn, _moonGlowOff, _moonLit, _moonDim;
    private SolidColorBrush? _craterShade;
    private readonly Dictionary<(string, double, bool, uint), FormattedText> _textCache = new();

    private void EnsureCache(bool light)
    {
        if (_cacheReady && _cacheLight == light) return;

        var lineColor = light ? Color.Parse("#A79E8E") : Color.Parse("#4A443A");
        var gridColor = light ? Color.Parse("#C9C2B4") : Color.Parse("#2A2723");
        _textColor = light ? Color.Parse("#6B6459") : Color.Parse("#8A8071");
        _captionColor = light ? Color.Parse("#4A443A") : Color.Parse("#B7AC97");
        var sunColor = light ? Color.Parse("#C08A1E") : Color.Parse("#E0B65C");
        var sunGlow = light ? Color.Parse("#F0DDB0") : Color.Parse("#8A7438");
        var moonColor = light ? Color.Parse("#6B7787") : Color.Parse("#9AA9BC");
        var moonGlow = light ? Color.Parse("#C8D2DE") : Color.Parse("#4E5967");

        _gridPen = new Pen(new SolidColorBrush(gridColor, light ? 0.55 : 0.45), 1);
        _horizonPen = new Pen(new SolidColorBrush(lineColor, 0.8), 1.2);
        _nowPen = new Pen(new SolidColorBrush(lineColor, 0.45), 1);
        _sunDimPen = new Pen(new SolidColorBrush(sunColor, light ? 0.34 : 0.28), 1.8);
        _sunBrightPen = new Pen(new SolidColorBrush(sunColor, light ? 0.95 : 1.0), 3.2,
            lineCap: PenLineCap.Round);
        _moonDimPen = new Pen(new SolidColorBrush(moonColor, light ? 0.28 : 0.24), 1.6);
        _moonBrightPen = new Pen(new SolidColorBrush(moonColor, light ? 0.72 : 0.66), 2.6,
            lineCap: PenLineCap.Round);

        byte top = light ? (byte)0x14 : (byte)0x12;
        _veilLight = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(top, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(0x02, 0, 0, 0), 1),
            },
        };
        _veilDark = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(top, 0xFF, 0xFF, 0xFF), 0),
                new GradientStop(Color.FromArgb(0x02, 0xFF, 0xFF, 0xFF), 1),
            },
        };

        _sunGlowOn = new SolidColorBrush(sunGlow, 0.26);
        _sunGlowOff = new SolidColorBrush(sunGlow, 0.12);
        _sunLit = new SolidColorBrush(sunColor, 1.0);
        _sunDim = new SolidColorBrush(sunColor, 0.78);
        _moonGlowOn = new SolidColorBrush(moonGlow, 0.14);
        _moonGlowOff = new SolidColorBrush(moonGlow, 0.07);
        _moonLit = new SolidColorBrush(moonColor, 0.98);
        _moonDim = new SolidColorBrush(moonColor, 0.82);
        _craterShade = new SolidColorBrush(Palette.Bg, 0.32);

        _textCache.Clear();
        _cacheLight = light;
        _cacheReady = true;
    }

    private FormattedText GetText(string s, double size, Color color, bool medium = false)
    {
        var key = (s, size, medium, color.ToUInt32());
        if (_textCache.TryGetValue(key, out var cached)) return cached;
        var made = Text(s, size, color, medium);
        _textCache[key] = made;
        return made;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 160 || h < 120) return;

        bool light = Palette.IsLight;
        EnsureCache(light);

        double padL = 14, padR = 14, padT = 34, padB = 30;
        double plotW = w - padL - padR;
        double plotH = h - padT - padB;
        if (plotW < 80 || plotH < 40) return;

        double X(double hour) => padL + plotW * (hour / 24.0);

        var now = Now == DateTime.MinValue ? DateTime.Now : Now;
        double nowH = now.TimeOfDay.TotalHours;
        var day = now.Date;

        // ---------------- 采样 ----------------
        double lat = WeatherService.Latitude, lon = WeatherService.Longitude;
        bool geo = !double.IsNaN(lat) && !double.IsNaN(lon);

        var sun = new double[Steps];
        var moon = new double[Steps];
        FillAltitudes(geo ? lat : 0, geo ? lon : 0, day, geo, Rise, Set, sun, moon);

        // 纵轴按当天的极值自适应：
        // 取太阳与月亮的实际最高/最低点，算出刚好塞进绘图区的比例；
        // 地平线随之上下浮动（不再固定 52%），这样面板再矮也不会把曲线压平。
        const double FullScale = 32;
        double peak = 54, trough = -33;
        for (int i = 0; i < Steps; i++)
        {
            if (sun[i] > peak) peak = sun[i];
            if (moon[i] > peak) peak = moon[i];
            if (sun[i] < trough) trough = sun[i];
            if (moon[i] < trough) trough = moon[i];
        }
        peak = Math.Min(peak + 4, 84);
        trough = Math.Max(trough - 4, -72);

        double margin = Math.Clamp(plotH * 0.08, 6, 20);
        double span = Math.Max(peak - trough, 1);
        double scale = Math.Min(plotH * 0.44 / FullScale, (plotH - 2 * margin) / span);
        scale = Math.Max(scale, 0.05);

        double baseY = padT + margin + peak * scale;
        double MinShown = trough, MaxShown = peak;
        double Y(double alt) => baseY - alt * scale;
        // 天体圆心兜底收边：极端角度下也不让它探出绘图区
        double Fit(double y) => Math.Clamp(y, padT + margin, padT + plotH - margin);

        // ---------------- 网格与刻度 ----------------
        double[] ticks = { 0, 4, 8, 12, 16, 20, 24 };
        var gridPen = _gridPen!;

        foreach (double t in ticks)
        {
            double x = X(t);
            if (t > 0 && t < 24)
                ctx.DrawLine(gridPen, new Point(x, padT), new Point(x, padT + plotH));

            string label = ((int)(t % 24)).ToString();
            var tb = GetText(label, 13.5, _textColor);
            ctx.DrawText(tb, new Point(x - tb.Width / 2, h - padB + 8));
        }

        // 地平线以下的"地下"遮罩：夜间用浅色、日间用深色，正好反过来。
        // 从上往下渐淡，免得成一块生硬的灰板。
        var veil = light ? _veilLight! : _veilDark!;

        ctx.FillRectangle(veil, new Rect(padL, baseY, plotW, padT + plotH - baseY));

        // 地平线
        ctx.DrawLine(_horizonPen!,
            new Point(padL, baseY), new Point(padL + plotW, baseY));

        // 地平线右端的"水平"字样去掉了：月亮贴着地平线时会被它压住，
        // 地平线本身加上下遮罩已经够说明问题了

        // ---------------- 顶部说明 ----------------
        if (!string.IsNullOrEmpty(Caption))
        {
            var cap = GetText(Caption, 15, _captionColor, medium: true);
            ctx.DrawText(cap, new Point(X(12) - cap.Width / 2, padT - cap.Height - 10));
        }

        // ---------------- 曲线 ----------------
        var sunPts = new List<(double x, double alt)>(Steps);
        var moonPts = new List<(double x, double alt)>(Steps);
        for (int i = 0; i < Steps; i++)
        {
            sunPts.Add((X(i * 0.1), sun[i]));
            moonPts.Add((X(i * 0.1), moon[i]));
        }

        // ---------------- 现在的位置 ----------------
        double nowX = X(nowH);
        // 从同一条采样折线里插值，圆点才会正好落在曲线上（直接再算一次会有秒级偏差）
        double sunAlt = Sample(sun, nowH);
        double moonAlt = Sample(moon, nowH);

        var sunPt = new Point(nowX, Fit(Y(sunAlt)));
        var moonPt = new Point(nowX, Fit(Y(moonAlt)));


        // 天体周围留空：曲线和"现在"那条竖线都给它们让开，免得像穿在线上
        // 洞只比圆盘大一点点：曲线端点贴着圆盘边缘，看起来就是穿过去的；
        // 之前留 14px 会在圆点周围空一圈，视觉上像脱轨。
        var holes = new List<(Point Center, double Radius)>
        {
            (sunPt, 10.5),
            (moonPt, 10.5),
        };

        // 整条画淡的，地平线以上再压一遍加粗
        DrawCurve(ctx, sunPts, _sunDimPen!, Y, MinShown, MaxShown, null, holes);
        DrawCurve(ctx, sunPts, _sunBrightPen!, Y, MinShown, MaxShown, true, holes);

        DrawCurve(ctx, moonPts, _moonDimPen!, Y, MinShown, MaxShown, null, holes);
        DrawCurve(ctx, moonPts, _moonBrightPen!, Y, MinShown, MaxShown, true, holes);

        // 竖直的"现在"线，同样在两天体处断开
        var nowPen = _nowPen!;
        DrawVerticalWithHoles(ctx, nowX, padT, padT + plotH, holes, nowPen);

        // 太阳
        ctx.DrawEllipse(sunAlt > 0 ? _sunGlowOn! : _sunGlowOff!, null, sunPt, 20, 20);
        ctx.DrawEllipse(sunAlt > 0 ? _sunLit! : _sunDim!, null, sunPt, 9.4, 9.4);

        // 月亮：按真实相位画亮面，再点几处环形山
        double phase = Astro.MoonPhase(now);
        ctx.DrawEllipse(moonAlt > 0 ? _moonGlowOn! : _moonGlowOff!, null, moonPt, 20, 20);
        DrawMoonDisc(ctx, moonPt, 11.6, phase,
            moonAlt > 0 ? _moonLit! : _moonDim!);
    }

    /// <summary>在 0.1 时一格的采样数组里做线性插值。</summary>
    private static double Sample(double[] values, double hour)
    {
        double pos = Math.Clamp(hour, 0, (values.Length - 1) * 0.1) / 0.1;
        int i = (int)pos;
        if (i >= values.Length - 1) return values[^1];
        double f = pos - i;
        return values[i] + (values[i + 1] - values[i]) * f;
    }

    /// <summary>画一条曲线。above = true 时只画地平线以上那段。</summary>
    private static void DrawCurve(DrawingContext ctx, List<(double x, double alt)> pts, Pen pen,
        Func<double, double> y, double minAlt, double maxAlt, bool? above = null,
        List<(Point Center, double Radius)>? holes = null)
    {
        var geo = new StreamGeometry();
        bool open = false;
        using (var g = geo.Open())
        {
            foreach (var (px, alt) in pts)
            {
                var probe = new Point(px, y(alt));

                bool blocked = false;
                if (holes != null)
                {
                    foreach (var (c, r) in holes)
                    {
                        double dx = probe.X - c.X, dy = probe.Y - c.Y;
                        if (dx * dx + dy * dy < r * r) { blocked = true; break; }
                    }
                }

                bool inside = !blocked && alt >= minAlt && alt <= maxAlt && (above == null || alt >= 0);
                if (!inside)
                {
                    if (open) { g.EndFigure(false); open = false; }
                    continue;
                }

                var p = new Point(px, y(alt));
                if (!open) { g.BeginFigure(p, false); open = true; }
                else g.LineTo(p);
            }
            if (open) g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    /// <summary>
    /// 月面：按真实相位画。
    ///
    /// phase：0 新月、0.25 上弦、0.5 满月、0.75 下弦。
    /// 形状用一条"明暗分界线"定：它是半宽 r·|cosθ| 的椭圆，
    /// θ 从 0（新月）走到 π（满月）。θ 小于 π/2 时分界线往暗侧凹，成了月牙；
    /// 大于 π/2 时往亮侧凸，成了凸月。盈月亮右，亏月镜像到左。
    /// </summary>
    private static void DrawMoonDisc(DrawingContext ctx, Point c, double r, double phase, IBrush lit)
    {
        double t = phase <= 0.5 ? phase : 1 - phase;   // 0…0.5
        double theta = 2 * Math.PI * t;
        double k = Math.Cos(theta);                    // 1 = 新月，-1 = 满月
        double flip = phase <= 0.5 ? 1 : -1;           // 盈月亮右，亏月亮左

        // 暗面：整圆，压得很淡，只留轮廓
        ctx.DrawEllipse(new SolidColorBrush(Colors.Black, 0.16), null, c, r, r);
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Colors.White, 0.10), 1), c, r, r);

        var pts = new List<Point>(132);

        for (int i = 0; i <= 64; i++)                  // 外圆右半：底下到顶上
        {
            double ang = -Math.PI / 2 + Math.PI * i / 64;
            pts.Add(new Point(c.X + flip * r * Math.Cos(ang), c.Y + r * Math.Sin(ang)));
        }

        for (int i = 1; i <= 64; i++)                  // 明暗分界线：顶上回到底下
        {
            double phi = Math.PI / 2 - Math.PI * i / 64;
            double x = k * r * Math.Cos(phi);
            pts.Add(new Point(c.X + flip * x, c.Y + r * Math.Sin(phi)));
        }

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(pts[0], true);
            for (int i = 1; i < pts.Count; i++) g.LineTo(pts[i]);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(lit, null, geo);

        // 环形山：几个固定斑点，只落在亮面上
        var shade = new SolidColorBrush(Palette.Bg, 0.32);
        (double X, double Y, double R)[] craters =
        {
            (-0.30, -0.20, 0.20),
            (0.16, 0.30, 0.15),
            (0.34, -0.36, 0.12),
            (-0.06, -0.52, 0.10),
            (0.44, 0.10, 0.09),
            (-0.40, 0.28, 0.08),
        };

        foreach (var (cx, cy, cr) in craters)
        {
            double x = cx * r, y = cy * r;
            if (!IsLit(x, y, r, k)) continue;
            ctx.DrawEllipse(shade, null, new Point(c.X + flip * x, c.Y + y), cr * r, cr * r);
        }
    }

    /// <summary>某点是否落在亮面上（+x 指向亮侧）。</summary>
    private static bool IsLit(double x, double y, double r, double k)
    {
        if (x * x + y * y > r * r) return false;

        double half = Math.Sqrt(Math.Max(0, r * r - y * y));
        return x >= k * half;
    }

    private static void DrawVerticalWithHoles(DrawingContext ctx, double x, double y0, double y1,
        List<(Point Center, double Radius)> holes, Pen pen)
    {
        var spans = new List<(double A, double B)>();
        foreach (var (c, r) in holes)
        {
            double dx = Math.Abs(c.X - x);
            if (dx >= r) continue;
            double half = Math.Sqrt(r * r - dx * dx);
            spans.Add((c.Y - half, c.Y + half));
        }

        spans.Sort((p, q) => p.A.CompareTo(q.A));

        double cursor = y0;
        foreach (var (a, b) in spans)
        {
            if (b <= cursor) continue;
            if (a > cursor) ctx.DrawLine(pen, new Point(x, cursor), new Point(x, Math.Min(a, y1)));
            cursor = Math.Max(cursor, b);
            if (cursor >= y1) break;
        }
        if (cursor < y1) ctx.DrawLine(pen, new Point(x, cursor), new Point(x, y1));
    }

    private static FormattedText Text(string s, double size, Color color, bool medium = false)
        => new(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(medium ? "HarmonyOS Sans SC Medium" : "HarmonyOS Sans SC"),
            size, new SolidColorBrush(color));
}
