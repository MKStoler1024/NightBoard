using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NightBoard.Controls;

/// <summary>
/// 轻量折线图。刻意不用图表库：只画底色 + 网格 + 一条线，
/// 数据不变就不重绘，夜间常驻也就这点开销。
/// </summary>
public class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    /// <summary>时间窗口容量。数据不满时靠右生长，不拉伸铺满。</summary>
    public static readonly StyledProperty<int> CapacityProperty =
        AvaloniaProperty.Register<Sparkline, int>(nameof(Capacity), 360);

    /// <summary>纵轴自适应：低负载时放大，曲线才有起伏。</summary>
    public static readonly StyledProperty<bool> AutoScaleProperty =
        AvaloniaProperty.Register<Sparkline, bool>(nameof(AutoScale), true);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke), Brushes.White);

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(GridBrush));

    /// <summary>图表底色，用来界定这块区域的范围。</summary>
    public static readonly StyledProperty<IBrush?> PlateBrushProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(PlateBrush));

    /// <summary>零基线。</summary>
    public static readonly StyledProperty<IBrush?> BaseBrushProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(BaseBrush));

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, CapacityProperty, AutoScaleProperty,
            StrokeProperty, FillProperty, GridBrushProperty, PlateBrushProperty, BaseBrushProperty);
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public int Capacity
    {
        get => GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    public bool AutoScale
    {
        get => GetValue(AutoScaleProperty);
        set => SetValue(AutoScaleProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? PlateBrush
    {
        get => GetValue(PlateBrushProperty);
        set => SetValue(PlateBrushProperty, value);
    }

    public IBrush? BaseBrush
    {
        get => GetValue(BaseBrushProperty);
        set => SetValue(BaseBrushProperty, value);
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 2 || h < 2) return;

        // 底色：给这块区域一个边界，不然黑底上看不出画布范围
        if (PlateBrush != null)
            ctx.FillRectangle(PlateBrush, new Rect(0, 0, w, h));

        // 参考线：25 / 50 / 75
        var grid = GridBrush;
        if (grid != null)
        {
            var pen = new Pen(grid, 1);
            for (int i = 1; i <= 3; i++)
            {
                double y = Math.Round(h * i / 4.0) + 0.5;
                ctx.DrawLine(pen, new Point(0, y), new Point(w, y));
            }
        }

        // 零基线：比网格线明显一点，一眼能看出跌到哪算底
        var baseBrush = BaseBrush;
        if (baseBrush != null)
        {
            double y = h - 0.5;
            ctx.DrawLine(new Pen(baseBrush, 1), new Point(0, y), new Point(w, y));
        }

        var vals = Values;
        if (vals is null || vals.Count < 2) return;

        int n = vals.Count;
        int cap = Math.Max(Capacity, n);
        double stepX = cap > 1 ? w / (cap - 1) : w;
        double offset = (cap - n) * stepX;
        double X(int i) => offset + i * stepX;

        // 纵轴上限：默认满量程 100%，低谷时收窄到峰值的 130%，下限 25%
        double top = 100;
        if (AutoScale)
        {
            double peak = 0;
            for (int i = 0; i < n; i++)
                if (!double.IsNaN(vals[i]) && vals[i] > peak) peak = vals[i];
            top = Math.Clamp(Math.Ceiling(peak * 1.3 / 5) * 5, 25, 100);
        }

        double Y(double v) => h - Math.Clamp(v, 0, top) / top * (h - 2) - 1;

        // NaN 当断点：显卡被关掉那段时间曲线是空的，不画成一条贴底的 0
        int lastValid = -1;
        for (int i = n - 1; i >= 0; i--)
            if (!double.IsNaN(vals[i])) { lastValid = i; break; }

        // 面积与折线都按连续段画
        var seg = new List<int>(n);
        for (int i = 0; i <= n; i++)
        {
            if (i < n && !double.IsNaN(vals[i])) { seg.Add(i); continue; }
            if (seg.Count > 0)
            {
                if (Fill != null)
                {
                    var area = new StreamGeometry();
                    using (var g = area.Open())
                    {
                        g.BeginFigure(new Point(X(seg[0]), h), true);
                        foreach (int k in seg) g.LineTo(new Point(X(k), Y(vals[k])));
                        g.LineTo(new Point(X(seg[^1]), h));
                        g.EndFigure(true);
                    }
                    ctx.DrawGeometry(Fill, null, area);
                }

                if (Stroke != null && seg.Count > 1)
                {
                    var line = new StreamGeometry();
                    using (var g = line.Open())
                    {
                        g.BeginFigure(new Point(X(seg[0]), Y(vals[seg[0]])), false);
                        for (int j = 1; j < seg.Count; j++)
                            g.LineTo(new Point(X(seg[j]), Y(vals[seg[j]])));
                        g.EndFigure(false);
                    }
                    ctx.DrawGeometry(null,
                        new Pen(Stroke, 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
                }

                seg.Clear();
            }
        }

        if (Stroke != null && lastValid >= 0)
            ctx.DrawEllipse(Stroke, null, new Point(X(lastValid), Y(vals[lastValid])), 2.4, 2.4);
    }
}
