using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDMan.Core.Models;
using RDMan.Core.Persistence;
using RDMan.Core.Ssh;
using RDMan.Infrastructure;
using RDMan.Services;
using RDMan.Services.Ssh;
using RDMan.Terminal;
using RDMan.Views;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RDMan.ViewModels.Terminal;

public enum TerminalState
{
    /// <summary>Never connected in this tab (the WPF idle card is shown).</summary>
    Idle,
    Connecting,
    Connected,
    /// <summary>Connection lost; waiting before the next automatic attempt.</summary>
    Reconnecting,
    /// <summary>The session ended or failed; the terminal stays visible with its history (Enter reconnects).</summary>
    Ended,
}

/// <summary>
/// One SSH terminal tab: the WebView2 screen (created once, kept across reconnects so history survives), the SSH
/// connection and its shell channel, and everything that hangs off it (stats, forwards, log).
/// </summary>
public sealed partial class SshSessionViewModel : RemoteSessionViewModel, ISshPrompts
{
    private const int HighWater = 2 * 1024 * 1024;   // chars sent to the page and not yet rendered
    private const int LowWater = 512 * 1024;
    private static readonly int[] Backoff = [1, 2, 4, 8, 15, 30, 30, 60, 60, 60];

    private readonly MainViewModel _main;
    private readonly AppServices _svc;
    private readonly SshEnvironment _env;
    private readonly Dispatcher _ui;
    private TerminalHost? _terminal;
    private TaskCompletionSource<PtySize> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PtySize _size = new(120, 32, 0, 0);
    private int _generation;
    private CancellationTokenSource? _connectCts;
    private SshLink<SshClient>? _link;
    private TerminalChannel? _channel;
    private StatsProbe? _stats;
    private SessionLogger? _logger;
    private readonly ForwardingManager _forwards = new();
    private System.Threading.Channels.Channel<byte[]>? _input;
    private bool _forwardsLoaded;
    private bool _disposed;

    // Output pump (see OnData): decoded on SSH.NET's thread, flushed to the page on the UI thread.
    private readonly object _outGate = new();
    private readonly StringBuilder _pending = new();
    private Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private char[] _chars = new char[64 * 1024];
    private bool _flushQueued;
    private long _unacked;
    private readonly ManualResetEventSlim _canRead = new(true);

    // Session-only memory: passwords/passphrases typed (never saved unless asked), host keys accepted once.
    private string? _sessionPassword;
    private string? _sessionUser;
    private readonly Dictionary<string, string> _passphrases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _trustedOnce = new(StringComparer.OrdinalIgnoreCase);
    private bool _passwordPromptedAfterFailure;

    // Reconnect
    private readonly DispatcherTimer _reconnectTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _reconnectAttempt;
    private int _reconnectCountdown;

    // In-page cards and banners: action id → handler.
    private readonly Dictionary<string, Action<JsonElement>> _actions = new();
    private int _actionSeq;
    private readonly Dictionary<string, TaskCompletionSource<string>> _serialized = new();

