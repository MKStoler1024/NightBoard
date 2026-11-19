using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NightBoard.Services;

namespace NightBoard.Controls;

/// <summary>
/// 逐小时温度折线：一格一小时，往右排开，宽了就在外面的 ScrollViewer 里滚。
/// 线走平滑曲线，点上标温度，底下标小时；「现在」那一格用实心点标出来。
/// </summary>
public class HourlyChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<HourPoint>?> HoursProperty =
        AvaloniaProperty.Register<HourlyChart, IReadOnlyList<HourPoint>?>(nameof(Hours));

    /// <summary>外层 ScrollViewer 当前的横向偏移，用来把"均"标签钉在视口右侧。</summary>
    public static readonly StyledProperty<double> OffsetXProperty =
        AvaloniaProperty.Register<HourlyChart, double>(nameof(OffsetX));

    public static readonly StyledProperty<double> ViewportWidthProperty =
        AvaloniaProperty.Register<HourlyChart, double>(nameof(ViewportWidth));

    /// <summary>每格宽度（逻辑像素）。</summary>
    public static readonly StyledProperty<double> SlotProperty =
        AvaloniaProperty.Register<HourlyChart, double>(nameof(Slot), 46.0);

    static HourlyChart()
    {
        AffectsRender<HourlyChart>(HoursProperty, SlotProperty, OffsetXProperty, ViewportWidthProperty);
    }

    public IReadOnlyList<HourPoint>? Hours
    {
        get => GetValue(HoursProperty);
        set => SetValue(HoursProperty, value);
    }

    public double Slot
    {
        get => GetValue(SlotProperty);
        set => SetValue(SlotProperty, value);
    }

    public double OffsetX
    {
        get => GetValue(OffsetXProperty);
        set => SetValue(OffsetXProperty, value);
    }

    public double ViewportWidth
    {
        get => GetValue(ViewportWidthProperty);
        set => SetValue(ViewportWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int n = Hours?.Count ?? 0;
        double w = Math.Max(n * Slot, availableSize.Width is double.PositiveInfinity ? 0 : availableSize.Width);
        double h = availableSize.Height is double.PositiveInfinity ? 120 : availableSize.Height;
        return new Size(w, h);
    }

    public override void Render(DrawingContext ctx)
    {
        var hours = Hours;
        double w = Bounds.Width, h = Bounds.Height;
        if (hours is null || hours.Count == 0 || w < 20 || h < 30) return;

        bool light = Palette.IsLight;
        var lineColor = light ? Color.Parse("#4A7FA5") : Color.Parse("#7FA6C9");
        var fillColor = light ? Color.Parse("#BFD4E6") : Color.Parse("#2A3948");
        var textColor = light ? Color.Parse("#6B6459") : Color.Parse("#8A8071");
        var faint = light ? Color.Parse("#A79E8E") : Color.Parse("#4A443A");

        // 窄的时候自动简化：温度和小时间隔显示，字号也小一档
        // 按“视口”宽度判断，而不是整条曲线的宽度
        double visW = ViewportWidth > 20 ? ViewportWidth : Bounds.Width;
        bool compact = visW < 420;
        int tempStep = compact ? 3 : 1;
        int hourStep = compact ? 3 : 1;

        double padTop = compact ? 16 : 22, padBottom = compact ? 20 : 24;
        double plotH = Math.Max(10, h - padTop - padBottom);

        double min = double.MaxValue, max = double.MinValue;
        foreach (var p in hours)
        {
            min = Math.Min(min, p.Temp);
            max = Math.Max(max, p.Temp);
        }
        if (max - min < 4) { double mid = (max + min) / 2; max = mid + 2; min = mid - 2; }
        double pad = (max - min) * 0.18;
        min -= pad; max += pad;

        double X(int i) => Slot * i + Slot / 2;
        double Y(double t) => padTop + (1 - (t - min) / (max - min)) * plotH;

        // 竖向网格：每小时一条，正好落在点上，标签和点就对得上了
        var gridPen = new Pen(new SolidColorBrush(light ? Color.Parse("#C9C2B4") : Color.Parse("#2A2723"),
            light ? 0.45 : 0.35), 1);
        var gridStrong = new Pen(new SolidColorBrush(light ? Color.Parse("#C9C2B4") : Color.Parse("#2A2723"),
            light ? 0.7 : 0.55), 1);

        for (int i = 0; i < hours.Count; i++)
        {
            bool strong = hours[i].Time.Hour % 3 == 0;
            var pen = strong ? gridStrong : gridPen;
            ctx.DrawLine(pen, new Point(X(i), padTop - 4), new Point(X(i), padTop + plotH + 2));
        }

        // 零度线：范围跨过 0° 时给一条虚线，好看出哪天回暖哪天更冷
        if (min < 0 && max > 0)
        {
            double y0 = Y(0);
            var zero = new Pen(new SolidColorBrush(faint, 0.55), 1,
                new DashStyle(new double[] { 3, 4 }, 0));
            ctx.DrawLine(zero, new Point(X(0), y0), new Point(X(hours.Count - 1), y0));
        }

        // 平均温度：一条虚线，标在右端
        double avg = 0;
        foreach (var p in hours) avg += p.Temp;
        avg /= hours.Count;

        double ya = Y(avg);
        var avgPen = new Pen(new SolidColorBrush(light ? Color.Parse("#B08A4A") : Color.Parse("#7A6234"), 0.85), 1,
            new DashStyle(new double[] { 4, 4 }, 0));
        ctx.DrawLine(avgPen, new Point(X(0), ya), new Point(X(hours.Count - 1), ya));

        // 窄的时候"均"字放左端，免得跟温度标签挤在一起；宽图仍钉在视口右上
        var avgText = Text($"均 {Math.Round(avg)}°", compact ? 9.5 : 11.5,
            light ? Color.Parse("#8C7440") : Color.Parse("#8A7440"));

        // 窄图不写字（会跟温度标签打架），均温改在天气那一栏里给出
        if (!compact)
        {
            double labelRight = (ViewportWidth > 20 ? OffsetX + ViewportWidth : X(hours.Count - 1)) - 6;
            ctx.DrawText(avgText, new Point(Math.Max(OffsetX + 6, labelRight - avgText.Width),
                ya - avgText.Height - 2));
        }

        // 面积
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(X(0), padTop + plotH), true);
            for (int i = 0; i < hours.Count; i++) g.LineTo(new Point(X(i), Y(hours[i].Temp)));
            g.LineTo(new Point(X(hours.Count - 1), padTop + plotH));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(fillColor, light ? 0.35 : 0.28), null, area);

        // 平滑曲线：Catmull-Rom 转三次贝塞尔，线过每一个点但不打结
        var pts = new Point[hours.Count];
        for (int i = 0; i < hours.Count; i++) pts[i] = new Point(X(i), Y(hours[i].Temp));

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(pts[0], false);
            for (int i = 0; i < pts.Length - 1; i++)
            {
                var p0 = pts[Math.Max(0, i - 1)];
                var p1 = pts[i];
                var p2 = pts[i + 1];
                var p3 = pts[Math.Min(pts.Length - 1, i + 2)];

                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                g.CubicBezierTo(c1, c2, p2);
            }
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(lineColor, 0.95), 2.4,
            lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);

        // 温度标签统一放线上或线下，别一上一下
        double topMost = padTop;
        foreach (var p in hours) topMost = Math.Min(topMost, Y(p.Temp));
        bool labelsBelow = topMost - 24 < padTop;

        // 点、温度、小时
        var nowHour = DateTime.Now;
        for (int i = 0; i < hours.Count; i++)
        {
            var p = hours[i];
            double x = X(i), y = Y(p.Temp);

            bool isNow = p.Time.Hour == nowHour.Hour && p.Time.Date == nowHour.Date;
            ctx.DrawEllipse(
                new SolidColorBrush(isNow ? lineColor : (light ? Colors.White : Color.Parse("#0C0B09"))),
                new Pen(new SolidColorBrush(lineColor, isNow ? 1 : 0.75), 1.6),
                new Point(x, y), isNow ? 4 : 2.6, isNow ? 4 : 2.6);

            var temp = Text($"{Math.Round(p.Temp)}°", compact ? 10.5 : 13.5, isNow ? (light ? Color.Parse("#2A2620") : Color.Parse("#F4EBD8")) : textColor);
            double labelY = labelsBelow ? y + 8 : y - temp.Height - 8;
            if (i % tempStep == 0 || isNow)
                ctx.DrawText(temp, new Point(x - temp.Width / 2, labelY));

            if (i % hourStep == 0 || isNow)
            {
                var hour = Text(p.Time.ToString("HH"), compact ? 9.5 : 11.5, isNow ? textColor : faint);
                ctx.DrawText(hour, new Point(x - hour.Width / 2, padTop + plotH + 5));
            }
        }
    }

    private static FormattedText Text(string s, double size, Color color)
        => new(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("HarmonyOS Sans SC"), size, new SolidColorBrush(color));
}
