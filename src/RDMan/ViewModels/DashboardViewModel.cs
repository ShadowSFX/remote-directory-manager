using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RDMan.Infrastructure;

namespace RDMan.ViewModels;

public sealed class DashboardSection
{
    public DashboardSection(string? title, GroupNodeViewModel? group, IReadOnlyList<ServerNodeViewModel> servers)
    {
        Title = title;
        Group = group;
        Servers = servers;
    }

    public string? Title { get; }
    public bool HasTitle => !string.IsNullOrEmpty(Title);
    public GroupNodeViewModel? Group { get; }
    public IReadOnlyList<ServerNodeViewModel> Servers { get; }
}

public enum DashboardKind { Home, Group, View, Smart }

/// <summary>Cards for every server in a group (or all of them on Home) — RDCMan's "thumbnails", for shares.</summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly IReadOnlyList<ServerNodeViewModel> _all;

    /// <summary>A flat list of servers (a view or a smart group), each card captioned with its group.</summary>
    public DashboardViewModel(string title, DashboardKind kind, IReadOnlyList<ServerNodeViewModel> servers, string emptyTitle, string emptyBody,
        SmartGroupViewModel? smart = null, bool thumbnails = false)
    {
        Title = title;
        Kind = kind;
        ShowThumbnails = thumbnails;
        Smart = smart;
        EmptyTitle = emptyTitle;
        EmptyBody = emptyBody;
        Sections = new ObservableCollection<DashboardSection>(servers.Count == 0 ? [] : [new DashboardSection(null, null, servers)]);
        _all = servers;
        foreach (var s in _all) System.ComponentModel.PropertyChangedEventManager.AddHandler(s.Health, OnHealthChanged, nameof(Services.HostHealth.State));
    }

    public DashboardViewModel(string title, GroupNodeViewModel? group, IReadOnlyList<NodeViewModel> children)
    {
        Title = title;
        Group = group;
        Kind = group is null ? DashboardKind.Home : DashboardKind.Group;
        EmptyTitle = Loc.T("Tree.Empty.Title");
        EmptyBody = Loc.T("Tree.Empty.Body");
        var sections = new List<DashboardSection>();
        var direct = children.OfType<ServerNodeViewModel>().ToList();
        if (direct.Count > 0) sections.Add(new DashboardSection(null, group, direct));
        foreach (var g in children.OfType<GroupNodeViewModel>()) AddGroup(g, g.Name, sections);
        Sections = new ObservableCollection<DashboardSection>(sections);
        _all = sections.SelectMany(s => s.Servers).ToList();
        // Weak subscription: dashboards are short-lived, health records live for the whole session.
        foreach (var s in _all) System.ComponentModel.PropertyChangedEventManager.AddHandler(s.Health, OnHealthChanged, nameof(Services.HostHealth.State));
    }

    private void OnHealthChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => OnPropertyChanged(nameof(Subtitle));

    public void RefreshLanguage() => OnPropertyChanged(nameof(Subtitle));

    private static void AddGroup(GroupNodeViewModel g, string path, List<DashboardSection> sections)
    {
        var servers = g.Children.OfType<ServerNodeViewModel>().ToList();
        if (servers.Count > 0) sections.Add(new DashboardSection(path, g, servers));
        foreach (var child in g.Children.OfType<GroupNodeViewModel>()) AddGroup(child, $"{path} / {child.Name}", sections);
    }

    public string Title { get; }
    public GroupNodeViewModel? Group { get; }
    public DashboardKind Kind { get; }
    public SmartGroupViewModel? Smart { get; }
    public bool IsHome => Kind == DashboardKind.Home;
    /// <summary>Home and groups offer "New server" and the onboarding card; views/smart groups explain how to fill them.</summary>
    public bool IsTreeDashboard => Kind is DashboardKind.Home or DashboardKind.Group;
    public bool CanEdit => Kind is DashboardKind.Group or DashboardKind.Smart;
    /// <summary>When empty, the onboarding card carries "New server" instead.</summary>
    public bool ShowNewServer => IsTreeDashboard && !IsEmpty;
    /// <summary>Connected view: big cards with a picture of each remote desktop.</summary>
    public bool ShowThumbnails { get; }
    public string EmptyTitle { get; }
    public string EmptyBody { get; }
    public ObservableCollection<DashboardSection> Sections { get; }
    public bool IsEmpty => _all.Count == 0;

    public string Subtitle => Loc.F(_all.Count == 1 ? "Group.Subtitle.One" : IsHome ? "Home.Subtitle" : "Group.Subtitle",
        _all.Count, _all.Count(s => s.Health.State == Services.HealthState.Online));
}