    private readonly DispatcherTimer _thumbTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public SshSessionViewModel(MainViewModel main, ServerNodeViewModel server) : base(main, server)
    {
        _main = main;
        _svc = main.Services;
        _env = main.Ssh;
        _ui = Application.Current.Dispatcher;
        _reconnectTimer.Tick += ReconnectTick;
        _thumbTimer.Tick += async (_, _) => await CaptureThumbnailAsync();
        ThemeManager.Instance.ThemeChanged += OnThemeChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsBusy), nameof(ShowIdle), nameof(StateText))]
    private TerminalState _state = TerminalState.Idle;

    [ObservableProperty] private string? _remoteTitle;
    [ObservableProperty] private string? _currentDirectory;
    /// <summary>OSC 9;4 progress: null = none, 0-100, or -1 = indeterminate.</summary>
    [ObservableProperty] private int? _progressValue;
    [ObservableProperty] private bool _progressError;

    // Stats band
    [ObservableProperty] private bool _hasStats;
    [ObservableProperty] private string? _osName;
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _memPercent;
    [ObservableProperty] private double _diskPercent;
    [ObservableProperty] private string _loadText = "";
    [ObservableProperty] private string _uptimeText = "";
    [ObservableProperty] private string _memText = "";
    [ObservableProperty] private string _diskText = "";
    public ObservableCollection<double> CpuHistory { get; } = new();
    [ObservableProperty] private PointCollection? _cpuSparkline;
    [ObservableProperty] private string _cpuLevel = "ok";
    [ObservableProperty] private string _memLevel = "ok";
    [ObservableProperty] private string _diskLevel = "ok";

    public ObservableCollection<ForwardViewModel> Forwards => _forwards.Items;
    public bool HasForwards => _forwards.Items.Count > 0;

    // Host: the WebView2 control. Error: only failures before a terminal exists (WebView2 missing); later errors
    // show inside the terminal, next to its history.
    public override bool IsSurfaceVisible => Host is not null;
    public override bool IsConnected => State == TerminalState.Connected;
    public override bool IsBusy => State is TerminalState.Connecting or TerminalState.Reconnecting;
    public override bool ShowIdle => Host is null && State == TerminalState.Idle && Error is null;
    public override bool IsTerminal => true;
    public override string IdleGlyph => "";
    public override string IdleText => Loc.T("Term.Idle.Body");
    public override string ConfirmLeaveText => Loc.F("Term.ConfirmLeave", Server.Name);
    public string? LogPath => _logger?.Path;

    public string StateText => State switch
    {
        TerminalState.Connecting => Loc.T("Term.State.Connecting"),
        TerminalState.Connected => Loc.T("Term.State.Connected"),
        TerminalState.Reconnecting => Loc.T("Term.State.Reconnecting"),
        TerminalState.Ended => Loc.T("Term.State.Ended"),
        _ => "",
    };

    /// <summary>BEL from the server (tab badge, taskbar flash when the window isn't active).</summary>
    public event EventHandler? Bell;
    /// <summary>The server is waiting for the user (password, host key) — useful when the tab isn't visible.</summary>
    public event EventHandler<string>? AttentionRequested;
    /// <summary>Keystrokes typed in this terminal (for broadcast input).</summary>
    public event Action<SshSessionViewModel, string>? InputTyped;

    partial void OnStateChanged(TerminalState value)
    {
        if (value == TerminalState.Connected && !_thumbTimer.IsEnabled) _thumbTimer.Start();
        if (value != TerminalState.Connected) _thumbTimer.Stop();
    }

    // ───────────── Connect ─────────────

    [RelayCommand]
    public override void Connect()
    {
        if (_disposed || IsBusy || IsConnected) return;
        if (!Server.Server.HasTerminal)
        {
            ShowFatal(Loc.T("SshErr.Unknown.Title"), Loc.F("Term.NotEnabled", Server.Name), SshErrorAction.EditServer);
            return;
        }
        if (!EnsureHost()) return;
        StopReconnect();
        _ = ConnectAsync(isReconnect: false);
    }

    private bool EnsureHost()
    {
        if (_terminal is not null) return true;
        if (TerminalHost.RuntimeVersion() is null)
        {
            var t = SshErrorTranslator.Translate(SshFailure.WebViewMissing);
            ShowFatal(Loc.T(t.TitleKey), Loc.T(t.DetailKey), SshErrorAction.InstallWebView);
            return false;
        }
        Error = null;
        var settings = _env.ResolveSettings(Server.Server);
        var scheme = TerminalTheme.SchemeFor(settings.ColorScheme.Value);
        var host = new TerminalHost(TerminalTheme.BackgroundColor(scheme, Tint));
        host.Message += OnHostMessage;
        host.Failed += ex => _ui.BeginInvoke(() => OnHostFailed(ex));
        host.View.IsKeyboardFocusWithinChanged += OnHostFocusWithinChanged;
        _terminal = host;
        _ready = new TaskCompletionSource<PtySize>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Send(new { t = "init", options = PageOptions(settings), theme = BuildTheme(settings), strings = PageStrings() });
        Host = host.View;
        return true;
    }

    private string? Tint => _svc.State.Settings.Terminal.TintWithServerColor ? Server.Server.Color : null;

    private object BuildTheme(EffectiveSshSettings settings)
        => TerminalTheme.Build(TerminalTheme.SchemeFor(settings.ColorScheme.Value), Tint, _svc.State.Settings.Terminal.MinimumContrast);

    private object PageOptions(EffectiveSshSettings settings)
    {
        var t = _svc.State.Settings.Terminal;
        return new
        {
            fontFamily = t.FontFamily,
            fontSize = settings.FontSize.Value ?? t.FontSize,
            lineHeight = t.LineHeight,
            letterSpacing = t.LetterSpacing,
            fontWeight = t.FontWeight,
            fontWeightBold = t.FontWeightBold,
            cursor = t.Cursor.ToString().ToLowerInvariant(),
            cursorBlink = t.CursorBlink,
            scrollback = t.Scrollback,
            minimumContrast = t.MinimumContrast,
            renderer = t.Renderer == TerminalRenderer.Gpu ? "gpu" : "dom",
            smartCtrlC = t.SmartCtrlC,
            copyOnSelect = t.CopyOnSelect,
            rightClick = t.RightClick == TerminalRightClick.CopyPaste ? "copypaste" : "menu",
            bell = t.Bell.ToString().ToLowerInvariant(),
            wordSeparators = t.WordSeparators,
        };
    }

    private static Dictionary<string, string> PageStrings()
    {
        string[] keys =
        [
            "Term.Search.Placeholder", "Term.Search.Case", "Term.Search.Word", "Term.Search.Regex", "Term.Search.Prev",
            "Term.Search.Next", "Term.Search.Close", "Term.Search.None", "Term.Menu.Copy", "Term.Menu.Paste",
            "Term.Menu.SelectAll", "Term.Menu.Find", "Term.Menu.Save", "Term.Menu.Clear",
        ];
        return keys.ToDictionary(k => k, Loc.T);
    }

    /// <summary>Settings or theme changed: restyle the live terminal without touching the session.</summary>
    public void ApplySettings()
    {
        if (_terminal is null) return;
        var settings = _env.ResolveSettings(Server.Server);
        _terminal.Send(new { t = "options", options = PageOptions(settings) });
        _terminal.Send(new { t = "theme", theme = BuildTheme(settings) });
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplySettings();

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(StateText));
        _terminal?.Send(new { t = "strings", strings = PageStrings() });
    }

    private async Task ConnectAsync(bool isReconnect)
    {
        var gen = ++_generation;
        _connectCts?.Cancel();
        var cts = _connectCts = new CancellationTokenSource();
        var ct = cts.Token;
        TearDownConnection();
        State = isReconnect ? TerminalState.Reconnecting : TerminalState.Connecting;
        if (!isReconnect) Banner(null);

        SshTarget target;
        try
        {
            target = _env.Resolve(Server.Server);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Fail(gen, new SshConnectException(SshFailure.Unknown, ex.Message, ex), isReconnect);
            return;
        }

        if (!string.IsNullOrEmpty(_sessionUser) && string.IsNullOrWhiteSpace(target.User)) target = target with { User = _sessionUser };
        if (string.IsNullOrWhiteSpace(target.User))
        {
            var answer = await AskPasswordAsync(null, true, ct);
            if (gen != _generation) return;
            if (answer is null) { Cancelled(gen); return; }
            target = target with { User = answer.User };
            if (answer.Password.Length > 0) _sessionPassword = answer.Password;
        }

        var password = _sessionPassword;
        if (target.Auth.Mode == SshAuthMode.Password && target.Auth.ProtectedPassword is { } protectedPassword)
            password = _svc.State.Secrets.TryUnprotect(protectedPassword) ?? password;
        if (target.Auth.Mode == SshAuthMode.Password && string.IsNullOrEmpty(password))
        {
            var answer = await AskPasswordAsync(target.User, false, ct);
            if (gen != _generation) return;
            if (answer is null) { Cancelled(gen); return; }
            password = answer.Password;
            _sessionPassword = password;
        }

        Line(Dim(Loc.F(isReconnect ? "Term.Line.Reconnecting" : "Term.Line.Connecting", target.Display)));
        if (target.Auth.IsMisconfigured) Line(Warn(Loc.T("Term.Line.AuthMisconfigured")));
        foreach (var note in target.Config?.Notes ?? []) Line(Dim("  " + note));

        var request = new SshConnectRequest
        {
            Target = target,
            Config = _env.Config,
            Secrets = _svc.State.Secrets,
            Prompts = this,
            Password = password,
            TrustedOnce = _trustedOnce,
        };
        foreach (var (k, v) in _passphrases) request.Passphrases[k] = v;

        SshLink<SshClient> link;
        try
        {
            link = await Task.Run(() => SshConnector.ConnectAsync(request, ci => new SshClient(ci), ct), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            foreach (var (k, v) in request.Passphrases) _passphrases[k] = v;
            if (gen != _generation) return;
            Fail(gen, SshConnector.Classify(ex), isReconnect, target);
            return;
        }
        foreach (var (k, v) in request.Passphrases) _passphrases[k] = v;
        if (gen != _generation || _disposed)
        {
            link.Dispose();
            return;
        }

        PtySize size;
        try { size = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20), ct); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            link.Dispose();
            if (gen == _generation) Fail(gen, new SshConnectException(SshFailure.Unknown, "terminal view"), isReconnect);
            return;
        }

        var settings = _env.ResolveSettings(Server.Server);
        var client = link.Client;
        client.KeepAliveInterval = target.KeepAliveSeconds > 0 ? TimeSpan.FromSeconds(target.KeepAliveSeconds) : Timeout.InfiniteTimeSpan;
        client.ErrorOccurred += (_, e) => _ui.BeginInvoke(() => OnConnectionLost(gen, e.Exception));

        var command = settings.InjectPrompt.Value ? PromptScript.RemoteCommand() : null;
        var environment = new Dictionary<string, string> { ["COLORTERM"] = "truecolor", ["TERM_PROGRAM"] = "RDMan", ["LC_TERMINAL"] = "RDMan" };
        TerminalChannel channel;
        try
        {
            channel = await Task.Run(() => TerminalChannel.Open(client, "xterm-256color", size, command, environment), ct);
        }
        catch (Exception ex) when (ex is SshException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            link.Dispose();
            if (gen == _generation && ex is not OperationCanceledException)
                Fail(gen, new SshConnectException(SshFailure.ShellRejected, ex.Message, ex), isReconnect);
            return;
        }
        if (gen != _generation || _disposed)
        {
            channel.Dispose();
            link.Dispose();
            return;
        }

        _link = link;
        _channel = channel;
        ResetPump();
        channel.Data += seg => OnData(gen, seg);
        channel.Closed += () => _ui.BeginInvoke(() => OnChannelClosed(gen));
        StartInputWriter(channel);

        State = TerminalState.Connected;
        _reconnectAttempt = 0;
        _passwordPromptedAfterFailure = false;
        Banner(null);
        ConnectedAs = Loc.F("Term.ConnectedAs", target.Display, request.AuthenticatedWith ?? "");
        Server.Server.LastConnected = DateTimeOffset.Now;
        _svc.State.MarkDirty();
        if (isReconnect) Line(Dim("── " + Loc.F("Term.Line.Reconnected", DateTime.Now.ToString("t", CultureInfo.CurrentCulture)) + " ──"));
        if (command is not null && !channel.RunsCommand) Line(Dim(Loc.T("Term.Line.PromptUnavailable")));

        if (settings.StartupCommand.Value is { Length: > 0 } startup)
            channel.Write(Encoding.UTF8.GetBytes(startup + "\r"), 0, Encoding.UTF8.GetByteCount(startup + "\r"));

        if (!_forwardsLoaded)
        {
            _forwards.Load(target.Forwards);
            _forwardsLoaded = true;
            OnPropertyChanged(nameof(HasForwards));
        }
        _forwards.Attach(client);

        if (settings.LogSession.Value && _logger is null)
        {
            _logger = SessionLogger.TryOpen(SessionLogger.FolderFor(_svc.State.Paths.DataDirectory, Server.Name),
                $"# RDMan session log · {Server.Name} · {target.Display} · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            OnPropertyChanged(nameof(LogPath));
        }
        if (settings.ShowStats.Value) StartStats(client, gen);
        _main.NotifyConnectionsChanged();
    }

    private void Cancelled(int gen)
    {
        if (gen != _generation) return;
        State = TerminalState.Ended;
        Line(Dim(Loc.T("Term.Line.Cancelled")));
    }

    // ───────────── Failures ─────────────

    private void Fail(int gen, SshConnectException ex, bool isReconnect, SshTarget? target = null)
    {
        if (gen != _generation || _disposed) return;
        TearDownConnection();
        if (ex.Cancelled)
        {
            Cancelled(gen);
            return;
        }

        if (ex.HostKey is { } key)
        {
            State = TerminalState.Ended;
            if (key.Status == HostKeyStatus.Changed) ShowChangedHostKey(key);
            else if (key.Status == HostKeyStatus.Revoked) ShowErrorCard(ex);
            else ShowUnknownHostKey(key);
            return;
        }

        // Password-only servers (no keyboard-interactive): ask once, then retry.
        if (ex.Failure == SshFailure.AuthFailed && !_passwordPromptedAfterFailure && target is not null
            && (ex.AllowedMethods?.Contains("password", StringComparison.OrdinalIgnoreCase) ?? false)
            && string.IsNullOrEmpty(_sessionPassword) && target.Auth.Mode != SshAuthMode.Password)
        {
            _passwordPromptedAfterFailure = true;
            Line(Warn(Loc.T("Term.Line.NeedsPassword")));
            _ = PromptPasswordThenRetry(gen, target.User);
            return;
        }

        if (isReconnect && ex.Failure is SshFailure.Timeout or SshFailure.Refused or SshFailure.Unreachable or SshFailure.ConnectionLost or SshFailure.HostNotFound or SshFailure.Unknown)
        {
            Line(Dim(Loc.F("Term.Line.AttemptFailed", Describe(ex))));
            ScheduleReconnect();
            return;
        }

        State = TerminalState.Ended;
        Line(Bad("✘ " + Describe(ex)));
        ShowErrorCard(ex);
    }

    private async Task PromptPasswordThenRetry(int gen, string? user)
    {
        var answer = await AskPasswordAsync(user, false, CancellationToken.None);
        if (gen != _generation || _disposed) return;
        if (answer is null)
        {
            Cancelled(gen);
            return;
        }
        _sessionPassword = answer.Password;
        await ConnectAsync(isReconnect: false);
    }

    private static string Describe(SshConnectException ex)
    {
        var t = SshErrorTranslator.Translate(ex.Failure);
        return ex.Hop is not null ? $"{Loc.T(t.TitleKey)} ({ex.Hop})" : Loc.T(t.TitleKey);
    }

    private void ShowErrorCard(SshConnectException ex)
    {
        var t = SshErrorTranslator.Translate(ex.Failure);
        var detail = ex.Failure switch
        {
            SshFailure.AuthFailed => Loc.F(t.DetailKey, string.IsNullOrEmpty(ex.Message) ? "—" : ex.Message),
            SshFailure.Protocol or SshFailure.Unknown => Loc.F(t.DetailKey, ex.Message),
            SshFailure.JumpFailed => Loc.F(t.DetailKey, ex.Hop ?? "", ex.InnerException?.Message ?? ex.Message),
            _ => Loc.T(t.DetailKey),
        };
        var actions = new List<object> { CardAction(Loc.T("Common.Close"), "link", _ => { }, cancel: true) };
        if (t.Action is SshErrorAction.EditCredentials or SshErrorAction.EditServer)
            actions.Add(CardAction(Loc.T("Term.Action.EditServer"), "btn", _ => _main.EditCommand.Execute(Server)));
        if (t.Action != SshErrorAction.None)
            actions.Add(CardAction(Loc.T("Desktop.Reconnect"), "primary", _ => Connect(), isDefault: true, focus: true));
        Card(new
        {
            kind = t.IsError ? "danger" : "",
            icon = t.IsError ? "" : "",
            title = Loc.T(t.TitleKey),
            body = detail,
            actions,
        });
    }

    private void ShowUnknownHostKey(PendingHostKey key)
    {
        Line(Warn(Loc.F("Term.Line.UnknownHost", key.HostLabel)));
        AttentionRequested?.Invoke(this, Loc.F("Term.Attention.HostKey", Server.Name));
        Card(new
        {
            kind = "",
            icon = "",
            title = Loc.T("Term.HostKey.Unknown.Title"),
            body = Loc.F(key.OtherTypesKnown ? "Term.HostKey.Unknown.BodyOtherTypes" : "Term.HostKey.Unknown.Body", key.HostLabel),
            rows = new[]
            {
                new[] { Loc.T("Term.HostKey.Host"), key.HostLabel },
                new[] { Loc.T("Term.HostKey.Type"), $"{Fingerprint.ShortType(key.KeyType)} {key.Bits}" },
                new[] { Loc.T("Term.HostKey.Fingerprint"), key.Fingerprint },
            },
            pre = key.RandomArt,
            preKind = "art",
            actions = new object[]
            {
                CardAction(Loc.T("Common.Cancel"), "link", _ => Cancelled(_generation), cancel: true),
                CardAction(Loc.T("Term.HostKey.Once"), "btn", _ =>
                {
                    _trustedOnce.Add($"{key.HostLabel}|{key.Fingerprint}");
                    Connect();
                }),
                CardAction(Loc.T("Term.HostKey.Trust"), "primary", _ =>
                {
                    if (TrySaveHostKey(key, replace: false)) Connect();
                }, isDefault: true, focus: true),
            },
        });
    }

    private void ShowChangedHostKey(PendingHostKey key)
    {
        Line(Bad("✘ " + Loc.F("Term.Line.ChangedHost", key.HostLabel)));
        AttentionRequested?.Invoke(this, Loc.F("Term.Attention.HostKey", Server.Name));
        Card(new
        {
            kind = "danger",
            icon = "",
            title = Loc.T("SshErr.HostKeyChanged.Title"),
            body = Loc.T("SshErr.HostKeyChanged.Detail"),
            rows = new[]
            {
                new[] { Loc.T("Term.HostKey.Host"), key.HostLabel },
                new[] { Loc.T("Term.HostKey.NewKey"), $"{Fingerprint.ShortType(key.KeyType)} {key.Fingerprint}" },
                new[] { Loc.T("Term.HostKey.File"), key.KnownHostsFiles.FirstOrDefault() ?? "" },
            },
            actions = new object[]
            {
                CardAction(Loc.T("Term.HostKey.Replace"), "danger", _ => ConfirmReplaceHostKey(key)),
                CardAction(Loc.T("Common.Close"), "btn", _ => Cancelled(_generation), cancel: true, isDefault: true, focus: true),
            },
        });
    }

    private void ConfirmReplaceHostKey(PendingHostKey key)
    {
        Card(new
        {
            kind = "danger",
            icon = "",
            title = Loc.T("Term.HostKey.ReplaceConfirm.Title"),
            body = Loc.F("Term.HostKey.ReplaceConfirm.Body", key.HostLabel),
            actions = new object[]
            {
                CardAction(Loc.T("Common.Cancel"), "btn", _ => Cancelled(_generation), cancel: true, isDefault: true, focus: true),
                CardAction(Loc.T("Term.HostKey.ReplaceConfirm.Do"), "danger", _ =>
                {
                    if (TrySaveHostKey(key, replace: true)) Connect();
                }),
            },
        });
    }

    private bool TrySaveHostKey(PendingHostKey key, bool replace)
    {
        try
        {
            SshConnector.Trust(key, replace);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log(ex);
            Line(Bad("✘ " + Loc.F("Term.Line.KnownHostsWriteFailed", ex.Message)));
            // Still let the user in for this session.
            _trustedOnce.Add($"{key.HostLabel}|{key.Fingerprint}");
            return true;
        }
    }

    private void ShowFatal(string title, string detail, SshErrorAction action)
    {
        Error = new ErrorInfo
        {
            Title = title,
            Detail = detail,
            CodeText = "",
            Action = action == SshErrorAction.EditServer ? Core.Smb.ErrorAction.CheckHost : Core.Smb.ErrorAction.Retry,
            PrimaryText = action switch
            {
                SshErrorAction.InstallWebView => Loc.T("Term.Action.InstallWebView"),
                SshErrorAction.EditServer => Loc.T("Term.Action.EditServer"),
                _ => Loc.T("Desktop.Reconnect"),
            },
        };
        _fatalAction = action;
        State = TerminalState.Idle;
    }

    private SshErrorAction _fatalAction;

    [RelayCommand]
    private void ErrorPrimary()
    {
        switch (_fatalAction)
        {
            case SshErrorAction.InstallWebView:
                ShellLauncher.OpenUrl("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                break;
            case SshErrorAction.EditServer:
                _main.EditCommand.Execute(Server);
                break;
            default:
                Error = null;
                Connect();
                break;
        }
    }

    private void OnHostFailed(Exception ex)
    {
        if (_disposed) return;
        App.Log(ex);
        var hadSession = IsConnected;
        DisposeHost();
        TearDownConnection();
        State = TerminalState.Idle;
        var t = SshErrorTranslator.Translate(TerminalHost.RuntimeVersion() is null ? SshFailure.WebViewMissing : SshFailure.Unknown);
        ShowFatal(Loc.T(t.TitleKey), TerminalHost.RuntimeVersion() is null ? Loc.T(t.DetailKey) : Loc.F(t.DetailKey, ex.Message),
            TerminalHost.RuntimeVersion() is null ? SshErrorAction.InstallWebView : SshErrorAction.Reconnect);
        if (hadSession) _main.NotifyConnectionsChanged();
    }

    // ───────────── Connection lifetime ─────────────

    private void OnChannelClosed(int gen)
    {
        if (gen != _generation || _disposed) return;
        var stillConnected = _link?.Client.IsConnected == true;
        TearDownConnection();
        if (stillConnected)
        {
            // exit / logout: the server ended the session on purpose.
            State = TerminalState.Ended;
            Line("");
            Line(Dim("── " + Loc.T("Term.Line.Ended") + " ──"));
            _main.NotifyConnectionsChanged();
            return;
        }
        LostConnection(Loc.T("SshErr.ConnectionLost.Title"));
    }

    private void OnConnectionLost(int gen, Exception ex)
    {
        if (gen != _generation || _disposed || State != TerminalState.Connected) return;
        TearDownConnection();
        LostConnection(ex.Message);
    }

    private void LostConnection(string reason)
    {
        Line("");
        Line(Bad("✘ " + Loc.F("Term.Line.Lost", reason)));
        _main.NotifyConnectionsChanged();
        _reconnectAttempt = 0;
        ScheduleReconnect();
    }

    private void ScheduleReconnect()
    {
        if (_disposed) return;
        if (_reconnectAttempt >= Backoff.Length)
        {
            State = TerminalState.Ended;
            Banner(new
            {
                kind = "danger",
                icon = "",
                text = Loc.T("Term.Banner.GaveUp"),
                actions = new[] { BannerAction(Loc.T("Desktop.Reconnect"), "primary", () => Connect()) },
            });
            return;
        }
        State = TerminalState.Reconnecting;
        _reconnectCountdown = Backoff[_reconnectAttempt];
        _reconnectAttempt++;
        UpdateReconnectBanner();
        _reconnectTimer.Start();
    }

    private void UpdateReconnectBanner()
    {
        Banner(new
        {
            kind = "warn",
            icon = "",
            spin = false,
            text = Loc.F("Term.Banner.Reconnecting", _reconnectCountdown, _reconnectAttempt, Backoff.Length),
            actions = new[]
            {
                BannerAction(Loc.T("Term.Banner.Now"), "primary", () => { StopReconnect(); _ = ConnectAsync(isReconnect: true); }),
                BannerAction(Loc.T("Common.Cancel"), "link", () => { StopReconnect(); State = TerminalState.Ended; Banner(null); Line(Dim(Loc.T("Term.Line.Ended"))); }),
            },
        });
    }

    private void ReconnectTick(object? sender, EventArgs e)
    {
        if (_disposed) { _reconnectTimer.Stop(); return; }
        _reconnectCountdown--;
        if (_reconnectCountdown > 0)
        {
            UpdateReconnectBanner();
            return;
        }
        _reconnectTimer.Stop();
        Banner(new { kind = "info", icon = "", spin = true, text = Loc.F("Term.Banner.Trying", _reconnectAttempt, Backoff.Length) });
        _ = ConnectAsync(isReconnect: true);
    }

    private void StopReconnect()
    {
        _reconnectTimer.Stop();
    }

    [RelayCommand]
    public override void Disconnect()
    {
        if (_link is null && !IsBusy) return;
        _generation++;
        _connectCts?.Cancel();
        StopReconnect();
        TearDownConnection();
        Banner(null);
        State = TerminalState.Ended;
        Line("");
        Line(Dim("── " + Loc.T("Term.Line.Disconnected") + " ──"));
        _main.NotifyConnectionsChanged();
    }

    [RelayCommand]
    private void Reconnect()
    {
        if (IsConnected && !Dialogs.Confirm(DialogOwner, Loc.T("Desktop.Reconnect"), Loc.F("Term.ConfirmReconnect", Server.Name), Loc.T("Desktop.Reconnect"), danger: false))
            return;
        _generation++;
        TearDownConnection();
        State = TerminalState.Ended;
        _passwordPromptedAfterFailure = false;
        Connect();
    }

    [RelayCommand]
    private void FullScreen()
    {
        if (IsSurfaceVisible) RaiseFullScreenRequested(true);
    }

    private void TearDownConnection()
    {
        _thumbTimer.Stop();
        _stats?.Dispose();
        _stats = null;
        HasStats = false;
        _forwards.Detach();
        _input?.Writer.TryComplete();
        _input = null;
        var channel = _channel;
        var link = _link;
        _channel = null;
        _link = null;
        _canRead.Set(); // never leave SSH.NET's thread waiting on a page that's gone
        if (channel is null && link is null) return;
        _ = Task.Run(() =>
        {
            try { channel?.Dispose(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException) { }
            link?.Dispose();
        });
    }

    // ───────────── Output pump ─────────────

    private void ResetPump()
    {
        lock (_outGate)
        {
            _decoder = new UTF8Encoding(false).GetDecoder();
            _pending.Clear();
            _flushQueued = false;
        }
        Interlocked.Exchange(ref _unacked, 0);
        _canRead.Set();
    }

    /// <summary>SSH.NET's thread. Decodes, queues for the page, and waits when the page is far behind.</summary>
    private void OnData(int gen, ArraySegment<byte> data)
    {
        if (gen != _generation || data.Count == 0 || data.Array is null) return;
        int pending;
        lock (_outGate)
        {
            var max = _decoder.GetCharCount(data.Array, data.Offset, data.Count, flush: false);
            if (_chars.Length < max) _chars = new char[Math.Max(max, _chars.Length * 2)];
            var n = _decoder.GetChars(data.Array, data.Offset, data.Count, _chars, 0, flush: false);
            _pending.Append(_chars, 0, n);
            _logger?.Append(_chars.AsSpan(0, n));
            pending = _pending.Length;
            if (!_flushQueued)
            {
                _flushQueued = true;
                _ui.BeginInvoke(Flush, DispatcherPriority.Background);
            }
        }
        if (Interlocked.Read(ref _unacked) + pending > HighWater)
        {
            _canRead.Reset();
            // Backpressure: holding this thread stops reading from the socket, so the server slows down.
            while (!_disposed && gen == _generation && Interlocked.Read(ref _unacked) > LowWater && !_canRead.Wait(250)) { }
        }
    }

    private void Flush()
    {
        string text;
        lock (_outGate)
        {
            text = _pending.ToString();
            _pending.Clear();
            _flushQueued = false;
        }
        if (text.Length == 0 || _terminal is null) return;
        Interlocked.Add(ref _unacked, text.Length);
        _terminal.Write(text);
    }

    private void OnAck(long n)
    {
        if (Interlocked.Add(ref _unacked, -n) < LowWater) _canRead.Set();
    }

    // ───────────── Input ─────────────

    private void StartInputWriter(TerminalChannel channel)
    {
        var queue = System.Threading.Channels.Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });
        _input = queue;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var bytes in queue.Reader.ReadAllAsync())
                {
                    // Large pastes go out in pieces so a full remote window never stalls a huge single write.
                    for (var offset = 0; offset < bytes.Length; offset += 16 * 1024)
                        channel.Write(bytes, offset, Math.Min(16 * 1024, bytes.Length - offset));
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SshException or InvalidOperationException) { }
        });
    }

    /// <summary>Text for the shell, as if typed (snippets, broadcast from another terminal).</summary>
    public void SendInput(string text)
    {
        if (!IsConnected || text.Length == 0) return;
        _input?.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Pastes like Ctrl+Shift+V (bracketed paste, multi-line warning).</summary>
    [RelayCommand]
    public void Paste()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (System.Runtime.InteropServices.COMException) { return; }
        PasteText(text);
    }

    public void PasteText(string text)
    {
        if (_terminal is null || text.Length == 0) return;
        text = text.Replace("\r\n", "\n");
        var lines = text.TrimEnd('\n').Split('\n').Length;
        var t = _svc.State.Settings.Terminal;
        if (t.WarnMultilinePaste && (lines > 1 || text.Length > 5000))
        {
            var preview = string.Join('\n', text.Split('\n').Take(12));
            if (lines > 12) preview += "\n…";
            Card(new
            {
                kind = "warn",
                icon = "",
                title = Loc.T("Term.Paste.Title"),
                body = Loc.F("Term.Paste.Body", lines, text.Length),
                pre = preview,
                actions = new object[]
                {
                    CardAction(Loc.T("Common.Cancel"), "link", _ => Focus(), cancel: true, focus: true),
                    CardAction(Loc.F("Term.Paste.Do", lines), "primary", _ => _terminal?.Send(new { t = "paste", text }), isDefault: true),
                },
            });
            return;
        }
        _terminal.Send(new { t = "paste", text });
    }

    // ───────────── Page messages ─────────────

    private void OnHostMessage(string type, JsonElement m)
    {
        if (_disposed) return;
        switch (type)
        {
            case "ready":
                _size = new PtySize(UInt(m, "cols", 120), UInt(m, "rows", 32), 0, 0);
                _ready.TrySetResult(_size);
                break;
            case "resize":
                var size = new PtySize(UInt(m, "cols", 120), UInt(m, "rows", 32), UInt(m, "w", 0), UInt(m, "h", 0));
                _size = size;
                _ready.TrySetResult(size);
                if (_channel is { } ch) _ = Task.Run(() => ch.Resize(size));
                break;
            case "input":
                var d = Str(m, "d");
                if (d.Length == 0) break;
                if (IsConnected)
                {
                    _input?.Writer.TryWrite(Encoding.UTF8.GetBytes(d));
                    InputTyped?.Invoke(this, d);
                }
                else if (State == TerminalState.Ended && d.Contains('\r'))
                {
                    Connect();
                }
                break;
            case "bin":
                if (IsConnected) _input?.Writer.TryWrite(Encoding.Latin1.GetBytes(Str(m, "d")));
                break;
            case "ack":
                if (m.TryGetProperty("n", out var n) && n.TryGetInt64(out var count)) OnAck(count);
                break;
            case "title":
                RemoteTitle = Str(m, "v") is { Length: > 0 } title ? title : null;
                break;
            case "cwd":
                CurrentDirectory = Str(m, "v");
                break;
            case "bell":
                Bell?.Invoke(this, EventArgs.Empty);
                break;
            case "link":
                ShellLauncher.OpenUrl(Str(m, "url"));
                break;
            case "copy":
                SetClipboard(Str(m, "text"));
                break;
            case "paste-request":
                Paste();
                break;
            case "osc52":
                OnClipboardRequest(Str(m, "text"));
                break;
            case "progress":
                var state = m.TryGetProperty("state", out var s) && s.TryGetInt32(out var si) ? si : 0;
                var value = m.TryGetProperty("value", out var v) && v.TryGetInt32(out var vi) ? vi : 0;
                ProgressValue = state switch { 0 => null, 3 => -1, _ => Math.Clamp(value, 0, 100) };
                ProgressError = state == 2;
                break;
            case "activate":
                RaiseActivated();
                break;
            case "action":
                var id = Str(m, "id");
                if (_actions.Remove(id, out var handler))
                {
                    // A card closes as a whole: drop the other buttons' handlers too.
                    var group = id.Split(':')[0];
                    foreach (var k in _actions.Keys.Where(k => k.StartsWith(group + ":", StringComparison.Ordinal)).ToList()) _actions.Remove(k);
                    handler(m);
                }
                break;
            case "serialized":
                if (_serialized.Remove(Str(m, "id"), out var tcs)) tcs.TrySetResult(Str(m, "data"));
                break;
            case "save-request":
                _ = SaveOutputAsync();
                break;
            case "webgl-lost":
                App.Log(new InvalidOperationException("Terminal WebGL context lost; using the DOM renderer."));
                break;
        }
    }

    private static uint UInt(JsonElement m, string name, uint fallback)
        => m.TryGetProperty(name, out var p) && p.TryGetUInt32(out var v) ? v : fallback;

    private static string Str(JsonElement m, string name)
        => m.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static void SetClipboard(string text)
    {
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException ex) { App.Log(ex); }
    }

    private void OnClipboardRequest(string text)
    {
        if (text.Length == 0) return;
        var settings = _env.ResolveSettings(Server.Server);
        if (settings.AllowClipboardWrite.Value)
        {
            SetClipboard(text);
            return;
        }
        Banner(new
        {
            kind = "info",
            icon = "",
            text = Loc.T("Term.Banner.Clipboard"),
            actions = new[]
            {
                BannerAction(Loc.T("Term.Banner.AllowOnce"), "primary", () => { SetClipboard(text); Banner(null); }),
                BannerAction(Loc.T("Term.Banner.AllowAlways"), "link", () =>
                {
                    Server.Server.Ssh.AllowClipboardWrite = true;
                    _svc.State.MarkDirty();
                    SetClipboard(text);
                    Banner(null);
                }),
                BannerAction(Loc.T("Term.Banner.Ignore"), "link", () => Banner(null)),
            },
        });
    }

    private void OnHostFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) RaiseActivated();
    }

    // ───────────── Cards, banners, lines ─────────────

    private object CardAction(string label, string style, Action<JsonElement> run, bool cancel = false, bool isDefault = false, bool focus = false)
    {
        var id = $"c{_actionSeq}:{Guid.NewGuid():N}";
        _actions[id] = run;
        return new { id, label, style, cancel, @default = isDefault, focus };
    }

    private object BannerAction(string label, string style, Action run)
    {
        var id = $"b{_actionSeq}:{Guid.NewGuid():N}";
        _actions[id] = _ => run();
        return new { id, label, style };
    }

    private void Card(object? card)
    {
        _actionSeq++;
        foreach (var k in _actions.Keys.Where(k => k.StartsWith('c')).ToList()) _actions.Remove(k);
        _terminal?.Send(new { t = "card", card });
    }

    private void Banner(object? banner)
    {
        foreach (var k in _actions.Keys.Where(k => k.StartsWith('b')).ToList()) _actions.Remove(k);
        _actionSeq++;
        _terminal?.Send(new { t = "banner", banner });
    }

    private void Line(string text) => _terminal?.Write(text + "\r\n");

    private static string Dim(string s) => $"\x1b[2m{s}\x1b[0m";
    private static string Warn(string s) => $"\x1b[33m{s}\x1b[0m";
    private static string Bad(string s) => $"\x1b[31m{s}\x1b[0m";
    private static string Good(string s) => $"\x1b[32m{s}\x1b[0m";

    // ───────────── ISshPrompts (called from background threads) ─────────────

    public Task<string?> AskPassphraseAsync(string keyName, CancellationToken ct)
        => AskAsync<string>(ct, (done, _) => Card(new
        {
            kind = "",
            icon = "",
            title = Loc.F("Term.Prompt.Passphrase.Title", keyName),
            body = Loc.T("Term.Prompt.Passphrase.Body"),
            fields = new[] { new { id = "p0", label = Loc.T("Term.Prompt.Passphrase.Label"), type = "password", value = "" } },
            actions = new object[]
            {
                CardAction(Loc.T("Common.Cancel"), "link", _ => done(null), cancel: true),
                CardAction(Loc.T("Term.Prompt.Unlock"), "primary", m => done(Field(m, "p0")), isDefault: true),
            },
        }), Loc.F("Term.Attention.Passphrase", Server.Name));

    public async Task<SshPasswordAnswer?> AskPasswordAsync(string? user, bool userEditable, CancellationToken ct)
    {
        SshPasswordAnswer? answer = null;
        await AskAsync<string>(ct, (done, _) => Card(new
        {
            kind = "",
            icon = "",
            title = Loc.F("Term.Prompt.Password.Title", Server.Name),
            body = userEditable ? Loc.T("Term.Prompt.Password.BodyUser") : Loc.F("Term.Prompt.Password.Body", user ?? ""),
            fields = userEditable
                ? new object[]
                {
                    new { id = "u", label = Loc.T("Term.Prompt.User"), type = "text", value = user ?? "" },
                    new { id = "p", label = Loc.T("Term.Prompt.Password"), type = "password", value = "" },
                }
                : new object[] { new { id = "p", label = Loc.T("Term.Prompt.Password"), type = "password", value = "" } },
            check = new { id = "r", label = Loc.T("Term.Prompt.Remember"), @checked = false },
            actions = new object[]
            {
                CardAction(Loc.T("Common.Cancel"), "link", _ => done(null), cancel: true),
                CardAction(Loc.T("Term.Prompt.SignIn"), "primary", m =>
                {
                    var u = userEditable ? Field(m, "u").Trim() : user ?? "";
                    if (u.Length == 0) { done(null); return; }
                    answer = new SshPasswordAnswer(u, Field(m, "p"), Field(m, "r") == "true");
                    done("ok");
                }, isDefault: true),
            },
        }), Loc.F("Term.Attention.Password", Server.Name));
        if (answer is { Remember: true }) _ui.Invoke(() => Remember(answer));
        if (answer is not null) _sessionUser = answer.User;
        return answer;
    }

    private void Remember(SshPasswordAnswer answer)
    {
        var auth = Server.Server.SshAuth;
        auth.Username = answer.User;
        if (answer.Password.Length > 0)
        {
            auth.Mode = SshAuthMode.Password;
            auth.ProfileId = null;
            auth.ProtectedPassword = _svc.State.Secrets.Protect(answer.Password);
        }
        _svc.State.MarkDirty();
    }

    public async Task<string[]?> AskKeyboardInteractiveAsync(string? name, string? instruction, IReadOnlyList<SshPromptField> fields, CancellationToken ct)
    {
        string[]? result = null;
        await AskAsync<string>(ct, (done, _) => Card(new
        {
            kind = "",
            icon = "",
            title = string.IsNullOrWhiteSpace(name) ? Loc.F("Term.Prompt.Server.Title", Server.Name) : name,
            body = string.IsNullOrWhiteSpace(instruction) ? Loc.T("Term.Prompt.Server.Body") : instruction,
            fields = fields.Select((f, i) => new { id = $"k{i}", label = f.Text.TrimEnd(':', ' '), type = f.Echo ? "text" : "password", value = "" }).ToArray(),
            actions = new object[]
            {
                CardAction(Loc.T("Common.Cancel"), "link", _ => done(null), cancel: true),
                CardAction(Loc.T("Term.Prompt.Continue"), "primary", m =>
                {
                    result = fields.Select((_, i) => Field(m, $"k{i}")).ToArray();
                    done("ok");
                }, isDefault: true),
            },
        }), Loc.F("Term.Attention.Password", Server.Name));
        return result;
    }

    /// <summary>Shows a card on the UI thread and completes when one of its actions runs (or on cancellation).</summary>
    private async Task<T?> AskAsync<T>(CancellationToken ct, Action<Action<T?>, CancellationToken> show, string attention) where T : class
    {
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _ui.InvokeAsync(() =>
        {
            if (_terminal is null || _disposed) { tcs.TrySetResult(null); return; }
            show(r => tcs.TrySetResult(r), ct);
            AttentionRequested?.Invoke(this, attention);
        });
        await using var reg = ct.Register(() =>
        {
            tcs.TrySetResult(null);
            _ui.BeginInvoke(() => Card(null));
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    private static string Field(JsonElement m, string id)
        => m.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Object && values.TryGetProperty(id, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.True ? "true" : ""
            : "";

    public void Progress(SshProgress step, string detail)
    {
        var text = step switch
        {
            SshProgress.Jumping => Dim("  ↪ " + Loc.F("Term.Line.Jump", detail)),
            SshProgress.HostKeyTrusted => Dim("  " + Good("✓") + Dim(" " + Loc.F("Term.Line.HostKey", detail))),
            SshProgress.Authenticated => Dim("  " + Good("✓") + Dim(" " + Loc.F("Term.Line.SignedIn", detail))),
            _ => null,
        };
        if (text is not null) _ui.BeginInvoke(() => Line(text));
    }

    // ───────────── Stats ─────────────

    private void StartStats(SshClient client, int gen)
    {
        var probe = new StatsProbe(client);
        probe.OperatingSystem += os => _ui.BeginInvoke(() => { if (gen == _generation) OsName = os; });
        probe.Sample += (sample, cpu) => _ui.BeginInvoke(() =>
        {
            if (gen != _generation) return;
            HasStats = true;
            if (cpu is { } c)
            {
                CpuPercent = c;
                CpuHistory.Add(c);
                while (CpuHistory.Count > 20) CpuHistory.RemoveAt(0);
                CpuSparkline = Sparkline(CpuHistory);
                CpuLevel = Level(c);
            }
            MemPercent = sample.MemUsedPercent;
            DiskPercent = sample.DiskUsedPercent;
            MemLevel = Level(MemPercent);
            DiskLevel = Level(DiskPercent);
            LoadText = sample.Load1.ToString("0.00", CultureInfo.InvariantCulture);
            UptimeText = ServerStats.FormatUptime(sample.UptimeSeconds);
            MemText = $"{FormatKb(sample.MemTotalKb - sample.MemAvailableKb)} / {FormatKb(sample.MemTotalKb)}";
            DiskText = $"{FormatKb(sample.DiskUsedKb)} / {FormatKb(sample.DiskTotalKb)}";
        });
        probe.Unavailable += () => _ui.BeginInvoke(() => { if (gen == _generation) HasStats = false; });
        _stats = probe;
        CpuHistory.Clear();
        probe.Start();
    }

    /// <summary>"ok" / "warn" (85 %+) / "danger" (95 %+): the stats chips color their value with it.</summary>
    private static string Level(double percent) => percent >= 95 ? "danger" : percent >= 85 ? "warn" : "ok";

    /// <summary>The last minute of CPU as a 40×14 line (0 % at the bottom).</summary>
    private static PointCollection Sparkline(IReadOnlyList<double> values)
    {
        const double w = 40, h = 14;
        var points = new PointCollection(values.Count);
        if (values.Count == 1) points.Add(new Point(w, h - values[0] / 100 * h));
        for (var i = 0; i < values.Count && values.Count > 1; i++)
            points.Add(new Point(w * i / (values.Count - 1), h - Math.Clamp(values[i], 0, 100) / 100 * (h - 1) - 0.5));
        points.Freeze();
        return points;
    }

    private static string FormatKb(long kb)
    {
        double v = kb;
        string[] units = ["KB", "MB", "GB", "TB"];
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return v.ToString(v >= 100 || u == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + units[u];
    }

    // ───────────── Commands for toolbar and menus ─────────────

    [RelayCommand] private void Search() => _terminal?.Send(new { t = "search" });
    [RelayCommand] private void ClearScreen() => _terminal?.Send(new { t = "clear" });
    [RelayCommand] private void ZoomIn() => _terminal?.Send(new { t = "zoom", delta = 1 });
    [RelayCommand] private void ZoomOut() => _terminal?.Send(new { t = "zoom", delta = -1 });
    [RelayCommand] private void ZoomReset() => _terminal?.Send(new { t = "zoom", reset = true });

    [RelayCommand]
    private void OpenLogFolder()
    {
        ShellLauncher.OpenFolder(_logger is not null ? Path.GetDirectoryName(_logger.Path)! : SessionLogger.FolderFor(_svc.State.Paths.DataDirectory, Server.Name));
    }

    [RelayCommand]
    private async Task CopyLastOutput()
    {
        var text = await RequestSerializedAsync("last-output", null);
        if (!string.IsNullOrEmpty(text))
        {
            SetClipboard(text);
            _svc.Toasts.Show(Loc.T("Term.Toast.CopiedOutput"), ToastKind.Success);
        }
        else
        {
            _svc.Toasts.Show(Loc.T("Term.Toast.NoOutput"));
        }
    }

    [RelayCommand]
    private async Task SaveOutputAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{Server.Name} {DateTime.Now:yyyy-MM-dd HHmm}",
            Filter = Loc.T("Term.Save.Filter"),
            DefaultExt = ".txt",
        };
        if (dlg.ShowDialog(DialogOwner) != true) return;
        var html = dlg.FilterIndex == 2;
        var text = await RequestSerializedAsync("serialize", html ? "html" : "text");
        if (text is null) return;
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, text, new UTF8Encoding(false));
            _svc.Toasts.Show(Loc.F("Term.Toast.Saved", Path.GetFileName(dlg.FileName)), ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _svc.Toasts.Show(ex.Message, ToastKind.Error);
        }
    }

    private async Task<string?> RequestSerializedAsync(string kind, string? format)
    {
        if (_terminal is null) return null;
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _serialized[id] = tcs;
        _terminal.Send(new { t = kind, id, format });
        try { return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { _serialized.Remove(id); return null; }
    }

    public override void Focus()
    {
        if (_terminal is null) return;
        _terminal.Focus();
    }

    /// <summary>Keyboard released back to RDMan (Ctrl+Alt+←/→ inside the terminal).</summary>
    public void ReleaseFocus() => RaiseFocusReleased();

    private async Task CaptureThumbnailAsync()
    {
        if (_terminal is null || !IsConnected || Host is not { IsVisible: true }) return;
        var png = await _terminal.CaptureAsync();
        if (png is null || _disposed) return;
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 480;
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();
            Server.Thumbnail = image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or FileFormatException) { }
    }

    // ───────────── Lifetime ─────────────

    private void DisposeHost()
    {
        var host = _terminal;
        _terminal = null;
        Host = null;
        if (host is null) return;
        host.Message -= OnHostMessage;
        host.View.IsKeyboardFocusWithinChanged -= OnHostFocusWithinChanged;
        // After the overlay dropped it, so WPF never renders a dead HWND.
        _ui.BeginInvoke(host.Dispose, DispatcherPriority.Background);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        _connectCts?.Cancel();
        StopReconnect();
        _thumbTimer.Stop();
        ThemeManager.Instance.ThemeChanged -= OnThemeChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        TearDownConnection();
        _forwards.Dispose();
        _logger?.Dispose();
        _logger = null;
        foreach (var tcs in _serialized.Values) tcs.TrySetResult("");
        _serialized.Clear();
        DisposeHost();
        _canRead.Set();
    }
}
