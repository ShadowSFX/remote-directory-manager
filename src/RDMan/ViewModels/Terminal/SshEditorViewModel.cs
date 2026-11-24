using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDMan.Core.Models;
using RDMan.Core.Ssh;
using RDMan.Infrastructure;
using RDMan.Services.Ssh;
using RDMan.Views;

namespace RDMan.ViewModels.Terminal;

/// <summary>One choice of a terminal option ("inherit", yes/no, a scheme…).</summary>
public sealed record SshChoice(object? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A terminal option with "inherit (value, from where)" or an explicit choice — like the RDP option rows.</summary>
public sealed partial class SshOptionRowViewModel : ObservableObject
{
    private readonly Func<EffectiveSshSettings, (string Value, TreeNode? Source)> _inherited;
    private readonly Action<SshProfile, object?> _write;

    public SshOptionRowViewModel(string labelKey, object? current, IReadOnlyList<SshChoice> choices,
        Func<EffectiveSshSettings, (string, TreeNode?)> inherited, Action<SshProfile, object?> write)
    {
        LabelKey = labelKey;
        _inherited = inherited;
        _write = write;
        Choices.Add(new SshChoice(null, ""));
        foreach (var c in choices) Choices.Add(c);
        _selected = Choices.FirstOrDefault(c => Equals(c.Value, current)) ?? Choices[0];
    }

    public string LabelKey { get; }
    public string Label => Loc.T(LabelKey);
    public ObservableCollection<SshChoice> Choices { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverridden))]
    private SshChoice _selected;

    public bool IsOverridden => Selected.Value is not null;

    public void RefreshInherited(EffectiveSshSettings parent)
    {
        var (value, source) = _inherited(parent);
        var text = source is null ? Loc.F("Rdp.Default", value) : Loc.F("Rdp.InheritedFrom", value, source.Name);
        var wasSelected = Selected.Value is null;
        Choices[0] = new SshChoice(null, Loc.F("Rdp.InheritChoice", text));
        if (wasSelected) Selected = Choices[0];
    }

    public void ApplyTo(SshProfile profile) => _write(profile, Selected.Value);
}

/// <summary>One port forward being edited ("5432 → db:5432").</summary>
public sealed partial class ForwardRowViewModel : ObservableObject
{
    public ForwardRowViewModel(SshForward? f = null)
    {
        _kind = f?.Kind ?? SshForwardKind.Local;
        _bindPort = f is null ? "" : f.BindPort.ToString(CultureInfo.InvariantCulture);
        _bindAddress = f?.BindAddress ?? "";
        _target = f is null || f.Kind == SshForwardKind.Dynamic ? "" : $"{f.TargetHost}:{f.TargetPort.ToString(CultureInfo.InvariantCulture)}";
        _autoStart = f?.AutoStart ?? true;
        _label = f?.Label ?? "";
    }

    public static IReadOnlyList<SshChoice> Kinds { get; } =
    [
        new(SshForwardKind.Local, Loc.T("Term.Editor.Forward.Local")),
        new(SshForwardKind.Remote, Loc.T("Term.Editor.Forward.Remote")),
        new(SshForwardKind.Dynamic, Loc.T("Term.Editor.Forward.Dynamic")),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsTarget), nameof(SelectedKind))]
    private SshForwardKind _kind;

    public SshChoice SelectedKind
    {
        get => Kinds.First(k => Equals(k.Value, Kind));
        set { if (value?.Value is SshForwardKind k) Kind = k; }
    }

    [ObservableProperty] private string _bindPort;
    [ObservableProperty] private string _bindAddress;
    [ObservableProperty] private string _target;
    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private string _label;

    public bool NeedsTarget => Kind != SshForwardKind.Dynamic;

    /// <summary>Null when valid; otherwise what's wrong.</summary>
    public SshForward? ToModel(out string? error)
    {
        error = null;
        var spec = (string.IsNullOrWhiteSpace(BindAddress) ? "" : BindAddress.Trim() + ":") + BindPort.Trim();
        if (NeedsTarget) spec += " " + Target.Trim();
        if (!SshTargetResolver.TryParseForward(Kind, spec, out var f))
        {
            error = Loc.T("Term.Editor.Forward.Invalid");
            return null;
        }
        f.AutoStart = AutoStart;
        f.Label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim();
        return f;
    }
}

/// <summary>A vault key in the key picker (or the "none" placeholder).</summary>
public sealed record KeyChoice(SshKeyEntry? Key, string Label, string Detail)
{
    public override string ToString() => Label;
}

