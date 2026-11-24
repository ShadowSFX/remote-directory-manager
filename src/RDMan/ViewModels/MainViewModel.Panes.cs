using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using RDMan.Core.Layout;
using RDMan.Core.Persistence;
using RDMan.Infrastructure;
using RDMan.Services;

namespace RDMan.ViewModels;

/// <summary>
/// The editor grid (VS Code style): any number of panes arranged by a <see cref="PaneLayout"/> tree, a most-recently-used
/// order for "the other pane", and the window-wide list of live tabs that hosts their remote-desktop surfaces.
/// </summary>
public sealed partial class MainViewModel
{
    public const double MinPaneWidth = 380; // identity band: name + Files|Desktop switch + actions
    public const double MinPaneHeight = 240; // tab strip + identity band + toolbar + a usable content area

    private readonly ObservableCollection<BrowserTabViewModel> _liveTabs = new();
    private readonly List<PaneViewModel> _mru = new(); // most recent first
    private int _nextPaneId;

    /// <summary>Panes in reading order (the layout's leaves).</summary>
    public ObservableCollection<PaneViewModel> Panes { get; } = new();

    public PaneLayout Layout { get; private set; } = null!;

    /// <summary>
    /// Every open tab in the window, for the session overlay. Append/remove only: a tab moving between panes stays put,
    /// so the WindowsFormsHost of its remote desktop is never re-parented (that would kill the session). Never Move,
    /// Clear, sort or wrap it in a CollectionView.
    /// </summary>
    public ReadOnlyObservableCollection<BrowserTabViewModel> LiveTabs { get; private set; } = null!;

    /// <summary>The pane structure is about to change: the host reads splitter sizes back into the tree first.</summary>
    public event EventHandler? LayoutChanging;

    /// <summary>The pane structure changed (split, close, restore): the host rebuilds its grid.</summary>
    public event EventHandler? LayoutChanged;

    public bool HasMultiplePanes => Panes.Count > 1;

    /// <summary>Set by the window: whether a pane is big enough to split on a side (both halves keep the minimum size).</summary>
    public Func<PaneViewModel, DropSide, bool>? CanSplitPane { get; set; }

    public IEnumerable<BrowserTabViewModel> AllTabs => Panes.SelectMany(p => p.Tabs);

    private PaneViewModel InitPanes()
    {
        LiveTabs = new ReadOnlyObservableCollection<BrowserTabViewModel>(_liveTabs);
        var first = CreatePane();
        Panes.Add(first);
        _mru.Add(first);
        Layout = new PaneLayout(first.Id);
        first.IsActive = true;
        return first;
    }

    private PaneViewModel CreatePane() => new(this, (_nextPaneId++).ToString(CultureInfo.InvariantCulture));

    public PaneViewModel? PaneById(string id) => Panes.FirstOrDefault(p => p.Id == id);

    internal void RegisterTab(BrowserTabViewModel tab)
    {
        if (!_liveTabs.Contains(tab)) _liveTabs.Add(tab);
    }

    /// <summary>Disposes a tab that left its pane for good and drops its session surface.</summary>
    internal void ReleaseTab(BrowserTabViewModel tab)
    {
        if (ReferenceEquals(FullScreenTab, tab)) ExitFullScreen();
        tab.Dispose();
        _liveTabs.Remove(tab);
    }

