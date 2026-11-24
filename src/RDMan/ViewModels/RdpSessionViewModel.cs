using System.Runtime.InteropServices;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDMan.Core.Credentials;
using RDMan.Core.Models;
using RDMan.Core.Rdp;
using RDMan.Infrastructure;
using RDMan.Interop;
using RDMan.Services;
using RDMan.Views;
using RoyalApps.Community.Rdp.WinForms;
using RoyalApps.Community.Rdp.WinForms.Configuration;
using RoyalApps.Community.Rdp.WinForms.Configuration.Connection;
using RoyalApps.Community.Rdp.WinForms.Configuration.Display;
using RoyalApps.Community.Rdp.WinForms.Configuration.Performance;

namespace RDMan.ViewModels;

public enum DesktopState { Idle, Connecting, Connected, Error }

/// <summary>
/// One embedded Remote Desktop session (RoyalApps RdpControl → Microsoft's RDP ActiveX, hosted in a
/// WindowsFormsHost). Lives as long as its tab: switching tabs only hides it, so the session stays connected.
/// </summary>
public sealed partial class RdpSessionViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly AppServices _svc;
    private RdpControl? _control;
    private EventHandler<RoyalApps.Community.Rdp.WinForms.Controls.Events.ConnectedEventArgs>? _connectedHandler;
    private EventHandler<RoyalApps.Community.Rdp.WinForms.Controls.Events.DisconnectedEventArgs>? _disconnectedHandler;
    // Typed for this session only ("Remember" off). Kept DPAPI-protected, and only while the server's configured
    // desktop credential is still the one it replaced: editing the server/group/profile drops it.
    private (string User, string? ProtectedPassword, string Basis)? _sessionCredential;
    private bool _promptedAfterFailure;
    private bool _disposed;
    private readonly System.Windows.Threading.DispatcherTimer _sizeWatch = new();
    private int _resizeAttempts;
    private bool _resizeConfirmed; // the server applied a live resolution change at least once this session
    private (int W, int H)? _connectSize;

    public RdpSessionViewModel(MainViewModel main, ServerNodeViewModel server)
    {
        _main = main;
        _svc = main.Services;
        Server = server;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        _sizeWatch.Tick += SizeWatch_Tick;
    }

    public ServerNodeViewModel Server { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSurfaceVisible), nameof(IsConnected), nameof(IsBusy), nameof(ShowIdle))]
    private DesktopState _state = DesktopState.Idle;

    [ObservableProperty] private WindowsFormsHost? _host;
    [ObservableProperty] private ErrorInfo? _error;
    [ObservableProperty] private string _connectedAs = "";

    /// <summary>The ActiveX surface is shown while connecting (it paints its own progress) and when connected.</summary>
    public bool IsSurfaceVisible => State is DesktopState.Connecting or DesktopState.Connected;
    public bool IsConnected => State == DesktopState.Connected;
    public bool IsBusy => State == DesktopState.Connecting;
    public bool ShowIdle => State == DesktopState.Idle;
    public string ConnectingText => Loc.F("Desktop.Connecting", Server.Name);

    /// <summary>Raised when the user presses the release-focus hotkey inside the session (Ctrl+Alt+←/→).</summary>
    public event EventHandler? FocusReleased;

    /// <summary>Raised when the session takes the keyboard (a click inside the remote desktop), so its pane becomes active.</summary>
    public event EventHandler? Activated;

    /// <summary>Full screen requested (true) or left (false): toolbar button, or Ctrl+Alt+Pause inside the session.</summary>
    public event EventHandler<bool>? FullScreenRequested;

    // ───────────── Connect ─────────────
    [RelayCommand]
    public void Connect()
    {
        if (_disposed) return;
        if (!Server.HasDesktop)
        {
            ShowError(Loc.T("RdpErr.Generic.Title"), Loc.F("Desktop.NotEnabled", Server.Name), "", RdpErrorAction.None);
            return;
        }
        // One session per server: a second one with the same account would kick the first ("replaced by another connection").
        if (_main.RevealOtherSession(this)) return;

        var credential = BuildCredential(out var cancelled);
        if (cancelled) return;

        TearDown();
        Error = null;
        var control = new RdpControl();
        Configure(control, credential);
        // The library raises these with the inner ActiveX client as sender, not the RdpControl: bind each handler
        // to *this* control so events from a control we already replaced are ignored.
        _connectedHandler = (_, e) => { if (ReferenceEquals(_control, control)) OnConnected(e); };
        _disconnectedHandler = (_, e) => { if (ReferenceEquals(_control, control)) OnDisconnected(e); };
        control.OnConnected += _connectedHandler;
        control.OnDisconnected += _disconnectedHandler;
        control.RdpClientConfigured += OnClientConfigured;
        control.Enter += OnControlEnter;
        control.SizeChanged += OnControlSizeChanged;
        _control = control;

        var host = new WindowsFormsHost { Child = control, Focusable = true };
        host.IsKeyboardFocusWithinChanged += OnHostFocusWithinChanged;
        // The ActiveX needs a window handle: connect once the host is in the visual tree.
        void OnLoaded(object? s, System.Windows.RoutedEventArgs e)
        {
            host.Loaded -= OnLoaded;
            if (!ReferenceEquals(_control, control) || _disposed) return;
            try
            {
                control.Connect();
            }
            catch (COMException ex)
            {
                App.Log(ex);
                ShowError(Loc.T("RdpErr.Generic.Title"), Loc.T("RdpErr.Generic.Detail"), ex.Message, RdpErrorAction.Reconnect);
            }
        }
        host.Loaded += OnLoaded;
        Host = host;
        ConnectedAs = DescribeUser(credential.User);
        State = DesktopState.Connecting;
    }

    private sealed record ConnectCredential(string? User, string? Domain, string? Password);

    /// <summary>
    /// Resolves who to sign in as. Saved Windows credentials (CredSSP) are used as-is — RDMan never reads their secret;
    /// it only looks up the user name so Windows can match it. Anything unusable opens the sign-in prompt.
    /// </summary>
    private ConnectCredential BuildCredential(out bool cancelled)
    {
        cancelled = false;
        var eff = _svc.State.ResolveDesktopCredential(Server.Model);
        if (_sessionCredential is { } typed)
        {
            if (typed.Basis == BasisOf(eff)) return Split(typed.User, _svc.State.Secrets.TryUnprotect(typed.ProtectedPassword));
            _sessionCredential = null; // the server's credential changed since it was typed
        }
        switch (eff.Kind)
        {
            case EffectiveCredentialKind.Explicit:
                var pwd = _svc.State.Secrets.TryUnprotect(eff.ProtectedPassword);
                if (pwd is null && eff.ProtectedPassword is not null)
                    return Prompt(eff.Username, out cancelled); // encrypted on another PC/user
                return Split(eff.Username!, pwd);
            case EffectiveCredentialKind.CurrentUser when !eff.IsMisconfigured:
                var savedUser = WindowsCredentialLookup.FindUser(Server.Host);
                if (savedUser is not null) return Split(savedUser, null);
                return Prompt(null, out cancelled);
            default:
                // Guest isn't a thing for Remote Desktop, and a broken profile/custom entry has nothing to offer.
                return Prompt(eff.Kind == EffectiveCredentialKind.Guest ? null : eff.Username, out cancelled);
        }
    }

    private static ConnectCredential Split(string user, string? password)
    {
        var (u, d) = CredentialResolver.SplitUser(user);
        // An empty password means "none": let CredSSP use the saved Windows credential for that user.
        return new ConnectCredential(u, d, string.IsNullOrEmpty(password) ? null : password);
    }

    private static string BasisOf(EffectiveCredential eff)
        => $"{eff.Kind}|{eff.Username}|{eff.ProtectedPassword}|{eff.Profile?.Id}|{eff.SourceNode?.Id}|{eff.IsMisconfigured}";

    private ConnectCredential Prompt(string? user, out bool cancelled)
    {
        // "Remember" stores the account on this server; say so when that replaces an inherited or shared one.
        var overrides = Server.Model.DesktopCredentials.Mode != CredentialMode.Custom;
        var dlg = new CredentialPromptWindow(Server.Name, $"{Server.Host}:{Server.DesktopPort}", Server.ColorBrush, user, overrides);
        if (Dialogs.Show(dlg, _main.Owner) != true || dlg.Result is null)
        {
            cancelled = true;
            // Keep an error panel if one is showing (never stack the error and idle panels).
            if (State is DesktopState.Connecting) State = DesktopState.Idle;
            return new ConnectCredential(null, null, null);
        }
        cancelled = false;
        var r = dlg.Result;
        if (r.Remember)
        {
            Server.Model.DesktopCredentials = new CredentialSettings
            {
                Mode = CredentialMode.Custom,
                Username = r.Username,
                ProtectedPassword = _svc.State.Secrets.Protect(r.Password),
            };
            _svc.State.MarkDirty();
            _sessionCredential = null;
        }
        else
        {
            var eff = _svc.State.ResolveDesktopCredential(Server.Model);
            _sessionCredential = (r.Username, r.Password.Length == 0 ? null : _svc.State.Secrets.Protect(r.Password), BasisOf(eff));
        }
        return Split(r.Username, r.Password);
    }

    private string DescribeUser(string? user)
        => Loc.F("Status.ConnectedAs", string.IsNullOrEmpty(user) ? Loc.T("Desktop.SavedWindowsCredential") : user);

    private void Configure(RdpControl control, ConnectCredential credential)
    {
        var s = _svc.State.ResolveRdpSettings(Server.Model);
        var cfg = control.RdpConfiguration;
        cfg.Server = Server.Host;
        cfg.Port = Server.DesktopPort;

        cfg.Credentials.Username = credential.User;
        cfg.Credentials.Domain = credential.Domain;
        if (!string.IsNullOrEmpty(credential.Password)) cfg.Credentials.Password = new SensitiveString(credential.Password);
        cfg.Credentials.NetworkLevelAuthentication = s.NetworkLevelAuthentication.Value;

        // Fixes RDCMan's biggest pain: resizing the tab/window changes the remote resolution, no reconnect.
        cfg.Display.ResizeBehavior = ResizeBehavior.UpdateDesktopSize;
        cfg.Display.ColorDepth = s.ColorDepth.Value switch { 16 => ColorDepth.ColorDepth16Bpp, 24 => ColorDepth.ColorDepth24Bpp, _ => ColorDepth.ColorDepth32Bpp };
        cfg.Display.AutoScaling = true;
        // RDMan does full screen itself (the window covers the monitor and shows only the session), so the remote
        // resolution follows the monitor through the same resize path. The control only reports Ctrl+Alt+Pause.
        cfg.Display.ContainerHandledFullScreen = true;

        cfg.Input.KeyboardHookMode = s.WindowsKeysToServer.Value;
        cfg.Input.EnableWindowsKey = s.WindowsKeysToServer.Value;
        cfg.Input.GrabFocusOnConnect = true;

        cfg.Redirection.RedirectClipboard = s.RedirectClipboard.Value;
        cfg.Redirection.RedirectDrives = s.RedirectDrives.Value;
        cfg.Redirection.RedirectPrinters = s.RedirectPrinters.Value;
        cfg.Redirection.RedirectSmartCards = s.RedirectSmartCards.Value;
        cfg.Redirection.AudioRedirectionMode = s.Audio.Value switch
        {
            RdpAudio.Remote => AudioRedirectionMode.PlayRemote,
            RdpAudio.Off => AudioRedirectionMode.DisableSound,
            _ => AudioRedirectionMode.RedirectToClient,
        };

        cfg.Security.AuthenticationLevel = s.ServerCheck.Value switch
        {
            RdpServerCheck.Require => AuthenticationLevel.ServerAuthenticationRequired,
            RdpServerCheck.Skip => AuthenticationLevel.NoAuthenticationOfServer,
            _ => AuthenticationLevel.PromptIfAuthenticationFails,
        };
        cfg.Security.ConnectToAdministerServer = s.AdminSession.Value;
        cfg.Security.RestrictedAdminMode = s.SignInProtection.Value == RdpSignInProtection.RestrictedAdmin;
        cfg.Security.RemoteCredentialGuard = s.SignInProtection.Value == RdpSignInProtection.RemoteCredentialGuard;

        cfg.Connection.EnableAutoReconnect = true;
        cfg.Connection.MaxReconnectAttempts = 20;

        // The overlay painted while connecting matches the app surface instead of flashing black.
        if (System.Windows.Application.Current.TryFindResource("Bg.Content") is SolidColorBrush bg)
            control.InitialConnectOverlayBackColor = System.Drawing.Color.FromArgb(bg.Color.A, bg.Color.R, bg.Color.G, bg.Color.B);
        if (System.Windows.Application.Current.TryFindResource("Fg.Secondary") is SolidColorBrush fg)
            control.InitialConnectOverlayForeColor = System.Drawing.Color.FromArgb(fg.Color.A, fg.Color.R, fg.Color.G, fg.Color.B);
    }

    private void OnClientConfigured(object? sender, EventArgs e)
    {
        // RoyalApps picks the newest ActiveX the OS has (RdpClient12 wraps AxMsRdpClient11, older ones wrap older Ax
        // classes) and those don't share a base type: bind the event by name so focus release works on every version.
        if (_control?.RdpClient is not { } client) return;
        var type = client.GetType();
        type.GetEvent("OnFocusReleased")?.AddEventHandler(client,
            new AxMSTSCLib.IMsTscAxEvents_OnFocusReleasedEventHandler((_, _) => FocusReleased?.Invoke(this, EventArgs.Empty)));
        // RDMan's top bar replaces mstsc's connection bar (the ActiveX shows its own once FullScreen is set).
        try
        {
            if (client.GetOcx() is MSTSCLib.IMsRdpClientNonScriptable5 ns) ns.DisableConnectionBar = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { App.Log(ex); }
        // Ctrl+Alt+Pause inside the session (container-handled full screen).
        BindPlain(client, type, "OnRequestGoFullScreen", () => FullScreenRequested?.Invoke(this, true));
        BindPlain(client, type, "OnRequestLeaveFullScreen", () => FullScreenRequested?.Invoke(this, false));
    }

    private static void BindPlain(object target, Type type, string name, Action action)
    {
        if (type.GetEvent(name) is { EventHandlerType: { } t } ev && t == typeof(EventHandler))
            ev.AddEventHandler(target, new EventHandler((_, _) => action()));
    }

    /// <summary>Keeps the control's own full-screen flag in step with RDMan's, so Ctrl+Alt+Pause toggles correctly.</summary>
    public void SyncFullScreen(bool on)
    {
        if (_control?.RdpClient is not { } client) return;
        try
        {
            if (client.FullScreen != on) client.FullScreen = on;
        }
        catch (COMException ex) { App.Log(ex); }
    }

    private void OnControlEnter(object? sender, EventArgs e) => Activated?.Invoke(this, EventArgs.Empty);

    private void OnHostFocusWithinChanged(object? sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) Activated?.Invoke(this, EventArgs.Empty);
    }

    // ───────────── Events ─────────────
    private void OnConnected(RoyalApps.Community.Rdp.WinForms.Controls.Events.ConnectedEventArgs e)
    {
        if (_disposed) return;
        State = DesktopState.Connected;
        _resizeConfirmed = false;
        _connectSize = RemoteSize();
        WatchSize();
        _promptedAfterFailure = false;
        Server.Server.LastConnected = DateTimeOffset.Now;
        _svc.State.MarkDirty();
    }

    private void OnDisconnected(RoyalApps.Community.Rdp.WinForms.Controls.Events.DisconnectedEventArgs e)
    {
        if (_disposed) return;
        var t = RdpErrorTranslator.Translate(e.DisconnectCode, e.ExtendedDisconnectReasonCode ?? 0);
        if (!t.IsError || e.UserInitiated)
        {
            // Signed out / closed on the server: release the ActiveX now instead of keeping it until the tab closes.
            State = DesktopState.Idle;
            TearDown();
            return;
        }

        // Windows rejected the saved/stored credential: ask once and retry instead of showing a dead end.
        if (t.Action == RdpErrorAction.EditCredentials && !_promptedAfterFailure)
        {
            _promptedAfterFailure = true;
            _sessionCredential = null;
            // Show the error underneath right away; the prompt may be cancelled.
            ShowTranslated(t, e.Description);
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                // The tab may have been closed, or the user already reconnected, while this was queued.
                if (_disposed || State != DesktopState.Error) return;
                var user = _svc.State.ResolveDesktopCredential(Server.Model).Username ?? WindowsCredentialLookup.FindUser(Server.Host);
                Prompt(user, out var cancelled);
                if (!cancelled) Connect(); // Prompt stored the account (on the server, or for this session)
            });
            return;
        }
        ShowTranslated(t, e.Description);
    }

    private void ShowTranslated(RdpTranslatedError t, string? description)
    {
        var codeText = Loc.F("RdpErr.Code", t.ExtendedCode != 0 ? $"{t.Code}/{t.ExtendedCode}" : t.Code.ToString(), description ?? "");
        ShowError(Loc.T(t.TitleKey), Loc.T(t.DetailKey), codeText, t.Action);
    }

    private void ShowError(string title, string detail, string code, RdpErrorAction action)
    {
        Error = new ErrorInfo
        {
            Title = title,
            Detail = detail,
            CodeText = code,
            Action = action switch
            {
                RdpErrorAction.EditCredentials => Core.Smb.ErrorAction.EditCredentials,
                RdpErrorAction.CheckHost => Core.Smb.ErrorAction.CheckHost,
                _ => Core.Smb.ErrorAction.Retry,
            },
            PrimaryText = action == RdpErrorAction.EditCredentials ? Loc.T("Err.EditCredentials") : Loc.T("Desktop.Reconnect"),
        };
        State = DesktopState.Error;
        TearDown();
    }

    // ───────────── Actions ─────────────
    [RelayCommand]
    public void Disconnect()
    {
        if (_control is null) return;
        State = DesktopState.Idle;
        TearDown();
    }

    [RelayCommand]
    private void Reconnect()
    {
        // The toolbar's Reconnect is where the keyboard lands after Ctrl+Alt+←: never drop a live session on a stray Enter.
        if (IsConnected && !Dialogs.Confirm(_main.Owner, Loc.T("Desktop.Reconnect"), Loc.F("Desktop.ConfirmReconnect", Server.Name), Loc.T("Desktop.Reconnect"), danger: false))
            return;
        _promptedAfterFailure = false;
        Connect();
    }

    [RelayCommand]
    private void ErrorPrimary()
    {
        if (Error?.Action == Core.Smb.ErrorAction.EditCredentials)
        {
            _sessionCredential = null;
            var user = _svc.State.ResolveDesktopCredential(Server.Model).Username ?? WindowsCredentialLookup.FindUser(Server.Host);
            Prompt(user, out var cancelled);
            if (cancelled) return;
        }
        _promptedAfterFailure = false;
        Connect();
    }

    /// <summary>Sends Ctrl+Alt+Del to the server (scancodes: Ctrl 0x1D, Alt 0x38, Del 0x53 extended).</summary>
    [RelayCommand]
    private void SendCtrlAltDel()
    {
        if (!IsConnected || _control?.RdpClient?.GetOcx() is not IMsRdpClientNonScriptableKeys keys) return;
        const int extended = 0x100;
        try
        {
            _control.FocusRdpClient();
            keys.SendKeys(6,
                [false, false, false, true, true, true],
                [0x1D, 0x38, 0x53 | extended, 0x53 | extended, 0x38, 0x1D]);
        }
        catch (COMException ex)
        {
            App.Log(ex);
        }
    }

    [RelayCommand]
    private void FullScreen()
    {
        if (IsConnected) FullScreenRequested?.Invoke(this, true);
    }

    public void Focus()
    {
        if (IsConnected) _control?.FocusRdpClient();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(ConnectingText));

    // ───────────── Lifetime ─────────────
    // ───────────── Remote resolution watchdog ─────────────
    // The server ignores a resolution change while it shows a secure desktop (Ctrl+Alt+Del, UAC, sign-in), so a
    // resize that happened meanwhile (leaving full screen, splitting) would leave the old size with scrollbars.
    // While the sizes differ, keep asking until the server applies it.
    private void OnControlSizeChanged(object? sender, EventArgs e)
    {
        if (IsConnected) WatchSize();
    }

    private void WatchSize()
    {
        _resizeAttempts = 0;
        _sizeWatch.Interval = TimeSpan.FromSeconds(1);
        _sizeWatch.Start();
    }

    private (int W, int H)? RemoteSize()
    {
        try { return _control?.RdpClient is { } c ? (c.DesktopWidth, c.DesktopHeight) : null; }
        catch (COMException) { return null; }
    }

    private void SizeWatch_Tick(object? sender, EventArgs e)
    {
        if (!IsConnected || _control is null)
        {
            _sizeWatch.Stop();
            return;
        }
        if (Host is not { IsVisible: true }) return; // hidden tab: check again when shown
        if (RemoteSize() is not { } remote) return;
        var local = _control.ClientSize;
        // The server rounds widths to even pixels; a few pixels of difference is "the same size".
        if (Math.Abs(remote.W - local.Width) <= 4 && Math.Abs(remote.H - local.Height) <= 4)
        {
            if (_connectSize is { } first && remote != first) _resizeConfirmed = true;
            _sizeWatch.Stop();
            return;
        }
        // Servers without live resolution changes (2008 R2 and older) never match: stop asking after a few tries.
        if (!_resizeConfirmed && _resizeAttempts >= 10)
        {
            _sizeWatch.Stop();
            return;
        }
        _resizeAttempts++;
        if (_resizeAttempts == 10) _sizeWatch.Interval = TimeSpan.FromSeconds(3);
        try { _control.UpdateClientSize(); }
        catch (COMException ex) { App.Log(ex); }
    }

    private void TearDown()
    {
        _sizeWatch.Stop();
        var control = _control;
        var host = Host;
        _control = null;
        if (control is not null)
        {
            if (_connectedHandler is not null) control.OnConnected -= _connectedHandler;
            if (_disconnectedHandler is not null) control.OnDisconnected -= _disconnectedHandler;
            control.RdpClientConfigured -= OnClientConfigured;
            control.Enter -= OnControlEnter;
            control.SizeChanged -= OnControlSizeChanged;
            try
            {
                if (control.RdpClient?.ConnectionState is RoyalApps.Community.Rdp.WinForms.Controls.Clients.ConnectionState.Connected
                    or RoyalApps.Community.Rdp.WinForms.Controls.Clients.ConnectionState.Connecting)
                    control.Disconnect();
            }
            catch (COMException ex) { App.Log(ex); }
        }
        if (host is not null) host.IsKeyboardFocusWithinChanged -= OnHostFocusWithinChanged;
        Host = null;
        // Dispose after the host left the visual tree, so WPF never renders a dead HWND.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (host is not null)
            {
                host.Child = null;
                host.Dispose();
            }
            control?.Dispose();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        TearDown();
    }
}
