using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Wpf;
using RDMan.Infrastructure;

namespace RDMan;

/// <summary>
/// Window shortcuts, one table (formerly Window.InputBindings). Keys typed in a terminal never come through here: the
/// WebView2 doesn't hand them to the window reliably, so the terminal page recognizes the few RDMan keeps inside a
/// terminal (bridge.js appShortcut → MainViewModel.RunTerminalShortcut) and everything else goes to the shell.
/// </summary>
public partial class MainWindow
{
    private sealed record Shortcut(Key Key, ModifierKeys Modifiers, Func<ICommand> Command);

    private Shortcut[]? _shortcuts;

    private Shortcut[] Shortcuts => _shortcuts ??=
    [
        new(Key.K, ModifierKeys.Control, () => _vm.OpenPaletteCommand),
        new(Key.P, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.OpenPaletteCommand),
        new(Key.K, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.OpenPaletteCommand),
        new(Key.T, ModifierKeys.Control, () => _vm.NewTabCommand),
        new(Key.T, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.NewTerminalTabCommand),
        new(Key.W, ModifierKeys.Control, () => _vm.CloseTabCommand),
        new(Key.W, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.CloseTabCommand),
        new(Key.F4, ModifierKeys.Control, () => _vm.CloseTabCommand),
        new(Key.Tab, ModifierKeys.Control, () => _vm.NextTabCommand),
        new(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.PreviousTabCommand),
        new(Key.F3, ModifierKeys.None, () => _vm.SplitRightCommand),
        new(Key.F6, ModifierKeys.None, () => _vm.FocusOtherPaneCommand),
        new(Key.N, ModifierKeys.Control, () => _vm.NewServerCommand),
        new(Key.G, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.NewGroupCommand),
        new(Key.OemComma, ModifierKeys.Control, () => _vm.OpenSettingsCommand),
        new(Key.D, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.OpenActiveServerDesktopCommand),
        new(Key.F11, ModifierKeys.None, () => _vm.ToggleFullScreenCommand),
        new(Key.E, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.ToggleTerminalSftpCommand),
        new(Key.B, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.ToggleTerminalBroadcastCommand),
    ];

    /// <summary>The key comes from a terminal's WebView2 (the page has the keyboard).</summary>
    private static bool FromTerminal(KeyEventArgs e)
        => e.OriginalSource is DependencyObject d && (d is WebView2 || Ui.FindAncestor<WebView2>(d) is not null);

    /// <returns>True when the key was a shortcut and has been handled.</returns>
    private bool HandleShortcut(KeyEventArgs e, out bool fromTerminal)
    {
        fromTerminal = FromTerminal(e);
        // Inside a terminal the page itself recognizes RDMan's shortcuts (bridge.js appShortcut) and everything else
        // belongs to the shell: never act on a key event that comes from there (it would run twice or steal it).
        if (fromTerminal) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        foreach (var s in Shortcuts)
        {
            if (s.Key != key || s.Modifiers != mods) continue;
            var command = s.Command();
            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
            return true;
        }
        return false;
    }
}
