using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RDMan.Core.Persistence;
using RDMan.Core.Ssh;
using RDMan.Infrastructure;

namespace RDMan.ViewModels.Terminal;

/// <summary>A color scheme in the settings list: name plus its 8 base colors as swatches.</summary>
public sealed record SchemeChoice(string Id, string Name, Brush Background, IReadOnlyList<Brush> Swatches)
{
    public override string ToString() => Name;
}

/// <summary>Settings → Terminal: app-wide defaults; every change restyles open terminals immediately.</summary>
public sealed partial class TerminalSettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private TerminalSettings T => _main.State.Settings.Terminal;

    public TerminalSettingsViewModel(MainViewModel main)
    {
        _main = main;
        Schemes = BuildSchemes();
    }

    public IReadOnlyList<string> Fonts { get; } = BuildFonts();
    public double[] Sizes { get; } = [10, 11, 12, 13, 14, 15, 16, 18, 20, 22];
    public double[] LineHeights { get; } = [1.0, 1.1, 1.15, 1.2, 1.3, 1.4];
    public int[] ScrollbackSizes { get; } = [1_000, 5_000, 10_000, 50_000, 100_000];
    public IReadOnlyList<SchemeChoice> Schemes { get; }

    public string FontFamily { get => T.FontFamily; set { T.FontFamily = value; Save(); } }
    public double FontSize { get => T.FontSize; set { T.FontSize = value; Save(); } }
    public double LineHeight { get => T.LineHeight; set { T.LineHeight = value; Save(); } }
    public int Scrollback { get => T.Scrollback; set { T.Scrollback = value; Save(); } }
    public TerminalRenderer Renderer { get => T.Renderer; set { T.Renderer = value; Save(); } }
    public TerminalCursor Cursor { get => T.Cursor; set { T.Cursor = value; Save(); } }
    public bool CursorBlink { get => T.CursorBlink; set { T.CursorBlink = value; Save(); } }
    public bool TintWithServerColor { get => T.TintWithServerColor; set { T.TintWithServerColor = value; Save(); } }
    public bool EnsureContrast { get => T.MinimumContrast > 1; set { T.MinimumContrast = value ? 4.5 : 1; Save(); } }
    public bool CopyOnSelect { get => T.CopyOnSelect; set { T.CopyOnSelect = value; Save(); } }
    public bool SmartCtrlC { get => T.SmartCtrlC; set { T.SmartCtrlC = value; Save(); } }
    public TerminalRightClick RightClick { get => T.RightClick; set { T.RightClick = value; Save(); } }
    public bool WarnMultilinePaste { get => T.WarnMultilinePaste; set { T.WarnMultilinePaste = value; Save(); } }
    public TerminalBell Bell { get => T.Bell; set { T.Bell = value; Save(); } }

    public SchemeChoice? Scheme
    {
        get => Schemes.FirstOrDefault(s => s.Id == T.ColorScheme) ?? Schemes[0];
        set
        {
            if (value is null) return;
            T.ColorScheme = value.Id;
            Save();
        }
    }

    private void Save()
    {
        T.Normalize();
        OnPropertyChanged(string.Empty);
        _main.State.MarkSettingsDirty();
        _main.ApplyTerminalSettings();
    }

    private static IReadOnlyList<SchemeChoice> BuildSchemes()
    {
        Brush B(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
        var list = new List<SchemeChoice>();
        var auto = TerminalSchemes.Resolve(TerminalSchemes.Auto, ThemeManager.Instance.IsDark);
        list.Add(new SchemeChoice(TerminalSchemes.Auto, Loc.T("Term.Scheme.Auto"), B(auto.Background), auto.Ansi.Take(8).Skip(1).Select(B).ToList()));
        foreach (var s in TerminalSchemes.All)
            list.Add(new SchemeChoice(s.Id, s.Name, B(s.Background), s.Ansi.Take(8).Skip(1).Select(B).ToList()));
        return list;
    }

    /// <summary>The bundled Nerd Font first, then the monospaced fonts installed on this PC.</summary>
    private static IReadOnlyList<string> BuildFonts()
    {
        var names = new List<string> { TerminalSettings.DefaultFont };
        var culture = CultureInfo.CurrentUICulture;
        foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
        {
            var name = family.FamilyNames.TryGetValue(System.Windows.Markup.XmlLanguage.GetLanguage("en-us"), out var en) ? en : family.Source;
            if (string.IsNullOrWhiteSpace(name) || names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (IsMonospace(family, culture)) names.Add(name);
        }
        return names.Take(1).Concat(names.Skip(1).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)).ToList();
    }

    private static bool IsMonospace(FontFamily family, CultureInfo culture)
    {
        try
        {
            var typeface = new Typeface(family, System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out var glyphs) || glyphs.Symbol) return false;
            double Width(char c) => glyphs.CharacterToGlyphMap.TryGetValue(c, out var g) ? glyphs.AdvanceWidths[g] : -1;
            var i = Width('i');
            return i > 0 && Math.Abs(i - Width('W')) < 0.001 && Math.Abs(i - Width('.')) < 0.001;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
