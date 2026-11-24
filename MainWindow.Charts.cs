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

}
