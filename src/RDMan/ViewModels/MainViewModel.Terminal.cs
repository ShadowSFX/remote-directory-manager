using System.Runtime.InteropServices;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Input;
using RDMan.Infrastructure;
using RDMan.Interop;
using RDMan.Services;
using RDMan.Services.Ssh;
using RDMan.ViewModels.Terminal;

namespace RDMan.ViewModels;

/// <summary>SSH terminal: shared environment (ssh config, target resolution) and terminal-wide actions.</summary>
public sealed partial class MainViewModel
{
    private SshEnvironment? _ssh;

    /// <summary>The user's OpenSSH setup (~/.ssh/config, known_hosts) as RDMan sees it.</summary>
    public SshEnvironment Ssh => _ssh ??= new SshEnvironment(State);

    /// <summary>Every terminal session (tabs only: terminals don't undock).</summary>
    public IEnumerable<SshSessionViewModel> TerminalSessions => AllTabs.Select(t => t.Session).OfType<SshSessionViewModel>();

    /// <summary>
    /// Opens a terminal on the server. Unlike desktops, several terminals per server are normal: "open" reuses an
    /// existing terminal tab of that server when there is one, otherwise starts a new shell.
    /// </summary>
    public void OpenTerminal(ServerNodeViewModel server, OpenTarget target, bool newShell = false)
    {
        if (!server.HasTerminal)
        {
            Toasts.Show(Loc.F("Term.NotEnabled", server.Name), ToastKind.Info, Loc.T("Tree.Ctx.Properties"), () => EditNode(server));
            return;
        }
        if (!newShell && target == OpenTarget.Current
            && FindTab(t => t.Current is TerminalLocation tl && tl.ServerId == server.Model.Id) is { } hit)
        {
            Reveal(hit.Pane, hit.Tab);
            if (hit.Tab.Session is { IsLive: false } idle) idle.Connect();
            else hit.Tab.Session?.Focus();
            return;
        }

        var location = new TerminalLocation(server.Model.Id);
        switch (target)
        {
            case OpenTarget.NewTab:
                OpenInNewTab(location, ActivePane, t => t.RequestConnect());
                break;
            case OpenTarget.OtherPane:
                OpenInOtherPane(location, ActivePane, t => t.RequestConnect());
                break;
            default:
                if (ActiveTab is null || ActiveTab.Session is { IsConnected: true })
                    OpenInNewTab(location, ActivePane, t => t.RequestConnect());
                else
                {
                    ActiveTab.RequestConnect();
                    _ = ActiveTab.NavigateAsync(location);
                }
                break;
        }
    }

    /// <summary>Shows a tab wherever it lives and gives it the keyboard.</summary>
    public void RevealTab(BrowserTabViewModel tab)
    {
        Owner?.Activate();
        if (Panes.Contains(tab.Pane)) Reveal(tab.Pane, tab);
        tab.Session?.Focus();
    }

    public bool IsWindowActive => Owner is { IsActive: true, WindowState: not System.Windows.WindowState.Minimized };

    /// <summary>Flashes the taskbar button until the window is activated (bell, sign-in prompt in the background).</summary>
    public void FlashWindow()
    {
        if (Owner is null) return;
        var hwnd = new WindowInteropHelper(Owner).Handle;
        if (hwnd == IntPtr.Zero) return;
        var info = new NativeMethods.FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = NativeMethods.FLASHW_TRAY | NativeMethods.FLASHW_TIMERNOFG,
            uCount = 3,
        };
        NativeMethods.FlashWindowEx(ref info);
    }

    /// <summary>Terminal settings or a server's terminal options changed: restyle live terminals.</summary>
    public void ApplyTerminalSettings()
    {
        foreach (var t in TerminalSessions) t.ApplySettings();
    }

    [RelayCommand]
    private void OpenNodeTerminal(NodeViewModel? node)
    {
        if ((node ?? TreeSelection) is ServerNodeViewModel s) OpenTerminal(s, OpenTarget.Current);
    }

    [RelayCommand]
    private void OpenNodeTerminalInNewTab(NodeViewModel? node)
    {
        if ((node ?? TreeSelection) is ServerNodeViewModel s) OpenTerminal(s, OpenTarget.NewTab, newShell: true);
    }

    [RelayCommand]
    private void OpenNodeTerminalInOtherPane(NodeViewModel? node)
    {
        if ((node ?? TreeSelection) is ServerNodeViewModel s) OpenTerminal(s, OpenTarget.OtherPane, newShell: true);
    }

    /// <summary>Ctrl+Shift+T: another shell on the same server when a terminal is in front, otherwise a new tab.</summary>
    [RelayCommand]
    private void NewTerminalTab()
    {
        if (ActiveTab is { IsTerminal: true, Server: { } server }) OpenTerminal(server, OpenTarget.NewTab, newShell: true);
        else NewTabCommand.Execute(null);
    }

    /// <summary>"ssh -p 22 -i key user@host" for the server, as OpenSSH would need it.</summary>
    [RelayCommand]
    private void CopySshCommand(NodeViewModel? node)
    {
        if ((node ?? TreeSelection) is not ServerNodeViewModel s) return;
        try
        {
            var target = Ssh.Resolve(s.Server);
            System.Windows.Clipboard.SetText(target.ToCommandLine(target.Auth.Key?.SourcePath));
            Toasts.Show(Loc.T("Term.Toast.CommandCopied"), ToastKind.Success);
        }
        catch (Exception ex) when (ex is COMException or System.IO.IOException or InvalidOperationException)
        {
            Toasts.Show(ex.Message, ToastKind.Error);
        }
    }
}
