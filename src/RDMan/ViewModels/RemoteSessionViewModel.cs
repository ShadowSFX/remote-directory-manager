using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RDMan.ViewModels;

/// <summary>
/// What tabs, panes, the session overlay and the window know about a live remote session, whatever its kind:
/// a remote desktop (<see cref="RdpSessionViewModel"/>) or an SSH terminal (<see cref="Terminal.SshSessionViewModel"/>).
/// Both put a native window (<see cref="Host"/>) into the window-level overlay, which never re-parents it.
/// </summary>
public abstract partial class RemoteSessionViewModel : ObservableObject, IDisposable
{
    protected RemoteSessionViewModel(MainViewModel main, ServerNodeViewModel server)
    {
        Main = main;
        Server = server;
    }

    protected MainViewModel Main { get; }

    public ServerNodeViewModel Server { get; }

    /// <summary>The session's native surface (ActiveX host or WebView2), placed by the window's session overlay.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSurfaceVisible), nameof(ShowIdle))]
    private HwndHost? _host;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIdle))]
    private ErrorInfo? _error;

    [ObservableProperty] private string _connectedAs = "";

    /// <summary>The native surface is shown (the overlay places it over the pane).</summary>
    public abstract bool IsSurfaceVisible { get; }
    public abstract bool IsConnected { get; }
    public abstract bool IsBusy { get; }
    /// <summary>The "not connected yet" card is shown instead of the surface.</summary>
    public abstract bool ShowIdle { get; }
    /// <summary>Connecting or connected (what "one session per server" and "leave?" checks care about).</summary>
    public bool IsLive => IsConnected || IsBusy;

    public abstract bool IsTerminal { get; }
    /// <summary>Glyph and text of the idle card.</summary>
    public abstract string IdleGlyph { get; }
    public abstract string IdleText { get; }
    /// <summary>"Leaving ends the session with X" (tab close, navigation, window close).</summary>
    public abstract string ConfirmLeaveText { get; }

    /// <summary>Prompts open over the window that shows the session (a tab's main window or an undocked one).</summary>
    public Window? DialogOwner => (Host is not null ? Window.GetWindow(Host) : null) ?? Main.Owner;

    /// <summary>Raised when the user releases the keyboard from the session (Ctrl+Alt+←/→).</summary>
    public event EventHandler? FocusReleased;

    /// <summary>Raised when the session takes the keyboard (a click inside it), so its pane becomes active.</summary>
    public event EventHandler? Activated;

    /// <summary>Full screen requested (true) or left (false).</summary>
    public event EventHandler<bool>? FullScreenRequested;

    protected void RaiseFocusReleased() => FocusReleased?.Invoke(this, EventArgs.Empty);
    protected void RaiseActivated() => Activated?.Invoke(this, EventArgs.Empty);
    protected void RaiseFullScreenRequested(bool on) => FullScreenRequested?.Invoke(this, on);

    public abstract void Connect();
    public abstract void Disconnect();
    /// <summary>Gives the keyboard to the session.</summary>
    public abstract void Focus();
    /// <summary>The window entered or left full screen (sessions that track it themselves override this).</summary>
    public virtual void SyncFullScreen(bool on) { }
    public abstract void Dispose();
}
