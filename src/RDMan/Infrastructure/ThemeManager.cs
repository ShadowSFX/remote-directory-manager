using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using RDMan.Core.Persistence;
using RDMan.Interop;

namespace RDMan.Infrastructure;

/// <summary>Swaps the color dictionary (Dark/Light) at runtime and keeps every window's title bar + backdrop in sync.</summary>
public sealed class ThemeManager
{
    public static ThemeManager Instance { get; } = new();

    private ThemePreference _preference = ThemePreference.System;
    private ResourceDictionary? _current;

    public bool IsDark { get; private set; } = true;

    public event EventHandler? ThemeChanged;

    private ThemeManager()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color && _preference == ThemePreference.System)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(_preference));
        };
    }

    public static bool SystemUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v ? v == 0 : true;
        }
        catch (System.Security.SecurityException) { return true; }
    }

    public void Apply(ThemePreference preference)
    {
        _preference = preference;
        var dark = preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => SystemUsesDark(),
        };
        var changed = dark != IsDark || _current is null;
        IsDark = dark;
        var dicts = Application.Current.Resources.MergedDictionaries;
        var next = new ResourceDictionary { Source = new Uri($"pack://application:,,,/RDMan;component/Themes/{(dark ? "Dark" : "Light")}.xaml") };
        if (_current is not null) dicts.Remove(_current);
        dicts.Insert(0, next);
        _current = next;
        foreach (Window w in Application.Current.Windows) WindowEffects.Apply(w, dark);
        if (changed) ThemeChanged?.Invoke(this, EventArgs.Empty);
    }
}

public static class WindowEffects
{
    private static readonly bool SupportsDarkTitle = Environment.OSVersion.Version.Build >= 17763;

    /// <summary>Dark/light native frame and rounded corners (Windows 11).</summary>
    public static void Apply(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (SupportsDarkTitle)
        {
            var v = dark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
        }
        var corner = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }
}
