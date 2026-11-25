using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RDMan.Core.Models;
using RDMan.Core.Smb;
using RDMan.Infrastructure;
using RDMan.Services;

namespace RDMan.ViewModels;

public abstract partial class NodeViewModel : ObservableObject
{
    protected NodeViewModel(TreeNode model, GroupNodeViewModel? parent)
    {
        Model = model;
        Parent = parent;
    }

    public TreeNode Model { get; }
    public GroupNodeViewModel? Parent { get; set; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isDropTarget;

    public string Name => Model.Name;
    public string? Description => string.IsNullOrWhiteSpace(Model.Description) ? null : Model.Description;
    public string? ColorKey => Model.Color;
    public Brush? ColorBrush => BrushFor(Model.Color);
    public bool HasColor => ColorBrush is not null;

    public static Brush? BrushFor(string? key)
    {
        var hex = TagColors.HexFor(key);
        if (hex is null) return null;
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>Re-reads every bound property from the model (after an edit dialog).</summary>
    public virtual void Refresh() => OnPropertyChanged(string.Empty);

    public IEnumerable<GroupNodeViewModel> Ancestors()
    {
        for (var p = Parent; p is not null; p = p.Parent) yield return p;
    }

    /// <summary>A group or a server was expanded/collapsed in the tree (the state is saved).</summary>
    public static event EventHandler? ExpansionChanged;

    protected static void RaiseExpansionChanged(NodeViewModel node) => ExpansionChanged?.Invoke(node, EventArgs.Empty);
}

public sealed partial class GroupNodeViewModel : NodeViewModel
{
    public GroupNodeViewModel(GroupNode model, GroupNodeViewModel? parent) : base(model, parent) { }

    public GroupNode Group => (GroupNode)Model;

    public ObservableCollection<NodeViewModel> Children { get; } = new();

    public bool IsExpanded
    {
        get => Group.IsExpanded;
        set
        {
            if (Group.IsExpanded == value) return;
            Group.IsExpanded = value;
            OnPropertyChanged();
            RaiseExpansionChanged(this);
        }
    }

    public IEnumerable<ServerNodeViewModel> AllServers()
    {
        foreach (var c in Children)
        {
            if (c is ServerNodeViewModel s) yield return s;
            else if (c is GroupNodeViewModel g)
                foreach (var nested in g.AllServers()) yield return nested;
        }
    }

    public int ServerCount => AllServers().Count();
    public int OnlineCount => AllServers().Count(s => s.Health.State == HealthState.Online);
    public string CountText => ServerCount.ToString();

    public void NotifyCounts()
    {
        OnPropertyChanged(nameof(ServerCount));
        OnPropertyChanged(nameof(OnlineCount));
        OnPropertyChanged(nameof(CountText));
        Parent?.NotifyCounts();
    }
}

public sealed partial class ServerNodeViewModel : NodeViewModel
{
    private HostHealth _filesHealth;
    private HostHealth? _desktopHealth;
    private HostHealth? _terminalHealth;

    public ServerNodeViewModel(ServerNode model, GroupNodeViewModel? parent, HealthMonitor monitor) : base(model, parent)
    {
        Monitor = monitor;
        _filesHealth = monitor.Get(model.Host);
        _filesHealth.PropertyChanged += OnHealthChanged;
        AttachDesktopHealth();
        AttachTerminalHealth();
        SyncResources();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    private HealthMonitor Monitor { get; }

    public ServerNode Server => (ServerNode)Model;
    public string Host => Server.Host;
    public string? Share => Server.Share;

    public bool HasFiles => Server.HasFiles;
    public bool HasDesktop => Server.HasDesktop;
    public bool HasBoth => HasFiles && HasDesktop;
    public bool HasTerminal => Server.HasTerminal;
    /// <summary>How many of files / desktop / terminal this server offers (the view switch needs two or more).</summary>
    public int CapabilityCount => Server.CapabilityCount;
    public int DesktopPort => Server.Desktop?.Port ?? DesktopEndpoint.DefaultPort;
    public ServerAction DefaultAction => Server.EffectiveDefaultAction;
    // The tree highlights the badge a click opens, only when there's a choice.
    public bool FilesIsDefault => CapabilityCount > 1 && DefaultAction == ServerAction.Files;
    public bool DesktopIsDefault => CapabilityCount > 1 && DefaultAction == ServerAction.Desktop;
    public bool TerminalIsDefault => CapabilityCount > 1 && DefaultAction == ServerAction.Terminal;
    public bool IsFavorite => Server.IsFavorite;

    /// <summary>
    /// The resources listed under the server in the tree (one row per files / desktop / terminal). Empty when the
    /// server offers a single resource: a click opens it directly and there's nothing to expand.
    /// </summary>
    public ObservableCollection<ServerResourceViewModel> Resources { get; } = new();

    /// <summary>The tree shows <see cref="Resources"/> (saved, like a group's).</summary>
    public bool IsExpanded
    {
        get => Server.IsExpanded && Resources.Count > 0;
        set
        {
            if (value && Resources.Count == 0) value = false;
            if (Server.IsExpanded == value) return;
            Server.IsExpanded = value;
            OnPropertyChanged();
            RaiseExpansionChanged(this);
        }
    }

    // Which resources have a tab or window right now: the tree outlines their badges and dots their rows.
    private HashSet<ServerAction> _open = [];
    public bool FilesIsOpen => _open.Contains(ServerAction.Files);
    public bool DesktopIsOpen => _open.Contains(ServerAction.Desktop);
    public bool TerminalIsOpen => _open.Contains(ServerAction.Terminal);
    public bool IsOpen(ServerAction action) => _open.Contains(action);

    public void SetOpen(HashSet<ServerAction> open)
    {
        if (_open.SetEquals(open)) return;
        _open = open;
        OnPropertyChanged(nameof(FilesIsOpen));
        OnPropertyChanged(nameof(DesktopIsOpen));
        OnPropertyChanged(nameof(TerminalIsOpen));
        foreach (var r in Resources) r.IsOpen = open.Contains(r.Action);
    }

    public ServerResourceViewModel? ResourceFor(ServerAction action) => Resources.FirstOrDefault(r => r.Action == action);

    /// <summary>Keeps the resource rows in step with the capabilities (rebuilt only when they change: no flicker).</summary>
    private void SyncResources()
    {
        var offered = Server.CapabilityCount > 1 ? Server.OfferedActions : [];
        if (!Resources.Select(r => r.Action).SequenceEqual(offered))
        {
            Resources.Clear();
            foreach (var a in offered) Resources.Add(new ServerResourceViewModel(this, a) { IsOpen = _open.Contains(a) });
        }
        else foreach (var r in Resources) r.Refresh();
        if (Resources.Count == 0 && Server.IsExpanded) Server.IsExpanded = false; // nothing left to show under it
    }
    /// <summary>The tree row shows only the alias (full width); the address only when there's no alias.</summary>
    public string TreeLabel => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    private System.Windows.Media.ImageSource? _thumbnail;

    /// <summary>Last picture of this server's remote desktop while connected (null otherwise).</summary>
    public System.Windows.Media.ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Reachability of the SMB port (445).</summary>
    public HostHealth FilesHealth => _filesHealth;

    /// <summary>Reachability of the RDP port, or null when the server has no desktop.</summary>
    public HostHealth? DesktopHealth => _desktopHealth;

    /// <summary>Reachability of the SSH port (through ~/.ssh/config aliases), or null when the server has no terminal.</summary>
    public HostHealth? TerminalHealth => _terminalHealth;

    /// <summary>The status shown on the dot: the service a plain click opens.</summary>
    public HostHealth Health => DefaultAction switch
    {
        ServerAction.Desktop when _desktopHealth is not null => _desktopHealth,
        ServerAction.Terminal when _terminalHealth is not null => _terminalHealth,
        _ when !HasFiles && _desktopHealth is not null => _desktopHealth,
        _ when !HasFiles && _terminalHealth is not null => _terminalHealth,
        _ => _filesHealth,
    };

    /// <summary>"10.0.0.5\www" — what the user would have typed before.</summary>
    public string Address => Server.Share is null || !HasFiles ? Server.Host : $@"{Server.Host}\{Server.Share}";

    public string RootPath
    {
        get
        {
            var t = new SmbTarget(Server.Host, Server.Share, Server.Share is null ? null : Server.StartFolder);
            return t.FullPath;
        }
    }

    public string ShareRootPath => new SmbTarget(Server.Host, Server.Share, null).FullPath;

    public string StatusText
    {
        get
        {
            var parts = new List<string>();
            if (HasFiles) parts.Add(Describe(_filesHealth, desktop: false));
            if (_desktopHealth is not null) parts.Add(Describe(_desktopHealth, desktop: true));
            if (_terminalHealth is not null) parts.Add(DescribeTerminal(_terminalHealth));
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>Short status of the primary service, for cards.</summary>
    public string PrimaryStatusText => Describe(Health, desktop: ReferenceEquals(Health, _desktopHealth) || ReferenceEquals(Health, _terminalHealth), compact: true);

    private static string DescribeTerminal(HostHealth h) => h.State switch
    {
        HealthState.Online => Loc.F("Health.Terminal.Online", h.LatencyMs ?? 0),
        HealthState.Offline => Loc.F("Health.Terminal.Offline", h.Port),
        _ => Loc.T("Health.Unknown"),
    };

    private static string Describe(HostHealth h, bool desktop, bool compact = false) => h.State switch
    {
        HealthState.Online when compact => Loc.F("Health.Online", h.LatencyMs ?? 0),
        HealthState.Online => Loc.F(desktop ? "Health.Desktop.Online" : "Health.Files.Online", h.LatencyMs ?? 0),
        HealthState.Offline when desktop => Loc.F("Health.Desktop.Offline", h.Port),
        HealthState.Offline => Loc.T("Health.Offline"),
        _ => Loc.T("Health.Unknown"),
    };

    public string TagsText => string.Join("  ", Server.Tags);
    public bool HasTags => Server.Tags.Count > 0;

    public string LastConnectedText => Server.LastConnected is { } d ? RelativeTime.Format(d) : Loc.T("Health.Never");

    private void OnHealthChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HostHealth.State) or nameof(HostHealth.LatencyMs))
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(PrimaryStatusText));
            if (e.PropertyName == nameof(HostHealth.State)) Parent?.NotifyCounts();
        }
    }

