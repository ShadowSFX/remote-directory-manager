using System.Windows;
using CommunityToolkit.Mvvm.Input;
using RDMan.Infrastructure;
using RDMan.Services;
using RDMan.Views;

namespace RDMan.ViewModels;

/// <summary>
/// Remote desktops in their own window ("undocked"). The RDP ActiveX can't be re-parented, so moving a session
/// out of a tab (or back) reconnects it: the remote Windows session — and the programs in it — stays as it was.
/// </summary>
public partial class MainViewModel
{
    private readonly List<SessionWindow> _detached = new();

    /// <summary>Desktop sessions living in their own window.</summary>
    public IEnumerable<RdpSessionViewModel> DetachedSessions => _detached.Select(w => w.Session);

    /// <summary>Every desktop session, in tabs and in windows.</summary>
    public IEnumerable<RdpSessionViewModel> AllSessions
        => AllTabs.Select(t => t.Session).OfType<RdpSessionViewModel>().Concat(DetachedSessions);

    /// <summary>Moves a tab's remote desktop to its own window (the tab closes, the session reconnects there).</summary>
    [RelayCommand]
    public void UndockDesktop(BrowserTabViewModel? tab)
    {
        if (tab is not { IsDesktop: true, Server: { } server }) return;
        if (FullScreenTab is { } fs && ReferenceEquals(fs, tab)) ExitFullScreen();
        var size = tab.Session?.Host is { ActualWidth: > 200, ActualHeight: > 150 } host ? new Size(host.ActualWidth, host.ActualHeight) : (Size?)null;
        var wasLive = tab.Session is { State: DesktopState.Connecting or DesktopState.Connected };
        tab.Session?.Disconnect(); // ends the embedded session first: a second one would sign it out
        tab.Pane.CloseTab(tab);
        OpenDesktopWindow(server, size);
        if (wasLive) Toasts.Show(Loc.F("Undock.Moved", server.Name));
    }

    /// <summary>Opens (or brings forward) a server's remote desktop in its own window.</summary>
    [RelayCommand]
    public void OpenNodeDesktopWindow(NodeViewModel? node)
    {
        if (node is not ServerNodeViewModel server) return;
        if (!server.HasDesktop)
        {
            OpenDesktop(server, OpenTarget.Current); // explains it isn't enabled
            return;
        }
        if (FindWindow(server) is { } open)
        {
            open.BringToFront();
            return;
        }
        // Already open in a tab: move that one instead of starting a session that would sign it out.
        if (FindTab(t => t.IsDesktop && ReferenceEquals(t.Server, server)) is { } hit)
        {
            UndockDesktop(hit.Tab);
            return;
        }
        OpenDesktopWindow(server, null);
    }

    private void OpenDesktopWindow(ServerNodeViewModel server, Size? contentSize)
    {
        if (FindWindow(server) is { } open)
        {
            open.BringToFront();
            return;
        }
        var window = new SessionWindow(this, new RdpSessionViewModel(this, server), contentSize, Owner);
        _detached.Add(window);
        window.Closed += (_, _) =>
        {
            _detached.Remove(window);
            NotifyConnectionsChanged();
        };
        window.Show();
        window.Session.Connect();
    }

    /// <summary>Closes the window and reopens the desktop as a tab of the active pane (reconnecting).</summary>
    public void DockDesktop(SessionWindow window)
    {
        var server = window.Session.Server;
        if (!window.CloseQuietly()) return;
        Owner?.Activate();
        OpenDesktop(server, OpenTarget.NewTab);
    }

    private SessionWindow? FindWindow(ServerNodeViewModel server)
        => _detached.FirstOrDefault(w => !w.IsClosing && ReferenceEquals(w.Session.Server, server));

    /// <summary>A window already shows this server's desktop: bring it forward (one session per server).</summary>
    private bool RevealDetached(ServerNodeViewModel server, RdpSessionViewModel? except = null)
    {
        if (FindWindow(server) is not { } w || ReferenceEquals(w.Session, except)) return false;
        if (w.Session.State is not (DesktopState.Connecting or DesktopState.Connected)) return false;
        w.BringToFront();
        Toasts.Show(Loc.F("Undock.AlreadyOpen", server.Name));
        return true;
    }

    /// <summary>App closing: asks once about every connected desktop, in tabs and in windows.</summary>
    public bool ConfirmEndAllSessions() => ConfirmEndSessions(AllSessions);

    private void CloseDetachedWindows()
    {
        foreach (var w in _detached.ToList())
        {
            try { w.CloseQuietly(force: true); }
            catch (InvalidOperationException ex) { App.Log(ex); } // already closing (its own prompt is open)
            w.Session.Dispose(); // ends the session even if the window couldn't close yet
        }
        _detached.Clear();
    }

    /// <summary>"RDMan.exe /c Name": opens the server's remote desktop (or its files when it has no desktop).</summary>
    public void ConnectByName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        var all = AllServers().ToList();
        var server = all.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.CurrentCultureIgnoreCase))
                     ?? all.FirstOrDefault(s => string.Equals(s.Host, name, StringComparison.OrdinalIgnoreCase));
        if (server is null)
        {
            Toasts.Show(Loc.F("CommandLine.NotFound", name), ToastKind.Error);
            return;
        }
        SelectInTree(server);
        if (server.HasDesktop) OpenDesktop(server, OpenTarget.NewTab);
        else OpenServer(server, OpenTarget.NewTab);
    }
}
