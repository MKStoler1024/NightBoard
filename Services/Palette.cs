using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace NightBoard.Services;

/// <summary>
/// 主题：夜间白字黑底，日间黑字白底。两套色板切换，
/// 所有自绘控件订阅 Changed 重新取色。
/// </summary>
public static class Palette
{
    public enum Role { Ink, Value, Dim, Faint, Line, Plate, Grid, Base, Ghost }

    private sealed class Scheme
    {
        public required Color Bg;
        public required Color Ink;
        public required Color Value;
        public required Color Dim;
        public required Color Faint;
        public required Color Line;
        public required Color Plate;
        public required Color Grid;
        public required Color Base;
        public required Color Ghost;
        public required Color Cpu;
        public required Color Igpu;
        public required Color Dgpu;
        public required Color Mem;
    }

    private static readonly Scheme Dark = new()
    {
        Bg = Color.Parse("#000000"),
        Ink = Color.Parse("#F4EBD8"),
        Value = Color.Parse("#C6BBA4"),
        Dim = Color.Parse("#7D7364"),
        Faint = Color.Parse("#4A443A"),
        Line = Color.Parse("#221F1A"),
        Plate = Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF),
        Grid = Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF),
        Base = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF),
        Ghost = Color.Parse("#241F19"),
        Cpu = Color.Parse("#D9A34A"),
        Igpu = Color.Parse("#6FA39B"),
        Dgpu = Color.Parse("#9B8AC4"),
        Mem = Color.Parse("#8FAE72"),
    };

    private static readonly Scheme Light = new()
    {
        Bg = Color.Parse("#F6F2E9"),
        Ink = Color.Parse("#1E1B16"),
        Value = Color.Parse("#3B342A"),
        Dim = Color.Parse("#6B6459"),
        Faint = Color.Parse("#A79E8E"),
        Line = Color.Parse("#D9D2C5"),
        Plate = Color.FromArgb(0x08, 0x00, 0x00, 0x00),
        Grid = Color.FromArgb(0x0F, 0x00, 0x00, 0x00),
        Base = Color.FromArgb(0x24, 0x00, 0x00, 0x00),
        Ghost = Color.Parse("#EDE6D6"),
        Cpu = Color.Parse("#B0761A"),
        Igpu = Color.Parse("#2E7A70"),
        Dgpu = Color.Parse("#6A5B96"),
        Mem = Color.Parse("#5A7A3C"),
    };

    private static Scheme _p = Dark;
    private static readonly List<(WeakReference<AvaloniaObject> target, Role role)> Registry = new();
    private static readonly object Gate = new();

    public static bool IsLight { get; private set; }

    public static event Action? Changed;

    public static Color Bg => _p.Bg;
    public static Color Ink => _p.Ink;
    public static Color Value => _p.Value;
    public static Color Dim => _p.Dim;
    public static Color Faint => _p.Faint;
    public static Color Line => _p.Line;
    public static Color Plate => _p.Plate;
    public static Color Grid => _p.Grid;
    public static Color Base => _p.Base;
    public static Color Ghost => _p.Ghost;
    public static Color Cpu => _p.Cpu;
    public static Color Igpu => _p.Igpu;
    public static Color Dgpu => _p.Dgpu;
    public static Color Mem => _p.Mem;

    public static Color For(Role role) => role switch
    {
        Role.Ink => _p.Ink,
        Role.Value => _p.Value,
        Role.Dim => _p.Dim,
        Role.Faint => _p.Faint,
        Role.Line => _p.Line,
        Role.Plate => _p.Plate,
        Role.Grid => _p.Grid,
        Role.Base => _p.Base,
        Role.Ghost => _p.Ghost,
        _ => _p.Ink,
    };

    /// <summary>登记一个控件，主题变化时自动改色。</summary>
    public static void Track(AvaloniaObject target, Role role)
    {
        if (target is not Visual) return;
        lock (Gate) Registry.Add((new WeakReference<AvaloniaObject>(target), role));
    }

    public static void Apply(bool light, bool force = false)
    {
        if (!force && light == IsLight) return;
        IsLight = light;
        _p = light ? Light : Dark;
        Recolor();
        Changed?.Invoke();
    }

    public static void Toggle() => Apply(!IsLight);

    private static void Recolor()
    {
        lock (Gate)
        {
            for (int i = Registry.Count - 1; i >= 0; i--)
            {
                var (wr, role) = Registry[i];
                if (!wr.TryGetTarget(out var target))
                {
                    Registry.RemoveAt(i);
                    continue;
                }

                var color = new SolidColorBrush(For(role));
                switch (target)
                {
                    case Avalonia.Controls.TextBlock tb:
                        tb.Foreground = color;
                        break;
                    case Avalonia.Controls.Border bd when role == Role.Line:
                        bd.Background = color;
                        break;
                    case Avalonia.Controls.Shapes.Ellipse el:
                        el.Fill = color;
                        break;
                }
            }
        }
    }
}
