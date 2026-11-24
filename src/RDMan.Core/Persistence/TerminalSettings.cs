namespace RDMan.Core.Persistence;

public enum TerminalRenderer
{
    /// <summary>GPU (WebGL): crisp grayscale antialiasing, pixel-perfect powerline and box-drawing glyphs, fastest.</summary>
    Gpu,
    /// <summary>Browser text rendering (ClearType-style subpixel antialiasing); slower with heavy output.</summary>
    Subpixel,
}

public enum TerminalCursor { Bar, Block, Underline }

public enum TerminalRightClick
{
    /// <summary>Context menu (copy, paste, search…).</summary>
    Menu,
    /// <summary>PuTTY style: copy the selection if there is one, otherwise paste.</summary>
    CopyPaste,
}

public enum TerminalBell { Visual, Sound, Off }

/// <summary>App-wide terminal defaults. Fonts and sizes are in CSS pixels at 96 DPI (the WebView scales them).</summary>
public sealed class TerminalSettings
{
    public const string DefaultFont = "Cascadia Code NF";

    public string FontFamily { get; set; } = DefaultFont;
    public double FontSize { get; set; } = 13;
    public double LineHeight { get; set; } = 1.15;
    public double LetterSpacing { get; set; }
    public int FontWeight { get; set; } = 400;
    public int FontWeightBold { get; set; } = 700;
    public bool Ligatures { get; set; }
    public TerminalRenderer Renderer { get; set; } = TerminalRenderer.Gpu;
    public TerminalCursor Cursor { get; set; } = TerminalCursor.Bar;
    public bool CursorBlink { get; set; } = true;
    public int Scrollback { get; set; } = 10_000;
    /// <summary>"auto" follows the app theme (RDMan Night/Day); otherwise a scheme id.</summary>
    public string ColorScheme { get; set; } = "auto";
    /// <summary>1 = off; up to 7 forces readable text on any background (WCAG contrast ratio).</summary>
    public double MinimumContrast { get; set; } = 1;
    /// <summary>Tint the terminal background with the server's color.</summary>
    public bool TintWithServerColor { get; set; } = true;

    public bool CopyOnSelect { get; set; }
    /// <summary>Ctrl+C copies when text is selected, otherwise it's sent to the server (interrupt).</summary>
    public bool SmartCtrlC { get; set; } = true;
    public TerminalRightClick RightClick { get; set; } = TerminalRightClick.Menu;
    /// <summary>Ask before pasting text with several lines (it would run each line).</summary>
    public bool WarnMultilinePaste { get; set; } = true;
    public TerminalBell Bell { get; set; } = TerminalBell.Visual;
    /// <summary>Characters that end a word when double-clicking.</summary>
    public string WordSeparators { get; set; } = " ()[]{}',\"`|;:<>";

    public double SftpPanelWidth { get; set; } = 320;

    public void Normalize()
    {
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? DefaultFont : FontFamily.Trim();
        FontSize = Clamp(FontSize, 8, 32, 13);
        LineHeight = Clamp(LineHeight, 1, 2, 1.15);
        LetterSpacing = Clamp(LetterSpacing, -2, 4, 0);
        FontWeight = Math.Clamp(FontWeight / 100 * 100, 100, 900);
        FontWeightBold = Math.Clamp(FontWeightBold / 100 * 100, 100, 900);
        Scrollback = Math.Clamp(Scrollback, 0, 200_000);
        ColorScheme = string.IsNullOrWhiteSpace(ColorScheme) ? "auto" : ColorScheme.Trim();
        MinimumContrast = Clamp(MinimumContrast, 1, 7, 1);
        WordSeparators ??= " ()[]{}',\"`|;:<>";
        SftpPanelWidth = Clamp(SftpPanelWidth, 220, 900, 320);
        if (!Enum.IsDefined(Renderer)) Renderer = TerminalRenderer.Gpu;
        if (!Enum.IsDefined(Cursor)) Cursor = TerminalCursor.Bar;
        if (!Enum.IsDefined(RightClick)) RightClick = TerminalRightClick.Menu;
        if (!Enum.IsDefined(Bell)) Bell = TerminalBell.Visual;
    }

    private static double Clamp(double v, double min, double max, double fallback)
        => double.IsNaN(v) || double.IsInfinity(v) ? fallback : Math.Clamp(v, min, max);
}
