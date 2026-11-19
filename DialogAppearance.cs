using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using NightBoard.Services;

namespace NightBoard;

internal static class DialogAppearance
{
    public static IBrush Brush(string dark, string light) =>
        new SolidColorBrush(Color.Parse(Palette.IsLight ? light : dark));

    public static void Apply(Window window)
    {
        window.RequestedThemeVariant = Palette.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        window.Background = Brush("#100E0C", "#F6F2E9");
        window.Foreground = Brush("#F4EBD8", "#1E1B16");
        if (window.Owner is Window owner) window.Topmost = owner.Topmost;
        var screen = window.Owner is Window parent
            ? window.Screens.ScreenFromWindow(parent) : window.Screens.Primary;
        if (screen is null) return;
        window.MaxHeight = Math.Max(180, screen.WorkingArea.Height / screen.Scaling - 56);
        window.MaxWidth = Math.Max(240, screen.WorkingArea.Width / screen.Scaling - 32);
    }
}