    private void AttachDesktopHealth()
    {
        if (_desktopHealth is not null) _desktopHealth.PropertyChanged -= OnHealthChanged;
        _desktopHealth = HasDesktop ? Monitor.Get(Server.Host, DesktopPort) : null;
        if (_desktopHealth is not null) _desktopHealth.PropertyChanged += OnHealthChanged;
    }

    private void AttachTerminalHealth()
    {
        if (_terminalHealth is not null) _terminalHealth.PropertyChanged -= OnHealthChanged;
        _terminalHealth = TerminalEndpoint() is { } ep ? Monitor.Get(ep.Host, ep.Port) : null;
        if (_terminalHealth is not null) _terminalHealth.PropertyChanged += OnHealthChanged;
    }

    /// <summary>Where the SSH port really is (an alias like "pangya-vps" resolves through ~/.ssh/config).</summary>
    private (string Host, int Port)? TerminalEndpoint()
        => HasTerminal ? Services.Ssh.SshEnvironment.ProbeEndpoint(Server, InheritedProxyJump()) : null;

    /// <summary>The server's ProxyJump, or the nearest group's (the option is inherited down the tree).</summary>
    private string? InheritedProxyJump()
    {
        if (!string.IsNullOrWhiteSpace(Server.Ssh?.ProxyJump)) return Server.Ssh.ProxyJump;
        for (var g = Parent; g is not null; g = g.Parent)
            if (!string.IsNullOrWhiteSpace(g.Model.Ssh?.ProxyJump)) return g.Model.Ssh.ProxyJump;
        return null;
    }

