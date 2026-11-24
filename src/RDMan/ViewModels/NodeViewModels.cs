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
            ExpansionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public static event EventHandler? ExpansionChanged;

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

    public ServerNodeViewModel(ServerNode model, GroupNodeViewModel? parent, HealthMonitor monitor) : base(model, parent)
    {
        Monitor = monitor;
        _filesHealth = monitor.Get(model.Host);
        _filesHealth.PropertyChanged += OnHealthChanged;
        AttachDesktopHealth();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    private HealthMonitor Monitor { get; }

    public ServerNode Server => (ServerNode)Model;
    public string Host => Server.Host;
    public string? Share => Server.Share;

    public bool HasFiles => Server.HasFiles;
    public bool HasDesktop => Server.HasDesktop;
    public bool HasBoth => HasFiles && HasDesktop;
    public int DesktopPort => Server.Desktop?.Port ?? DesktopEndpoint.DefaultPort;
    public ServerAction DefaultAction => Server.EffectiveDefaultAction;
    public bool IsFavorite => Server.IsFavorite;
    /// <summary>The tree shows the address next to the name, unless the name already is the address.</summary>
    public bool ShowHostInTree => !string.Equals(Name, Host, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>The status shown on the dot: the service a plain click opens.</summary>
    public HostHealth Health => DefaultAction == ServerAction.Desktop && _desktopHealth is not null ? _desktopHealth : _filesHealth;

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
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>Short status of the primary service, for cards.</summary>
    public string PrimaryStatusText => Describe(Health, desktop: ReferenceEquals(Health, _desktopHealth), compact: true);

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
        base.Refresh();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PrimaryStatusText));
        OnPropertyChanged(nameof(LastConnectedText));
    }

    public void Detach()
    {
        _filesHealth.PropertyChanged -= OnHealthChanged;
        if (_desktopHealth is not null) _desktopHealth.PropertyChanged -= OnHealthChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>Endpoints the health monitor should probe for this server.</summary>
    public IEnumerable<(string Host, int Port)> Endpoints()
    {
        if (HasFiles) yield return (Server.Host, ConnectionService.SmbPort);
        if (HasDesktop) yield return (Server.Host, DesktopPort);
    }
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

