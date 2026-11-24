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

    /// <summary>Ctrl+Shift+E: the SFTP panel of the terminal in front.</summary>
    [RelayCommand]
    private void ToggleTerminalSftp()
    {
        if (ActiveTab?.Session is SshSessionViewModel t) t.ToggleSftp();
    }

    /// <summary>Ctrl+Shift+B: the terminal in front joins (or leaves) broadcast input.</summary>
    [RelayCommand]
    private void ToggleTerminalBroadcast()
    {
        if (ActiveTab?.Session is SshSessionViewModel { IsConnected: true } t) SetBroadcast(t, !t.IsBroadcasting);
    }

    /// <summary>Ctrl+Shift+T: another shell on the same server when a terminal is in front, otherwise a new tab.</summary>
    [RelayCommand]
    private void NewTerminalTab()
    {
        if (ActiveTab is { IsTerminal: true, Server: { } server }) OpenTerminal(server, OpenTarget.NewTab, newShell: true);
        else NewTabCommand.Execute(null);
    }

    /// <summary>
    /// Palette quick connect ("ubuntu@10.0.0.5", "ssh host -p 2222", a ~/.ssh/config alias): saves the server in the
    /// selected group (terminal only) and opens its terminal. One toast offers to undo the save.
    /// </summary>
    public void QuickConnect(Core.Ssh.SshDestination destination)
    {
        var parent = SelectedGroupForNew();
        var node = new Core.Models.ServerNode
        {
            Name = destination.Host,
            Host = destination.Host,
            Files = false,
            Terminal = new Core.Models.SshEndpoint { Port = destination.Port },
            DefaultAction = Core.Models.ServerAction.Terminal,
            SshAuth = new Core.Models.SshAuthSettings { Username = destination.User },
            Color = parent?.ColorKey,
        };
        var svm = (ServerNodeViewModel)CreateVm(node, parent);
        Insert(svm, parent);
        Changed();
        CheckHealth(svm);
        SelectInTree(svm);
        OpenTerminal(svm, OpenTarget.NewTab, newShell: true);
        Toasts.Show(Loc.F("Palette.Quick.Saved", node.Name), ToastKind.Success, Loc.T("Palette.Quick.Undo"), () =>
        {
            foreach (var tab in AllTabs.Where(t => ReferenceEquals(t.Server, svm)).ToList())
            {
                tab.Session?.Disconnect();
                tab.Pane.CloseTab(tab);
            }
            RemoveNode(svm);
        });
    }

    /// <summary>Types a snippet into the active terminal, filling {{host}}, {{user}}, {{server}} and asking for the rest.</summary>
    public void RunSnippet(Core.Models.Snippet snippet)
    {
        if (ActiveTab?.Session is not SshSessionViewModel { IsConnected: true } terminal || ActiveTab.Server is not { } server)
        {
            Toasts.Show(Loc.T("Snippets.NeedsTerminal"));
            return;
        }
        string? user = null;
        try { user = Ssh.Resolve(server.Server).User; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }

        var body = snippet.Body;
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = server.Host,
            ["server"] = server.Name,
            ["user"] = user ?? "",
        };
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body, @"\{\{\s*([\w.-]+)\s*\}\}"))
        {
            var name = m.Groups[1].Value;
            if (!known.ContainsKey(name))
            {
                var prompt = new Views.TextPromptWindow(snippet.Name, Loc.F("Snippets.Ask", name), name, Loc.T("Common.Ok"));
                if (Views.Dialogs.Show(prompt, Owner) != true) return;
                known[name] = prompt.Value;
            }
        }
        body = System.Text.RegularExpressions.Regex.Replace(body, @"\{\{\s*([\w.-]+)\s*\}\}", m => known[m.Groups[1].Value]);
        terminal.PasteText(body, warn: false);
        if (snippet.RunImmediately) terminal.SendInput("\r");
        terminal.Focus();
    }

    [RelayCommand]
    private void ManageSshKeys() => Views.Dialogs.Show(new Views.SshKeysWindow(new SshKeysViewModel(this)), Owner);

    [RelayCommand]
    private void ImportSshConfig()
    {
        var dlg = new Views.SshConfigImportWindow(new SshConfigImportViewModel(this));
        if (Views.Dialogs.Show(dlg, Owner) == true && dlg.Imported > 0)
            Toasts.Show(Loc.F(dlg.Imported == 1 ? "SshImport.DoneOne" : "SshImport.DoneMany", dlg.Imported), ToastKind.Success);
    }

    /// <summary>
    /// Creates terminal-only servers named after ~/.ssh/config hosts. They keep resolving through the config at connect
    /// time (edit the config and RDMan follows), exactly like typing <c>ssh alias</c>.
    /// </summary>
    public int AddSshConfigHosts(IReadOnlyList<string> aliases, GroupNodeViewModel? parent)
    {
        ServerNodeViewModel? last = null;
        foreach (var alias in aliases)
        {
            var node = new Core.Models.ServerNode
            {
                Name = alias,
                Host = alias,
                Files = false,
                Terminal = new Core.Models.SshEndpoint { UseSshConfig = true },
                DefaultAction = Core.Models.ServerAction.Terminal,
                Color = parent?.ColorKey,
            };
            last = (ServerNodeViewModel)CreateVm(node, parent);
            Insert(last, parent);
            CheckHealth(last);
        }
        if (last is null) return 0;
        Changed();
        if (parent is not null) parent.IsExpanded = true;
        SelectInTree(last);
        return aliases.Count;
    }

    [RelayCommand]
    private void ManageSnippets()
    {
        Views.Dialogs.Show(new Views.SnippetsWindow(new SnippetsViewModel(this)), Owner);
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