    public override void Refresh()
    {
        // Host, port or capabilities may have changed → re-attach to the right health records.
        var nextFiles = Monitor.Get(Server.Host);
        if (!ReferenceEquals(nextFiles, _filesHealth))
        {
            _filesHealth.PropertyChanged -= OnHealthChanged;
            _filesHealth = nextFiles;
            _filesHealth.PropertyChanged += OnHealthChanged;
        }
        AttachDesktopHealth();
        AttachTerminalHealth();
        SyncResources();
        base.Refresh();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PrimaryStatusText));
        OnPropertyChanged(nameof(LastConnectedText));
        foreach (var r in Resources) r.Refresh(); // their names
    }

    public void Detach()
    {
        _filesHealth.PropertyChanged -= OnHealthChanged;
        if (_desktopHealth is not null) _desktopHealth.PropertyChanged -= OnHealthChanged;
        if (_terminalHealth is not null) _terminalHealth.PropertyChanged -= OnHealthChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>Endpoints the health monitor should probe for this server.</summary>
    public IEnumerable<(string Host, int Port)> Endpoints()
    {
        if (HasFiles) yield return (Server.Host, ConnectionService.SmbPort);
        if (HasDesktop) yield return (Server.Host, DesktopPort);
        if (TerminalEndpoint() is { } ssh) yield return ssh;
    }
}

