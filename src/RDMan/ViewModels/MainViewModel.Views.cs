using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDMan.Core.Models;
using RDMan.Core.Search;
using RDMan.Infrastructure;
using RDMan.Services;
using RDMan.Views;

namespace RDMan.ViewModels;

/// <summary>A smart group row in the sidebar.</summary>
public sealed partial class SmartGroupViewModel : ObservableObject
{
    public SmartGroupViewModel(SmartGroup model) => Model = model;

    public SmartGroup Model { get; }
    public string Name => Model.Name;
    [ObservableProperty] private int _count;

    public void Refresh() => OnPropertyChanged(nameof(Name));
}

/// <summary>
/// Views that cut across the tree: Favorites, Recent, Connected (computed, never stored) and smart groups
/// (saved searches stored next to the tree).
/// </summary>
public sealed partial class MainViewModel
{
    public const string ViewFavorites = "favorites";
    public const string ViewRecent = "recent";
    public const string ViewConnected = "connected";
    private const int RecentLimit = 15;

    public ObservableCollection<SmartGroupViewModel> SmartGroups { get; } = new();

    [ObservableProperty] private int _favoriteCount;
    [ObservableProperty] private int _recentCount;
    [ObservableProperty] private int _connectedCount;

    private void BuildSmartGroups()
    {
        SmartGroups.Clear();
        foreach (var g in State.Document.SmartGroups) SmartGroups.Add(new SmartGroupViewModel(g));
        RefreshViewCounts();
    }

    /// <summary>Servers shown by a view, in its natural order.</summary>
    public IReadOnlyList<ServerNodeViewModel> ViewServers(string view) => view switch
    {
        ViewFavorites => AllServers().Where(s => s.Server.IsFavorite).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
        ViewRecent => AllServers().Where(s => s.Server.LastConnected is not null)
            .OrderByDescending(s => s.Server.LastConnected).Take(RecentLimit).ToList(),
        ViewConnected => ConnectedServers().OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
        _ => [],
    };

    /// <summary>A server is "connected" while RDMan holds its desktop session or a file session it opened.</summary>
    private IEnumerable<ServerNodeViewModel> ConnectedServers()
    {
        var live = LiveDesktopServers();
        return AllServers().Where(s => live.Contains(s) || Services.Connections.HasOpenSession(s.Host));
    }

    public IReadOnlyList<ServerNodeViewModel> SmartGroupServers(SmartGroup group)
        => SmartGroupEvaluator.Evaluate(State.Document.Root, group).Select(h => FindServer(h.Server.Id)).OfType<ServerNodeViewModel>().ToList();

    public SmartGroupViewModel? FindSmartGroup(Guid id) => SmartGroups.FirstOrDefault(g => g.Model.Id == id);

    public static string ViewTitle(string view) => view switch
    {
        ViewFavorites => Loc.T("View.Favorites"),
        ViewRecent => Loc.T("View.Recent"),
        ViewConnected => Loc.T("View.Connected"),
        _ => "",
    };

    public static string ViewGlyph(string view) => view switch
    {
        ViewFavorites => "",
        ViewRecent => "",
        _ => "",
    };

    public const int MaxSmartGroups = 100;
    private bool _countsQueued;

