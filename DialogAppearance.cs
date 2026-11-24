using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using NightBoard.Services;

namespace NightBoard;

internal static class DialogAppearance
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Windows 默认给标题栏配系统亮色，和夜板的底子撞得厉害，这里强制跟随主题。</summary>
    public static void UseDarkTitleBar(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        int on = Palette.IsLight ? 0 : 1;
        if (DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref on, sizeof(int));
    }

    public static IBrush Brush(string dark, string light) =>
        new SolidColorBrush(Color.Parse(Palette.IsLight ? light : dark));

    public static void Apply(Window window)
    {
        window.RequestedThemeVariant = Palette.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        window.Background = Brush("#100E0C", "#F6F2E9");
        window.Foreground = Brush("#F4EBD8", "#1E1B16");
        if (window.Owner is Window owner) window.Topmost = owner.Topmost;
        UseDarkTitleBar(window);
        var screen = window.Owner is Window parent
            ? window.Screens.ScreenFromWindow(parent) : window.Screens.Primary;
        if (screen is null) return;
        window.MaxHeight = Math.Max(180, screen.WorkingArea.Height / screen.Scaling - 56);
        window.MaxWidth = Math.Max(240, screen.WorkingArea.Width / screen.Scaling - 32);
    }
}
