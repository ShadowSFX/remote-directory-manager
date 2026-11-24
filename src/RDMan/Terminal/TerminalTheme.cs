using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RDMan.Core.Models;
using RDMan.Core.Ssh;
using RDMan.Infrastructure;

namespace RDMan.Terminal;

/// <summary>
/// Builds what the terminal page needs to look like the rest of RDMan: the xterm palette (scheme, optionally tinted
/// with the server's color) and the app's UI tokens as CSS variables for search, cards, banners and menus.
/// </summary>
public static class TerminalTheme
{
    public static TerminalScheme SchemeFor(string? id) => TerminalSchemes.Resolve(id, ThemeManager.Instance.IsDark);

    /// <param name="tint">Server color (TagColors key) blended into the background, or null.</param>
    public static object Build(TerminalScheme scheme, string? tint, double minimumContrast)
    {
        var bg = scheme.Background;
        if (TagColors.HexFor(tint) is { } tintHex) bg = Blend(scheme.Background, tintHex, scheme.IsDark ? 0.06 : 0.04);
        var a = scheme.Ansi;
        return new
        {
            xterm = new Dictionary<string, string>
            {
                ["background"] = bg,
                ["foreground"] = scheme.Foreground,
                ["cursor"] = scheme.Cursor,
                ["cursorAccent"] = bg,
                ["selectionBackground"] = scheme.Selection,
                ["selectionInactiveBackground"] = scheme.Selection[..7] + "33",
                ["scrollbarSliderBackground"] = scheme.IsDark ? "rgba(255,255,255,0.12)" : "rgba(0,0,0,0.14)",
                ["scrollbarSliderHoverBackground"] = scheme.IsDark ? "rgba(255,255,255,0.22)" : "rgba(0,0,0,0.24)",
                ["scrollbarSliderActiveBackground"] = scheme.IsDark ? "rgba(255,255,255,0.30)" : "rgba(0,0,0,0.32)",
                ["overviewRulerBorder"] = "rgba(0,0,0,0)",
                ["black"] = a[0], ["red"] = a[1], ["green"] = a[2], ["yellow"] = a[3],
                ["blue"] = a[4], ["magenta"] = a[5], ["cyan"] = a[6], ["white"] = a[7],
                ["brightBlack"] = a[8], ["brightRed"] = a[9], ["brightGreen"] = a[10], ["brightYellow"] = a[11],
                ["brightBlue"] = a[12], ["brightMagenta"] = a[13], ["brightCyan"] = a[14], ["brightWhite"] = a[15],
            },
            minimumContrast = Math.Max(minimumContrast, scheme.RecommendedMinimumContrast),
            css = new Dictionary<string, string>
            {
                ["--term-bg"] = bg,
                ["--rail"] = TagColors.HexFor(tint) ?? "transparent",
                ["--ui-bg"] = Css("Bg.Popup"),
                ["--ui-line"] = Css("Line.Popup"),
                ["--ui-fg"] = Css("Fg.Primary"),
                ["--ui-fg2"] = Css("Fg.Secondary"),
                ["--ui-fg3"] = Css("Fg.Tertiary"),
                ["--ui-hover"] = Css("Bg.Hover"),
                ["--ui-accent"] = Css("Accent"),
                ["--ui-accent-text"] = Css("Accent.Text"),
                ["--ui-on-accent"] = Css("Fg.OnAccent"),
                ["--ui-danger"] = Css("Danger"),
                ["--ui-warn"] = Css("Status.Warn"),
                ["--ui-scrim"] = Css("Bg.Scrim"),
                ["--ui-shadow"] = ThemeManager.Instance.IsDark ? "rgba(0,0,0,.45)" : "rgba(26,30,38,.16)",
            },
        };
    }

    public static Color BackgroundColor(TerminalScheme scheme, string? tint)
    {
        var hex = TagColors.HexFor(tint) is { } t ? Blend(scheme.Background, t, scheme.IsDark ? 0.06 : 0.04) : scheme.Background;
        return (Color)ColorConverter.ConvertFromString(hex);
    }

    /// <summary>A theme brush as CSS (#RRGGBB or rgba() when translucent).</summary>
    private static string Css(string key)
    {
        if (Application.Current?.TryFindResource(key) is not SolidColorBrush b) return "transparent";
        var c = b.Color;
        return c.A == 255
            ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
            : string.Create(CultureInfo.InvariantCulture, $"rgba({c.R},{c.G},{c.B},{c.A / 255.0:0.###})");
    }

    private static string Blend(string baseHex, string tintHex, double amount)
    {
        var b = (Color)ColorConverter.ConvertFromString(baseHex);
        var t = (Color)ColorConverter.ConvertFromString(tintHex);
        byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * amount);
        return $"#{Mix(b.R, t.R):X2}{Mix(b.G, t.G):X2}{Mix(b.B, t.B):X2}";
    }
}
