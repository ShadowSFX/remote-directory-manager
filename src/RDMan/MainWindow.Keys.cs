using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Wpf;
using RDMan.Infrastructure;

namespace RDMan;

/// <summary>
/// Window shortcuts. They used to be Window.InputBindings, but a terminal (WebView2) re-raises its accelerator keys
/// as WPF key events, so bindings would steal keys the shell needs (Ctrl+W deletes a word, Ctrl+K kills the line,
/// F3/F6 belong to mc/htop). One table decides: everywhere else every shortcut works; inside a terminal only the
/// ones marked <c>InTerminal</c> (Ctrl+Shift combos, Ctrl+Tab, F11) — everything else goes to the server.
/// </summary>
public partial class MainWindow
{
    private sealed record Shortcut(Key Key, ModifierKeys Modifiers, Func<ICommand> Command, bool InTerminal);

    private Shortcut[]? _shortcuts;

    private Shortcut[] Shortcuts => _shortcuts ??=
    [
        new(Key.K, ModifierKeys.Control, () => _vm.OpenPaletteCommand, false),
        new(Key.P, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.OpenPaletteCommand, true),
        new(Key.K, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.OpenPaletteCommand, true),
        new(Key.T, ModifierKeys.Control, () => _vm.NewTabCommand, false),
        new(Key.T, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.NewTerminalTabCommand, true),
        new(Key.W, ModifierKeys.Control, () => _vm.CloseTabCommand, false),
        new(Key.W, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.CloseTabCommand, true),
        new(Key.F4, ModifierKeys.Control, () => _vm.CloseTabCommand, true),
        new(Key.Tab, ModifierKeys.Control, () => _vm.NextTabCommand, true),
        new(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.PreviousTabCommand, true),
        new(Key.F3, ModifierKeys.None, () => _vm.SplitRightCommand, false),
        new(Key.F6, ModifierKeys.None, () => _vm.FocusOtherPaneCommand, false),
        new(Key.N, ModifierKeys.Control, () => _vm.NewServerCommand, false),
        new(Key.G, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.NewGroupCommand, false),
        new(Key.OemComma, ModifierKeys.Control, () => _vm.OpenSettingsCommand, true),
        new(Key.D, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.OpenActiveServerDesktopCommand, true),
        new(Key.F11, ModifierKeys.None, () => _vm.ToggleFullScreenCommand, true),
        new(Key.E, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.ToggleTerminalSftpCommand, true),
        new(Key.B, ModifierKeys.Control | ModifierKeys.Shift, () => _vm.ToggleTerminalBroadcastCommand, true),
    ];

    /// <summary>The key comes from a terminal's WebView2 (the page has the keyboard).</summary>
    private static bool FromTerminal(KeyEventArgs e)
        => e.OriginalSource is DependencyObject d && (d is WebView2 || Ui.FindAncestor<WebView2>(d) is not null);

    /// <returns>True when the key was a shortcut and has been handled.</returns>
    private bool HandleShortcut(KeyEventArgs e, out bool fromTerminal)
    {
        fromTerminal = FromTerminal(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        // Ctrl+Alt+←/→ gives the keyboard back to RDMan, like inside a remote desktop.
        if (fromTerminal && mods == (ModifierKeys.Control | ModifierKeys.Alt) && key is Key.Left or Key.Right)
        {
            if (_vm.ActiveTab?.Session is ViewModels.Terminal.SshSessionViewModel terminal) terminal.ReleaseFocus();
            e.Handled = true;
            return true;
        }

        foreach (var s in Shortcuts)
        {
            if (s.Key != key || s.Modifiers != mods) continue;
            if (fromTerminal && !s.InTerminal) return false;
            var command = s.Command();
            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
            return true;
        }
        return false;
    }
}
