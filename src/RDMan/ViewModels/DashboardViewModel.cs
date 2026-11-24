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

/// <summary>Cards for every server in a group (or all of them on Home) — RDCMan's "thumbnails", for shares.</summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly IReadOnlyList<ServerNodeViewModel> _all;

    public DashboardViewModel(string title, GroupNodeViewModel? group, IReadOnlyList<NodeViewModel> children)
    {
        Title = title;
        Group = group;
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
    public bool IsHome => Group is null;
    public ObservableCollection<DashboardSection> Sections { get; }
    public bool IsEmpty => _all.Count == 0;

    public string Subtitle => Loc.F(IsHome ? "Home.Subtitle" : "Group.Subtitle", _all.Count, _all.Count(s => s.Health.State == Services.HealthState.Online));
}