    /// <summary>Recounts the sidebar badges once the current burst of changes is over (one pass, any number of calls).</summary>
    public void RefreshViewCounts()
    {
        if (_countsQueued) return;
        _countsQueued = true;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _countsQueued = false;
            RecountNow();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void RecountNow()
    {
        int favorites = 0, recent = 0, connected = 0;
        var live = LiveDesktopServers();
        foreach (var s in AllServers())
        {
            if (s.Server.IsFavorite) favorites++;
            if (s.Server.LastConnected is not null) recent++;
            if (live.Contains(s) || Services.Connections.HasOpenSession(s.Host)) connected++;
        }
        FavoriteCount = favorites;
        RecentCount = Math.Min(RecentLimit, recent);
        ConnectedCount = connected;
        var counts = SmartGroupEvaluator.Count(State.Document.Root, SmartGroups.Select(g => g.Model).ToList());
        for (var i = 0; i < SmartGroups.Count && i < counts.Count; i++) SmartGroups[i].Count = counts[i];
    }

    private HashSet<ServerNodeViewModel> LiveDesktopServers()
        => AllSessions.Where(s => s.IsConnected).Select(s => s.Server).ToHashSet();

    /// <summary>Adds smart groups (new or imported) up to the limit; false (with a message) when there's no room.</summary>
    public bool TryAddSmartGroups(IReadOnlyList<SmartGroup> groups)
    {
        var room = MaxSmartGroups - State.Document.SmartGroups.Count;
        foreach (var g in groups.Take(Math.Max(0, room)))
        {
            State.Document.SmartGroups.Add(g);
            SmartGroups.Add(new SmartGroupViewModel(g));
        }
        if (groups.Count <= room) return true;
        Toasts.Show(Loc.F("Smart.Max", MaxSmartGroups), ToastKind.Error);
        return false;
    }

    /// <summary>A session opened or closed somewhere: the Connected view and badges follow.</summary>
    public void NotifyConnectionsChanged()
    {
        RefreshViewCounts();
        foreach (var tab in AllTabs)
            if (tab.Current is ViewLocation { View: ViewConnected or ViewRecent }) _ = tab.StartAsync();
    }

    // ───────────── Commands ─────────────
    [RelayCommand]
    private void OpenView(string view) => Open(new ViewLocation(view), OpenTarget.Current);

    [RelayCommand]
    private void OpenSmartGroup(SmartGroupViewModel? group)
    {
        if (group is not null) Open(new SmartGroupLocation(group.Model.Id), OpenTarget.Current);
    }

    [RelayCommand]
    private void OpenSmartGroupInNewTab(SmartGroupViewModel? group)
    {
        if (group is not null) Open(new SmartGroupLocation(group.Model.Id), OpenTarget.NewTab);
    }

    [RelayCommand]
    public void ToggleFavorite(ServerNodeViewModel? server)
    {
        if (server is null) return;
        server.Server.IsFavorite = !server.Server.IsFavorite;
        server.Refresh();
        Changed();
        Toasts.Show(Loc.F(server.Server.IsFavorite ? "Favorite.Added" : "Favorite.Removed", server.Name));
    }

    [RelayCommand]
    private void NewSmartGroup()
    {
        if (State.Document.SmartGroups.Count >= MaxSmartGroups)
        {
            Toasts.Show(Loc.F("Smart.Max", MaxSmartGroups), ToastKind.Error);
            return;
        }
        var model = new SmartGroup { Rules = { new SmartRule() } };
        var editor = new SmartGroupEditorViewModel(this, model, isNew: true);
        if (Dialogs.ShowSmartGroupEditor(Owner, editor) != true) return;
        TryAddSmartGroups([model]);
        Changed();
        OpenSmartGroup(SmartGroups[^1]);
    }

    [RelayCommand]
    public void EditSmartGroup(SmartGroupViewModel? group)
    {
        if (group is null) return;
        var editor = new SmartGroupEditorViewModel(this, group.Model, isNew: false);
        var result = Dialogs.ShowSmartGroupEditor(Owner, editor);
        if (editor.Deleted)
        {
            DeleteSmartGroupCore(group);
            return;
        }
        if (result != true) return;
        group.Refresh();
        Changed();
    }

    [RelayCommand]
    private void DeleteSmartGroup(SmartGroupViewModel? group)
    {
        if (group is null) return;
        if (!Dialogs.Confirm(Owner, Loc.T("Common.Delete"), Loc.F("Smart.ConfirmDelete", group.Name), Loc.T("Common.Delete"), danger: true)) return;
        DeleteSmartGroupCore(group);
    }

    private void DeleteSmartGroupCore(SmartGroupViewModel group)
    {
        State.Document.SmartGroups.Remove(group.Model);
        SmartGroups.Remove(group);
        Changed(); // tabs showing it fall back to Home
    }
}