/// <summary>
/// The SSH part of the server/group editor: capability, port and ~/.ssh/config use (servers), authentication and
/// terminal options (inherited by groups' children), port forwards (servers).
/// </summary>
public sealed partial class SshEditorViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly TreeNode _node;
    private readonly Func<GroupNode?> _parent;
    private readonly bool _isServer;

    public SshEditorViewModel(MainViewModel main, TreeNode node, Func<GroupNode?> parent)
    {
        _main = main;
        _node = node;
        _parent = parent;
        _isServer = node is ServerNode;

        var server = node as ServerNode;
        _enabled = server?.HasTerminal ?? false;
        _port = server?.Terminal?.Port?.ToString(CultureInfo.InvariantCulture) ?? "";
        _useSshConfig = server?.Terminal?.UseSshConfig ?? true;
        foreach (var f in server?.Terminal?.Forwards ?? []) Forwards.Add(new ForwardRowViewModel(f));

        var auth = node.SshAuth ?? new SshAuthSettings();
        _authMode = auth.Mode;
        _username = auth.Username ?? "";
        _hasStoredPassword = !string.IsNullOrEmpty(auth.ProtectedPassword);
        RebuildKeys(auth.KeyId);

        var p = node.Ssh ?? new SshProfile();
        _proxyJump = p.ProxyJump ?? "";
        _startupCommand = p.StartupCommand ?? "";
        BuildOptions(p);
        RefreshInherited();
    }

    public bool IsServer => _isServer;

    // ───────────── Capability (servers) ─────────────

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _port;
    [ObservableProperty] private bool _useSshConfig;
    [ObservableProperty] private string? _configPreview;
    [ObservableProperty] private bool _configMatched;

    /// <summary>Set by the server editor whenever the address changes (the preview resolves it like ssh would).</summary>
    public string Address { get; set; } = "";

    partial void OnUseSshConfigChanged(bool value) => UpdatePreview();
    partial void OnPortChanged(string value) => UpdatePreview();
    partial void OnUsernameChanged(string value) => UpdatePreview();

    public void UpdatePreview()
    {
        if (!_isServer || string.IsNullOrWhiteSpace(Address))
        {
            ConfigPreview = null;
            ConfigMatched = false;
            return;
        }
        var host = Address.Trim();
        var cfg = UseSshConfig ? SshEnvironment.SharedConfig.Resolve(host) : null;
        var user = !string.IsNullOrWhiteSpace(Username) ? Username.Trim() : cfg?.User ?? InheritedUser();
        var port = TryPort(out var p) && p > 0 ? p : cfg?.Port ?? SshEndpoint.DefaultPort;
        var target = new SshDestination(string.IsNullOrEmpty(user) ? null : user, cfg?.EffectiveHost ?? host, port == 22 ? null : port).ToString();
        ConfigMatched = cfg is { MatchedSpecificHost: true };
        // The user field's placeholder says what will be used when it's left empty.
        if (string.IsNullOrWhiteSpace(Username) && cfg?.User is { Length: > 0 } cfgUser && InheritedUser() is null)
            UsernameHint = Loc.F("Term.Editor.User.FromConfig", cfgUser);
        if (ConfigMatched)
        {
            var key = cfg!.IdentityFiles.Count > 0 ? Path.GetFileName(cfg.IdentityFiles[0]) : null;
            var jump = cfg.ProxyJump is { } j && !j.Equals("none", StringComparison.OrdinalIgnoreCase) ? j : null;
            var extras = new List<string>();
            if (key is not null) extras.Add(Loc.F("Term.Editor.Preview.Key", key));
            if (jump is not null) extras.Add(Loc.F("Term.Editor.Preview.Jump", jump));
            ConfigPreview = Loc.F("Term.Editor.Preview.Config", host, target) + (extras.Count > 0 ? " · " + string.Join(" · ", extras) : "");
        }
        else
        {
            ConfigPreview = Loc.F("Term.Editor.Preview.Plain", target);
        }
    }

    private string? InheritedUser()
    {
        for (var g = _parent(); g is not null; g = _main.State.Index.ParentOf(g.Id) as GroupNode)
            if (!string.IsNullOrWhiteSpace(g.SshAuth?.Username)) return g.SshAuth.Username.Trim();
        return null;
    }

    private bool TryPort(out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(Port)) return true;
        return int.TryParse(Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
    }

    public ObservableCollection<ForwardRowViewModel> Forwards { get; } = new();

    [RelayCommand]
    private void AddForward() => Forwards.Add(new ForwardRowViewModel());

    [RelayCommand]
    private void RemoveForward(ForwardRowViewModel? row)
    {
        if (row is not null) Forwards.Remove(row);
    }

    // ───────────── Authentication ─────────────

    public IReadOnlyList<SshChoice> AuthModes { get; } =
    [
        new(SshAuthMode.Auto, Loc.T("Term.Editor.Auth.Auto")),
        new(SshAuthMode.Key, Loc.T("Term.Editor.Auth.Key")),
        new(SshAuthMode.Password, Loc.T("Term.Editor.Auth.Password")),
        new(SshAuthMode.Agent, Loc.T("Term.Editor.Auth.Agent")),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInherit), nameof(IsAuto), nameof(IsKey), nameof(IsPassword), nameof(IsAgent))]
    private SshAuthMode _authMode;

    public bool IsInherit { get => AuthMode == SshAuthMode.Inherit; set { if (value) AuthMode = SshAuthMode.Inherit; } }
    public bool IsAuto { get => AuthMode == SshAuthMode.Auto; set { if (value) AuthMode = SshAuthMode.Auto; } }
    public bool IsKey { get => AuthMode == SshAuthMode.Key; set { if (value) AuthMode = SshAuthMode.Key; } }
    public bool IsPassword { get => AuthMode == SshAuthMode.Password; set { if (value) AuthMode = SshAuthMode.Password; } }
    public bool IsAgent { get => AuthMode == SshAuthMode.Agent; set { if (value) AuthMode = SshAuthMode.Agent; } }

    [ObservableProperty] private string _username;
    [ObservableProperty] private string? _usernameHint;
    [ObservableProperty] private string? _inheritText;

    /// <summary>Typed in the editor's PasswordBox (null = keep the stored one).</summary>
    public string? NewPassword { get; set; }

    [ObservableProperty] private bool _hasStoredPassword;

    public ObservableCollection<KeyChoice> Keys { get; } = new();
    [ObservableProperty] private KeyChoice? _selectedKey;

    private void RebuildKeys(Guid? select)
    {
        Keys.Clear();
        foreach (var k in _main.State.Document.SshKeys.OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase))
            Keys.Add(new KeyChoice(k, k.Name, $"{Fingerprint.ShortType(k.Algorithm)} {k.Bits} · {k.Fingerprint}"));
        SelectedKey = Keys.FirstOrDefault(k => k.Key?.Id == select) ?? (Keys.Count > 0 ? Keys[0] : null);
        OnPropertyChanged(nameof(HasKeys));
    }

    public bool HasKeys => Keys.Count > 0;

    /// <summary>Imports a .pem/.key/.ppk/OpenSSH key into RDMan (encrypted) and selects it.</summary>
    [RelayCommand]
    private void ImportKey()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Term.Editor.Key.ImportTitle"),
            Filter = Loc.T("Term.Editor.Key.Filter"),
            InitialDirectory = Directory.Exists(Path.Combine(SshEnvironment.Home, ".ssh")) ? Path.Combine(SshEnvironment.Home, ".ssh") : SshEnvironment.Home,
        };
        if (dlg.ShowDialog(_main.Owner) != true) return;
        if (ImportKeyFile(dlg.FileName) is { } entry)
        {
            RebuildKeys(entry.Id);
            AuthMode = SshAuthMode.Key;
        }
    }

    /// <summary>Shared with the SSH keys window: validates, asks the passphrase when needed, stores encrypted.</summary>
    public static SshKeyEntry? ImportKeyFile(MainViewModel main, string path, System.Windows.Window? owner)
    {
        var secrets = main.State.Secrets;
        string? passphrase = null;
        var remember = false;
        var result = SshKeyVault.TryImport(path, null, secrets, out var entry);
        for (var attempt = 0; result is KeyLoadResult.NeedsPassphrase or KeyLoadResult.WrongPassphrase && attempt < 5; attempt++)
        {
            var prompt = new PassphraseWindow(Path.GetFileName(path), wrong: result == KeyLoadResult.WrongPassphrase && attempt > 0);
            if (Dialogs.Show(prompt, owner) != true) return null;
            passphrase = prompt.Passphrase;
            remember = prompt.Remember;
            result = SshKeyVault.TryImport(path, passphrase, secrets, out entry);
            if (result == KeyLoadResult.NeedsPassphrase) result = KeyLoadResult.WrongPassphrase;
        }
        if (result != KeyLoadResult.Ok || entry is null)
        {
            if (result != KeyLoadResult.Ok || passphrase is null)
                main.Toasts.Show($"{Loc.T("SshErr.KeyUnreadable.Title")}. {Loc.T("SshErr.KeyUnreadable.Detail")}", Services.ToastKind.Error);
            return null;
        }
        if (!remember) entry.ProtectedPassphrase = null;

        // The same key imported twice is one entry (renamed files, copies…): keep the existing one.
        var existing = main.State.Document.SshKeys.FirstOrDefault(k => k.Fingerprint == entry.Fingerprint);
        if (existing is not null)
        {
            if (!existing.HasKeyMaterial) existing.ProtectedKey = entry.ProtectedKey; // restore after an export without secrets
            if (remember && entry.ProtectedPassphrase is not null) existing.ProtectedPassphrase = entry.ProtectedPassphrase;
            main.State.MarkDirty();
            main.Toasts.Show(Loc.F("Term.Editor.Key.AlreadyImported", existing.Name));
            return existing;
        }
        var baseName = entry.Name;
        var n = 2;
        while (main.State.Document.SshKeys.Any(k => string.Equals(k.Name, entry.Name, StringComparison.OrdinalIgnoreCase)))
            entry.Name = $"{baseName} ({n++})";
        main.State.Document.SshKeys.Add(entry);
        main.State.MarkDirty();
        main.Toasts.Show(Loc.F("Term.Editor.Key.Imported", entry.Name, entry.Fingerprint), Services.ToastKind.Success);
        return entry;
    }

    private SshKeyEntry? ImportKeyFile(string path) => ImportKeyFile(_main, path, _main.Owner);

    // ───────────── Options ─────────────

    public ObservableCollection<SshOptionRowViewModel> Options { get; } = new();
    [ObservableProperty] private string _proxyJump;

    /// <summary>The ProxyJump typed here, or the one inherited from the groups (for the editor's test).</summary>
    public string? EffectiveProxyJump => !string.IsNullOrWhiteSpace(ProxyJump) ? ProxyJump.Trim()
        : SshSettingsResolver.ResolveUnder(_parent(), _main.State.Index).ProxyJump.Value;
    [ObservableProperty] private string _startupCommand;
    [ObservableProperty] private string? _proxyJumpHint;
    [ObservableProperty] private string? _startupCommandHint;

    private void BuildOptions(SshProfile p)
    {
        string YesNo(bool v) => v ? Loc.T("Rdp.Yes") : Loc.T("Rdp.No");
        SshChoice[] Bools() => [new(true, Loc.T("Rdp.Yes")), new(false, Loc.T("Rdp.No"))];
        string SchemeName(string id) => id == TerminalSchemes.Auto ? Loc.T("Term.Scheme.Auto") : TerminalSchemes.All.FirstOrDefault(s => s.Id == id)?.Name ?? id;

        Options.Add(new SshOptionRowViewModel("Term.Opt.Prompt", p.InjectPrompt, Bools(),
            e => (YesNo(e.InjectPrompt.Value), e.InjectPrompt.Source), (r, v) => r.InjectPrompt = (bool?)v));
        Options.Add(new SshOptionRowViewModel("Term.Opt.Scheme", p.ColorScheme,
            [new SshChoice(TerminalSchemes.Auto, Loc.T("Term.Scheme.Auto")), .. TerminalSchemes.All.Select(s => new SshChoice(s.Id, s.Name))],
            e => (SchemeName(e.ColorScheme.Value), e.ColorScheme.Source), (r, v) => r.ColorScheme = (string?)v));
        Options.Add(new SshOptionRowViewModel("Term.Opt.Stats", p.ShowStats, Bools(),
            e => (YesNo(e.ShowStats.Value), e.ShowStats.Source), (r, v) => r.ShowStats = (bool?)v));
        Options.Add(new SshOptionRowViewModel("Term.Opt.Log", p.LogSession, Bools(),
            e => (YesNo(e.LogSession.Value), e.LogSession.Source), (r, v) => r.LogSession = (bool?)v));
        Options.Add(new SshOptionRowViewModel("Term.Opt.KeepAlive", p.KeepAliveSeconds,
            [.. new[] { 15, 30, 60, 120 }.Select(n => new SshChoice(n, Loc.F("Term.Opt.Seconds", n))), new SshChoice(0, Loc.T("Term.Opt.Off"))],
            e => (e.KeepAliveSeconds.Value is { } k ? (k == 0 ? Loc.T("Term.Opt.Off") : Loc.F("Term.Opt.Seconds", k)) : Loc.T("Term.Opt.KeepAlive.Config"), e.KeepAliveSeconds.Source),
            (r, v) => r.KeepAliveSeconds = (int?)v));
        Options.Add(new SshOptionRowViewModel("Term.Opt.Clipboard", p.AllowClipboardWrite,
            [new SshChoice(true, Loc.T("Term.Opt.Clipboard.Allow")), new SshChoice(false, Loc.T("Term.Opt.Clipboard.Ask"))],
            e => (e.AllowClipboardWrite.Value ? Loc.T("Term.Opt.Clipboard.Allow") : Loc.T("Term.Opt.Clipboard.Ask"), e.AllowClipboardWrite.Source),
            (r, v) => r.AllowClipboardWrite = (bool?)v));
    }

    /// <summary>The parent group changed (or the editor opened): refresh every "inherit" preview.</summary>
    public void RefreshInherited()
    {
        var parent = _parent();
        var settings = SshSettingsResolver.ResolveUnder(parent, _main.State.Index);
        foreach (var o in Options) o.RefreshInherited(settings);
        ProxyJumpHint = settings.ProxyJump.Value is { } pj ? Loc.F("Term.Editor.InheritedHint", pj, settings.ProxyJump.Source?.Name ?? "") : Loc.T("Term.Editor.ProxyJump.Hint");
        StartupCommandHint = settings.StartupCommand.Value is { } sc ? Loc.F("Term.Editor.InheritedHint", sc, settings.StartupCommand.Source?.Name ?? "") : Loc.T("Term.Editor.Startup.Hint");

        var auth = SshAuthResolver.ResolveUnder(parent, _main.State.Index, _main.State.Document.Profiles, _main.State.Document.SshKeys);
        InheritText = auth.ModeSource is null
            ? Loc.T("Term.Editor.Auth.InheritRoot")
            : Loc.F("Term.Editor.Auth.InheritFrom", DescribeAuth(auth), auth.ModeSource.Name);
        UsernameHint = auth.Username is { } u
            ? Loc.F("Term.Editor.InheritedHint", u, auth.UsernameSource?.Name ?? "")
            : Loc.T("Term.Editor.User.Hint");
        UpdatePreview();
    }

    private static string DescribeAuth(EffectiveSshAuth a) => a.Mode switch
    {
        SshAuthMode.Key when a.Key is { } k => Loc.F("Term.Editor.Auth.Describe.Key", k.Name),
        SshAuthMode.Password => Loc.T("Term.Editor.Auth.Password"),
        SshAuthMode.Agent => Loc.T("Term.Editor.Auth.Agent"),
        _ => Loc.T("Term.Editor.Auth.Auto"),
    };

    // ───────────── Validation and apply ─────────────

    /// <summary>Null when valid.</summary>
    public string? Validate()
    {
        if (_isServer && Enabled && !TryPort(out _)) return Loc.T("Term.Editor.Validation.Port");
        if (AuthMode == SshAuthMode.Key && SelectedKey?.Key is null) return Loc.T("Term.Editor.Validation.Key");
        if (!string.IsNullOrWhiteSpace(ProxyJump) && !SshDestination.TryParseJumpList(ProxyJump, out _)) return Loc.T("SshErr.JumpInvalid.Detail");
        if (Username.Contains(' ')) return Loc.T("Term.Editor.Validation.User");
        if (_isServer && Enabled)
            foreach (var f in Forwards)
                if (f.ToModel(out var err) is null) return err;
        return null;
    }

    public void ApplyTo(TreeNode node)
    {
        var auth = new SshAuthSettings
        {
            Mode = AuthMode,
            Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
            KeyId = AuthMode == SshAuthMode.Key ? SelectedKey?.Key?.Id : null,
        };
        if (AuthMode == SshAuthMode.Password)
        {
            auth.ProtectedPassword = NewPassword is { Length: > 0 } pwd
                ? _main.State.Secrets.Protect(pwd)
                : node.SshAuth?.Mode == SshAuthMode.Password ? node.SshAuth.ProtectedPassword : null;
        }
        node.SshAuth = auth;

        var profile = new SshProfile
        {
            ProxyJump = string.IsNullOrWhiteSpace(ProxyJump) ? null : ProxyJump.Trim(),
            StartupCommand = string.IsNullOrWhiteSpace(StartupCommand) ? null : StartupCommand.Trim(),
        };
        foreach (var o in Options) o.ApplyTo(profile);
        node.Ssh = profile;

        if (node is ServerNode server)
        {
            if (Enabled)
            {
                TryPort(out var port);
                server.Terminal = new SshEndpoint
                {
                    Enabled = true,
                    Port = port > 0 ? port : null,
                    UseSshConfig = UseSshConfig,
                    Forwards = Forwards.Select(f => f.ToModel(out _)).OfType<SshForward>().ToList(),
                };
            }
            else if (server.Terminal is not null)
            {
                server.Terminal.Enabled = false; // keep its settings for when it's turned back on
            }
        }
    }
}