/// <summary>
/// One resource of a server (shared files, remote desktop or terminal) listed under it in the tree. Not a
/// <see cref="NodeViewModel"/>: it can't be dragged, renamed or deleted; it only opens (or shows) that resource.
/// </summary>
public sealed partial class ServerResourceViewModel : ObservableObject
{
    public ServerResourceViewModel(ServerNodeViewModel server, ServerAction action)
    {
        Server = server;
        Action = action;
    }

    public ServerNodeViewModel Server { get; }
    public ServerAction Action { get; }

    /// <summary>A tab or window shows this resource: a click goes there instead of connecting again.</summary>
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isSelected;

    // The tree's item style binds these on every row; a resource has no children and is never a drop target.
    public bool IsExpanded { get => false; set { } }
    public bool IsDropTarget => false;

    public string Label => Loc.T(Action switch
    {
        ServerAction.Desktop => "Cap.Desktop",
        ServerAction.Terminal => "Cap.Terminal",
        _ => "Cap.Files",
    });

    public string Protocol => Action switch
    {
        ServerAction.Desktop => "RDP",
        ServerAction.Terminal => "SSH",
        _ => "SMB",
    };

    public string Glyph => Action switch
    {
        ServerAction.Desktop => "",
        ServerAction.Terminal => "",
        _ => "",
    };

    /// <summary>What sets this resource apart at a glance: the share, or a port that isn't the usual one.</summary>
    public string? Detail => Action switch
    {
        ServerAction.Files => string.IsNullOrWhiteSpace(Server.Share) ? null : Server.Share,
        ServerAction.Desktop => Server.DesktopPort != DesktopEndpoint.DefaultPort ? $":{Server.DesktopPort}" : null,
        _ => Server.Server.Terminal?.Port is { } p && p != SshEndpoint.DefaultPort ? $":{p}" : null,
    };

    /// <summary>Where it points: "\host\share", "host:3389", "ssh host".</summary>
    public string Address => Action switch
    {
        ServerAction.Desktop => $"{Server.Host}:{Server.DesktopPort}",
        ServerAction.Terminal => $"ssh {Server.Host}" + (Server.Server.Terminal?.Port is { } p && p != SshEndpoint.DefaultPort ? $" -p {p}" : ""),
        _ => Server.RootPath,
    };

    public void Refresh() => OnPropertyChanged(string.Empty);
}

public static class RelativeTime
{
    public static string Format(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when;
        if (span < TimeSpan.FromMinutes(1)) return Loc.T("Rel.JustNow");
        if (span < TimeSpan.FromHours(1)) return Loc.F("Rel.Minutes", (int)span.TotalMinutes);
        if (span < TimeSpan.FromDays(1)) return Loc.F("Rel.Hours", (int)span.TotalHours);
        if (span < TimeSpan.FromDays(30)) return Loc.F("Rel.Days", (int)span.TotalDays);
        return when.LocalDateTime.ToString("d");
    }
}