    private void RaiseLayoutChanged()
    {
        // Keep Panes in reading order (F6, saving, "next pane") without touching tab collections.
        var order = Layout.Leaves();
        for (var i = 0; i < order.Count; i++)
        {
            var at = IndexOfPane(order[i]);
            if (at >= 0 && at != i) Panes.Move(at, i);
        }
        OnPropertyChanged(nameof(HasMultiplePanes));
        foreach (var p in Panes) p.NotifyLayout();
        State.MarkSettingsDirty();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private int IndexOfPane(string id)
    {
        for (var i = 0; i < Panes.Count; i++)
            if (Panes[i].Id == id) return i;
        return -1;
    }

    // ───────────── Structure ─────────────

    /// <summary>
    /// Opens a new pane next to <paramref name="target"/>. <paramref name="fill"/> puts its first tab in (a moved
    /// tab, or a location); without one the pane starts at Home. Null when the grid is full.
    /// </summary>
    public PaneViewModel? SplitPane(PaneViewModel target, DropSide side, Action<PaneViewModel>? fill = null,
        bool quiet = false, bool homeIfEmpty = true)
    {
        if (side == DropSide.Center || !Panes.Contains(target)) return null;
        if (Panes.Count >= PaneLayout.MaxPanes)
        {
            if (!quiet) Toasts.Show(Loc.F("Pane.Max", PaneLayout.MaxPanes));
            return null;
        }
        // Measure the grid as it will be: full screen shows a single, monitor-sized pane.
        if (FullScreenTab is not null)
        {
            ExitFullScreen();
            Owner?.UpdateLayout();
        }
        if (CanSplitPane?.Invoke(target, side) == false)
        {
            if (!quiet) Toasts.Show(Loc.T("Pane.NoRoom"));
            return null;
        }
        var pane = CreatePane();
        LayoutChanging?.Invoke(this, EventArgs.Empty);
        Layout.Split(target.Id, pane.Id, side);
        Panes.Add(pane);
        RaiseLayoutChanged();
        fill?.Invoke(pane);
        if (pane.Tabs.Count == 0)
        {
            if (!homeIfEmpty)
            {
                // The fill showed something that already lives elsewhere: no empty pane, no stolen focus.
                ClosePane(pane);
                return null;
            }
            pane.AddTab(new HomeLocation());
        }
        Activate(pane);
        return pane;
    }

    /// <summary>Removes a pane (never the last one). Its remaining tabs are closed; callers confirm live sessions first.</summary>
    public void ClosePane(PaneViewModel pane)
    {
        if (Panes.Count <= 1 || !Panes.Contains(pane)) return;
        if (FullScreenTab is { } fs && ReferenceEquals(fs.Pane, pane)) ExitFullScreen();
        // Hand the keyboard to the pane used before this one *before* it disappears.
        if (ReferenceEquals(ActivePane, pane)) Activate(OtherPane(pane) ?? Panes.First(p => !ReferenceEquals(p, pane)));
        LayoutChanging?.Invoke(this, EventArgs.Empty);
        Layout.Remove(pane.Id);
        Panes.Remove(pane);
        _mru.Remove(pane);
        foreach (var t in pane.Tabs.ToList())
        {
            pane.Tabs.Remove(t);
            ReleaseTab(t);
        }
        RaiseLayoutChanged();
        RequestFocusPane?.Invoke(this, ActivePane);
    }

    /// <summary>Raised when a pane should take the keyboard (after its neighbour closed).</summary>
    public event EventHandler<PaneViewModel>? RequestFocusPane;

    /// <summary>
    /// Moves a tab to <paramref name="target"/> at <paramref name="index"/> (reorders within its own pane). The tab
    /// object, its history and its live remote-desktop session go along; an emptied source pane closes.
    /// </summary>
    public void MoveTab(BrowserTabViewModel tab, PaneViewModel target, int index)
    {
        var source = tab.Pane;
        if (!Panes.Contains(target) || !source.Tabs.Contains(tab)) return;
        var from = source.Tabs.IndexOf(tab);
        if (ReferenceEquals(source, target))
        {
            // Dropping on the gap after itself means "stay".
            var to = Math.Clamp(index > from ? index - 1 : index, 0, source.Tabs.Count - 1);
            if (to != from) source.Tabs.Move(from, to);
            source.SelectedTab = tab;
            Activate(source);
            return;
        }
        if (ReferenceEquals(FullScreenTab, tab)) ExitFullScreen();
        if (ReferenceEquals(source.SelectedTab, tab) && source.Tabs.Count > 1)
            source.SelectedTab = source.Tabs[from == source.Tabs.Count - 1 ? from - 1 : from + 1];
        source.Tabs.RemoveAt(from);
        tab.Pane = target;
        target.Tabs.Insert(Math.Clamp(index, 0, target.Tabs.Count), tab);
        target.SelectedTab = tab;
        Activate(target);
        if (source.Tabs.Count == 0) ClosePane(source);
        else source.NotifyLayout();
    }

    /// <summary>Moves a tab into a brand-new pane on <paramref name="side"/> of <paramref name="target"/>.</summary>
    public void MoveTabToNewPane(BrowserTabViewModel tab, PaneViewModel target, DropSide side)
    {
        if (ReferenceEquals(tab.Pane, target) && target.Tabs.Count == 1) return; // nothing would be left behind
        SplitPane(target, side, pane => MoveTab(tab, pane, 0));
    }

    /// <summary>
    /// A server or group dropped from the tree onto a pane: the middle opens it as a new tab there, an edge opens it
    /// in a new pane on that side. A server whose desktop is already open elsewhere just shows that session.
    /// </summary>
    public void OpenNodeAt(NodeViewModel node, PaneViewModel pane, DropSide side, int? index = null)
    {
        if (!Panes.Contains(pane)) return;
        if (node is ServerNodeViewModel { DefaultAction: Core.Models.ServerAction.Desktop, HasDesktop: true } desktop
            && FindTab(t => t.Current is DesktopLocation d && d.ServerId == desktop.Model.Id) is not null)
        {
            OpenDesktop(desktop, OpenTarget.Current);
            return;
        }
        if (side == DropSide.Center)
        {
            Activate(pane);
            var before = pane.Tabs.ToList();
            OpenNodeHere(node, OpenTarget.NewTab);
            // Dropped on the tab strip: put the new tab where the marker was.
            if (index is { } at && pane.Tabs.FirstOrDefault(t => !before.Contains(t)) is { } added)
                MoveTab(added, pane, Math.Min(at, pane.Tabs.Count));
            return;
        }
        SplitPane(pane, side, p =>
        {
            Activate(p);
            OpenNodeHere(node, OpenTarget.Current);
        }, homeIfEmpty: false);
    }

    private void OpenNodeHere(NodeViewModel node, OpenTarget target)
    {
        switch (node)
        {
            case ServerNodeViewModel s: OpenServer(s, target); break;
            case GroupNodeViewModel g: OpenGroup(g, target); break;
        }
    }

    /// <summary>F3 / "Split right|down": a new pane showing the same place (a desktop's files, since a server has one session).</summary>
    public void SplitWithCopy(PaneViewModel pane, DropSide side, BrowserTabViewModel? from = null)
    {
        var tab = from ?? pane.SelectedTab;
        Location location = tab?.Current switch
        {
            DesktopLocation => tab.Server is { HasFiles: true } s ? new FolderLocation(s.RootPath) : new HomeLocation(),
            { } current => current,
            null => new HomeLocation(),
        };
        SplitPane(pane, side, p => p.AddTab(location));
    }

    [RelayCommand]
    private void SplitRight() => SplitWithCopy(ActivePane, DropSide.Right);

    [RelayCommand]
    private void SplitDown() => SplitWithCopy(ActivePane, DropSide.Bottom);

    /// <summary>"Close pane": confirms its live desktops, then closes it.</summary>
    public void CloseWholePane(PaneViewModel pane)
    {
        if (Panes.Count <= 1 || !ConfirmEndSessions(pane.Tabs)) return;
        ClosePane(pane);
    }

    // ───────────── Activation & "other pane" ─────────────
    public void Activate(PaneViewModel pane)
    {
        if (!Panes.Contains(pane)) return;
        _mru.Remove(pane);
        _mru.Insert(0, pane);
        if (ReferenceEquals(ActivePane, pane)) return;
        ActivePane.IsActive = false;
        ActivePane = pane;
        pane.IsActive = true;
    }

    /// <summary>The pane used most recently before <paramref name="from"/> — target of "copy/open in the other pane".</summary>
    public PaneViewModel? OtherPane(PaneViewModel from)
        => _mru.FirstOrDefault(p => !ReferenceEquals(p, from) && Panes.Contains(p))
           ?? Panes.FirstOrDefault(p => !ReferenceEquals(p, from));

    public BrowserTabViewModel? OtherPaneTab(PaneViewModel pane) => OtherPane(pane)?.SelectedTab;

    [RelayCommand]
    private void FocusOtherPane()
    {
        // F6 walks the panes in reading order.
        if (Panes.Count < 2) return;
        ExitFullScreen();
        var i = Panes.IndexOf(ActivePane);
        var next = Panes[(i + 1) % Panes.Count];
        Activate(next);
        RequestFocusPane?.Invoke(this, next);
    }

    // ───────────── Persistence ─────────────
    public void RestoreTabs()
    {
        var s = State.Settings;
        var layout = s.RestoreTabs ? PaneLayout.FromSettings(s) : null;
        // Start over from the placeholder pane the constructor made (it has no tabs yet).
        foreach (var p in Panes) p.IsActive = false;
        Panes.Clear();
        _mru.Clear();
        if (layout is null)
        {
            _nextPaneId = 0;
            var only = CreatePane();
            Panes.Add(only);
            Layout = new PaneLayout(only.Id);
            Restore(only, s.RestoreTabs ? s.Panes.ElementAtOrDefault(0) : null);
            ActivePane = only;
            only.IsActive = true;
            _mru.Add(only);
            RaiseLayoutChanged();
            return;
        }
        // Leaf ids are indexes into settings.Panes; panes keep them as ids, new ones count on from there.
        Layout = layout;
        _nextPaneId = s.Panes.Count;
        foreach (var id in layout.Leaves())
        {
            var pane = new PaneViewModel(this, id);
            Panes.Add(pane);
            Restore(pane, s.Panes[int.Parse(id, CultureInfo.InvariantCulture)]);
        }
        var active = PaneById(s.ActivePane.ToString(CultureInfo.InvariantCulture)) ?? Panes[0];
        foreach (var p in Panes.Where(p => !ReferenceEquals(p, active))) _mru.Add(p);
        _mru.Insert(0, active);
        ActivePane = active;
        active.IsActive = true;
        RaiseLayoutChanged();
    }

    private void SaveSession()
    {
        var s = State.Settings;
        var order = Layout.Leaves();
        s.Panes = order.Select(id => PaneById(id)?.Save() ?? new SavedPane()).ToList();
        s.Layout = Layout.ToSaved(order);
        s.ActivePane = Math.Max(0, IndexOfOrder(order, ActivePane.Id));
        s.SplitView = Panes.Count > 1; // read by 1.x builds
        State.Flush();
    }

    private static int IndexOfOrder(IReadOnlyList<string> order, string id)
    {
        for (var i = 0; i < order.Count; i++)
            if (order[i] == id) return i;
        return -1;
    }
}
