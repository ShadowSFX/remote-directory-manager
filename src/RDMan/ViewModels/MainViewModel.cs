using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RDMan.Core.Models;
using RDMan.Core.Persistence;
using RDMan.Core.Smb;
using RDMan.Infrastructure;
using RDMan.Services;
using RDMan.Views;

namespace RDMan.ViewModels;

public enum OpenTarget { Current, NewTab, OtherPane }

public sealed record LocalPlace(string Name, string Path, System.Windows.Media.ImageSource? Icon);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<Guid, NodeViewModel> _map = new();

    public MainViewModel(AppServices services)
    {
        Services = services;
        _activePane = InitPanes();
        Palette = new CommandPaletteViewModel(this);
        BuildTree();
        BuildSmartGroups();
        BuildLocalPlaces();
        services.State.SaveFailed += (_, msg) => services.Toasts.Show(Loc.F("Toast.SaveFailed", msg), ToastKind.Error);
        services.Updates.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpdateService.Status)) OnUpdateStatusChanged();
        };
        GroupNodeViewModel.ExpansionChanged += (_, _) => Services.State.MarkDirty();
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            BuildLocalPlaces();
            OnPropertyChanged(nameof(UpdateReadyText));
            OnPropertyChanged(nameof(DataLocationText));
        };
    }

    public AppServices Services { get; }
    public AppState State => Services.State;
    public ToastService Toasts => Services.Toasts;
    public UpdateService Updates => Services.Updates;
    public CommandPaletteViewModel Palette { get; }

    public ObservableCollection<NodeViewModel> RootNodes { get; } = new();
    public ObservableCollection<LocalPlace> LocalPlaces { get; } = new();

    /// <summary>The desktop tab shown full screen, or null. MainWindow hides all chrome while this is set.</summary>
    [ObservableProperty] private BrowserTabViewModel? _fullScreenTab;

    partial void OnFullScreenTabChanged(BrowserTabViewModel? oldValue, BrowserTabViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsFullScreen = false;
        if (newValue is not null) newValue.IsFullScreen = true;
    }

    /// <summary>Enters/leaves full screen for a connected desktop tab (toolbar, F11, Ctrl+Alt+Pause, the top bar).</summary>
    public void SetFullScreen(BrowserTabViewModel tab, bool on)
    {
        if (on)
        {
            if (tab.Session is not { IsConnected: true } || ReferenceEquals(FullScreenTab, tab)) return;
            Reveal(tab.Pane, tab);
            FullScreenTab = tab;
        }
        else if (ReferenceEquals(FullScreenTab, tab)) FullScreenTab = null;
        else return;
        tab.Session?.SyncFullScreen(on);
        if (on) tab.Session?.Focus();
    }

    [RelayCommand]
    private void ToggleFullScreen()
    {
        if (FullScreenTab is { } current) SetFullScreen(current, false);
        else if (ActiveTab is { IsDesktop: true } tab) SetFullScreen(tab, true);
    }

    [RelayCommand]
    private void ExitFullScreen()
    {
        if (FullScreenTab is { } current) SetFullScreen(current, false);
    }
    [ObservableProperty] private PaneViewModel _activePane;
    [ObservableProperty] private bool _isUpdateBannerVisible;

    public Window? Owner { get; set; }
    public IntPtr OwnerHandle => Owner is null ? IntPtr.Zero : new WindowInteropHelper(Owner).Handle;

    public BrowserTabViewModel? ActiveTab => ActivePane.SelectedTab;

    public bool IsThisPcExpanded
    {
        get => State.Settings.ThisPcExpanded;
        set
        {
            if (State.Settings.ThisPcExpanded == value) return;
            State.Settings.ThisPcExpanded = value;
            OnPropertyChanged();
            State.MarkSettingsDirty();
        }
    }
    public bool IsViewsExpanded
    {
        get => State.Settings.ViewsExpanded;
        set
        {
            if (State.Settings.ViewsExpanded == value) return;
            State.Settings.ViewsExpanded = value;
            OnPropertyChanged();
            State.MarkSettingsDirty();
        }
    }
    public bool IsTreeEmpty => RootNodes.Count == 0;
    public string WindowTitle => ActiveTab is { } t ? $"{t.Title} — Remote Directory Manager" : "Remote Directory Manager";
    public string DataLocationText => State.Paths.IsPortable ? Loc.T("Status.Portable") : Loc.T("Status.AppData");

    // ───────────── Tree ─────────────
    private void BuildTree()
    {
        foreach (var vm in _map.Values.OfType<ServerNodeViewModel>()) vm.Detach();
        _map.Clear();
        RootNodes.Clear();
        foreach (var child in State.Document.Root.Children) RootNodes.Add(CreateVm(child, null));
        OnPropertyChanged(nameof(IsTreeEmpty));
    }

    private NodeViewModel CreateVm(TreeNode node, GroupNodeViewModel? parent)
    {
        NodeViewModel vm;
        if (node is GroupNode g)
        {
            var gvm = new GroupNodeViewModel(g, parent);
            foreach (var c in g.Children) gvm.Children.Add(CreateVm(c, gvm));
            vm = gvm;
        }
        else vm = new ServerNodeViewModel((ServerNode)node, parent, Services.Health);
        _map[node.Id] = vm;
        return vm;
    }

    public ServerNodeViewModel? FindServer(Guid id) => _map.GetValueOrDefault(id) as ServerNodeViewModel;
    public GroupNodeViewModel? FindGroup(Guid id) => _map.GetValueOrDefault(id) as GroupNodeViewModel;
    public IEnumerable<ServerNodeViewModel> AllServers() => _map.Values.OfType<ServerNodeViewModel>();
    public IEnumerable<GroupNodeViewModel> AllGroups() => _map.Values.OfType<GroupNodeViewModel>();

    private ObservableCollection<NodeViewModel> ChildrenOf(GroupNodeViewModel? g) => g?.Children ?? RootNodes;
    private List<TreeNode> ModelChildrenOf(GroupNodeViewModel? g) => g?.Group.Children ?? State.Document.Root.Children;

    private void Insert(NodeViewModel vm, GroupNodeViewModel? parent, int index = -1)
    {
        var list = ChildrenOf(parent);
        var model = ModelChildrenOf(parent);
        if (index < 0 || index > list.Count) index = list.Count;
        list.Insert(index, vm);
        model.Insert(Math.Min(index, model.Count), vm.Model);
        vm.Parent = parent;
        if (parent is not null) parent.IsExpanded = true;
        parent?.NotifyCounts();
        OnPropertyChanged(nameof(IsTreeEmpty));
    }

    private void Detach(NodeViewModel vm)
    {
        ChildrenOf(vm.Parent).Remove(vm);
        ModelChildrenOf(vm.Parent).Remove(vm.Model);
        vm.Parent?.NotifyCounts();
        OnPropertyChanged(nameof(IsTreeEmpty));
    }

    private void Changed()
    {
        State.MarkDirty();
        RefreshViewCounts();
        RefreshDashboards();
    }

    private void RefreshDashboards()
    {
        foreach (var tab in AllTabs)
            if (tab.Mode == TabMode.Dashboard) _ = tab.StartAsync();
    }

    [RelayCommand]
    private void NewServer(GroupNodeViewModel? parent)
    {
        parent ??= SelectedGroupForNew();
        var node = new ServerNode();
        var vm = new ServerEditorViewModel(this, node, parent, isNew: true);
        if (Dialogs.ShowServerEditor(Owner, vm) != true) return;
        var svm = (ServerNodeViewModel)CreateVm(node, vm.SelectedParent?.Group);
        Insert(svm, vm.SelectedParent?.Group);
        Changed();
        CheckHealth(svm);
        OpenServer(svm, OpenTarget.Current);
        SelectInTree(svm);
    }

    [RelayCommand]
    private void NewGroup(GroupNodeViewModel? parent)
    {
        parent ??= SelectedGroupForNew();
        var node = new GroupNode();
        var vm = new GroupEditorViewModel(this, node, parent, isNew: true);
        if (Dialogs.ShowGroupEditor(Owner, vm) != true) return;
        var gvm = CreateVm(node, vm.SelectedParent?.Group);
        Insert(gvm, vm.SelectedParent?.Group);
        Changed();
        SelectInTree(gvm);
    }

    public NodeViewModel? TreeSelection { get; set; }

    private GroupNodeViewModel? SelectedGroupForNew() => TreeSelection switch
    {
        GroupNodeViewModel g => g,
        ServerNodeViewModel s => s.Parent,
        _ => null,
    };

    /// <summary>Opens the properties dialog. Returns true when the user saved.</summary>
    public bool EditNode(NodeViewModel? node, bool focusCredentials = false)
    {
        if (node is null) return false;
        bool saved;
        GroupNodeViewModel? newParent;
        if (node is ServerNodeViewModel s)
        {
            var oldHost = s.Server.Host;
            var vm = new ServerEditorViewModel(this, s.Server, s.Parent, isNew: false) { FocusCredentials = focusCredentials };
            saved = Dialogs.ShowServerEditor(Owner, vm) == true;
            newParent = vm.SelectedParent?.Group;
            if (saved)
            {
                Services.Connections.Invalidate(oldHost);
                Services.Connections.Invalidate(s.Server.Host);
                s.Refresh();
                CheckHealth(s);
            }
        }
        else
        {
            var g = (GroupNodeViewModel)node;
            var vm = new GroupEditorViewModel(this, g.Group, g.Parent, isNew: false) { FocusCredentials = focusCredentials };
            saved = Dialogs.ShowGroupEditor(Owner, vm) == true;
            newParent = vm.SelectedParent?.Group;
            if (saved)
                foreach (var srv in g.AllServers()) Services.Connections.Invalidate(srv.Server.Host);
        }
        if (!saved) return false;
        if (!ReferenceEquals(newParent, node.Parent)) Move(node, newParent, -1);
        node.Refresh();
        foreach (var tab in AllTabs) tab.RefreshIdentity();
        Changed();
        return true;
    }

    [RelayCommand]
    private void Edit(NodeViewModel? node) => EditNode(node ?? TreeSelection);

    [RelayCommand]
    private void Delete(NodeViewModel? node)
    {
        node ??= TreeSelection;
        if (node is null) return;
        var message = node is GroupNodeViewModel g
            ? Loc.F("Tree.ConfirmDelete.Group", g.Name, g.ServerCount)
            : Loc.F("Tree.ConfirmDelete.Server", node.Name);
        if (!Dialogs.Confirm(Owner, Loc.T("Common.Delete"), message, Loc.T("Common.Delete"), danger: true)) return;
        Detach(node);
        foreach (var id in Descendants(node).Select(n => n.Model.Id).ToList())
        {
            if (_map.Remove(id, out var removed) && removed is ServerNodeViewModel sv) sv.Detach();
        }
        Changed();
        foreach (var tab in AllTabs) tab.RefreshIdentity();
    }

    private static IEnumerable<NodeViewModel> Descendants(NodeViewModel node)
    {
        yield return node;
        if (node is GroupNodeViewModel g)
            foreach (var c in g.Children)
                foreach (var d in Descendants(c)) yield return d;
    }

    [RelayCommand]
    private void Duplicate(NodeViewModel? node)
    {
        if (node is not ServerNodeViewModel s) return;
        var copy = new ServerNode
        {
            Name = Loc.F("Tree.CopySuffix", s.Server.Name),
            Host = s.Server.Host,
            Share = s.Server.Share,
            StartFolder = s.Server.StartFolder,
            Color = s.Server.Color,
            Description = s.Server.Description,
            Tags = s.Server.Tags.ToList(),
            Credentials = s.Server.Credentials.Clone(),
            Files = s.Server.Files,
            Desktop = s.Server.Desktop is { } d ? new DesktopEndpoint { Enabled = d.Enabled, Port = d.Port } : null,
            DefaultAction = s.Server.DefaultAction,
            DesktopCredentials = s.Server.DesktopCredentials.Clone(),
            Rdp = s.Server.Rdp.Clone(),
        };
        var vm = CreateVm(copy, s.Parent);
        Insert(vm, s.Parent, ChildrenOf(s.Parent).IndexOf(s) + 1);
        Changed();
        SelectInTree(vm);
    }

    /// <summary>Moves a node to another group (drag &amp; drop in the tree). Refuses cycles.</summary>
    public bool Move(NodeViewModel node, GroupNodeViewModel? target, int index)
    {
        if (ReferenceEquals(node, target)) return false;
        if (node is GroupNodeViewModel g && target is not null && (ReferenceEquals(g, target) || target.Ancestors().Contains(g))) return false;
        var sameParent = ReferenceEquals(node.Parent, target);
        var oldIndex = ChildrenOf(node.Parent).IndexOf(node);
        Detach(node);
        if (sameParent && index > oldIndex) index--;
        Insert(node, target, index);
        Changed();
        return true;
    }

    [RelayCommand]
    private void MoveUp(NodeViewModel? node) => Shift(node, -1);

    [RelayCommand]
    private void MoveDown(NodeViewModel? node) => Shift(node, +1);

    private void Shift(NodeViewModel? node, int delta)
    {
        if (node is null) return;
        var list = ChildrenOf(node.Parent);
        var i = list.IndexOf(node);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        list.Move(i, j);
        var model = ModelChildrenOf(node.Parent);
        model.RemoveAt(i);
        model.Insert(j, node.Model);
        Changed();
    }

    public void SelectInTree(NodeViewModel? node)
    {
        if (node is null) return;
        foreach (var a in node.Ancestors()) a.IsExpanded = true;
        node.IsSelected = true;
        RevealInTree?.Invoke(this, node);
    }

    /// <summary>The tree should scroll to this node (it may be virtualized away and have no row yet).</summary>
    public event EventHandler<NodeViewModel>? RevealInTree;

    public void CheckHealth(ServerNodeViewModel server)
    {
        foreach (var (host, port) in server.Endpoints()) _ = Services.Health.CheckAsync(host, port);
    }

    // ───────────── Opening ─────────────
    /// <summary>Plain click: the server's default view (files or desktop).</summary>
    public void OpenServer(ServerNodeViewModel server, OpenTarget target)
    {
        if (server.DefaultAction == ServerAction.Desktop) OpenDesktop(server, target);
        else OpenFiles(server, target);
    }

    public void OpenFiles(ServerNodeViewModel server, OpenTarget target)
    {
        if (!server.HasFiles)
        {
            OpenDesktop(server, target);
            return;
        }
        Open(new FolderLocation(server.RootPath), target);
    }

    public void OpenDesktop(ServerNodeViewModel server, OpenTarget target)
    {
        if (!server.HasDesktop)
        {
            Toasts.Show(Loc.F("Desktop.NotEnabled", server.Name), ToastKind.Info, Loc.T("Tree.Ctx.Properties"), () => EditNode(server));
            return;
        }
        if (FindWindow(server) is { } window)
        {
            window.BringToFront();
            if (window.Session is { IsConnected: false, IsBusy: false } idle) idle.Connect();
            return;
        }
        // One session per server: a second embedded session with the same account would take over the first
        // (Windows: "replaced by another connection"), so an existing desktop tab is reused.
        var existing = FindTab(t => t.Current is DesktopLocation d && d.ServerId == server.Model.Id);
        if (existing is { } hit)
        {
            Reveal(hit.Pane, hit.Tab);
            if (hit.Tab.Session is { IsConnected: false, IsBusy: false } idle) idle.Connect();
            else hit.Tab.Session?.Focus();
            return;
        }
        var location = new DesktopLocation(server.Model.Id);
        switch (target)
        {
            case OpenTarget.NewTab:
                OpenInNewTab(location, ActivePane, t => t.RequestConnect());
                break;
            case OpenTarget.OtherPane:
                OpenInOtherPane(location, ActivePane, t => t.RequestConnect());
                break;
            default:
                if (ActiveTab is null || ActiveTab.Session is { IsConnected: true })
                    OpenInNewTab(location, ActivePane, t => t.RequestConnect());
                else
                {
                    ActiveTab.RequestConnect();
                    _ = ActiveTab.NavigateAsync(location);
                }
                break;
        }
    }

    /// <summary>
    /// The identity band's Files | Desktop switch: jump to the tab showing the other view of the same server
    /// (or open it next to this one). Never tears down the view you came from.
    /// </summary>
    public void OpenCompanion(BrowserTabViewModel from, ServerAction view)
    {
        if (from.Server is not { } server) return;
        var pane = from.Pane;
        // Prefer the same pane, then any other pane.
        var hit = FindTab(t => ReferenceEquals(t.Pane, pane) && IsCompanion(t))
                  ?? FindTab(IsCompanion);
        if (hit is { } h)
        {
            Reveal(h.Pane, h.Tab);
            return;
        }
        if (view == ServerAction.Desktop) OpenDesktop(server, OpenTarget.NewTab);
        else OpenInNewTab(new FolderLocation(server.RootPath), pane);

        bool IsCompanion(BrowserTabViewModel t) => view == ServerAction.Desktop
            ? t.Current is DesktopLocation d && d.ServerId == server.Model.Id
            : !ReferenceEquals(t, from) && t.Current is FolderLocation { IsUnc: true } && ReferenceEquals(t.Server, server);
    }

    /// <summary>Shows a tab wherever it lives (leaving another tab's full screen first).</summary>
    private void Reveal(PaneViewModel pane, BrowserTabViewModel tab)
    {
        if (FullScreenTab is { } fs && !ReferenceEquals(fs, tab)) ExitFullScreen();
        Activate(pane);
        pane.SelectedTab = tab;
    }

    /// <summary>
    /// One session per server: if another tab already has a session with this server connecting or connected,
    /// show that tab instead of starting a second session (it would sign the first one out).
    /// </summary>
    public bool RevealOtherSession(RdpSessionViewModel session)
    {
        if (RevealDetached(session.Server, session)) return true;
        var hit = FindTab(t => t.Session is { State: DesktopState.Connecting or DesktopState.Connected } s
                               && !ReferenceEquals(s, session) && ReferenceEquals(s.Server, session.Server));
        if (hit is not { } h) return false;
        Reveal(h.Pane, h.Tab);
        Toasts.Show(Loc.F("Desktop.AlreadyOpen", session.Server.Name));
        return true;
    }

    /// <summary>Asks once before ending the connected desktop sessions among <paramref name="tabs"/>.</summary>
    public bool ConfirmEndSessions(IEnumerable<BrowserTabViewModel> tabs)
        => ConfirmEndSessions(tabs.Select(t => t.Session).OfType<RdpSessionViewModel>());

    /// <summary>Asks once before ending the connected sessions among <paramref name="sessions"/>.</summary>
    public bool ConfirmEndSessions(IEnumerable<RdpSessionViewModel> sessions)
    {
        var live = sessions.Where(s => s.IsConnected).ToList();
        if (live.Count == 0) return true;
        var message = live.Count == 1
            ? Loc.F("Desktop.ConfirmLeave", live[0].Server.Name)
            : Loc.F("Desktop.ConfirmLeaveMany", live.Count);
        return Dialogs.Confirm(Owner, Loc.T("Desktop.Disconnect"), message, Loc.T("Desktop.Disconnect"), danger: false);
    }

    private (PaneViewModel Pane, BrowserTabViewModel Tab)? FindTab(Func<BrowserTabViewModel, bool> match)
    {
        foreach (var pane in Panes)
            foreach (var t in pane.Tabs)
                if (match(t)) return (pane, t);
        return null;
    }

    public void OpenGroup(GroupNodeViewModel group, OpenTarget target)
        => Open(new GroupLocation(group.Model.Id), target);

    public void Open(Location location, OpenTarget target)
    {
        switch (target)
        {
            case OpenTarget.NewTab:
                OpenInNewTab(location, ActivePane);
                break;
            case OpenTarget.OtherPane:
                OpenInOtherPane(location, ActivePane);
                break;
            default:
                // Never replace a live remote desktop just to look at something else: use a new tab.
                if (ActiveTab is null) ActivePane.AddTab(location);
                else if (ActiveTab.Session is { IsConnected: true } && location is not DesktopLocation) OpenInNewTab(location, ActivePane);
                else _ = ActiveTab.NavigateAsync(location);
                break;
        }
    }

    public void OpenInNewTab(Location location, PaneViewModel pane, Action<BrowserTabViewModel>? configure = null)
    {
        var index = pane.SelectedTab is null ? pane.Tabs.Count : pane.Tabs.IndexOf(pane.SelectedTab) + 1;
        pane.AddTab(location, index: index, configure: configure);
        Activate(pane);
    }

    /// <summary>Opens in the pane used before this one; with a single pane, splits it to the right.</summary>
    public void OpenInOtherPane(Location location, PaneViewModel from, Action<BrowserTabViewModel>? configure = null)
    {
        ExitFullScreen(); // the other pane is hidden while one session fills the screen
        if (OtherPane(from) is not { } other)
        {
            // No other pane yet: split to the right; if there's no room for that, at least open it here.
            if (SplitPane(from, Core.Layout.DropSide.Right, p => p.AddTab(location, configure: configure), quiet: true) is null)
                OpenInNewTab(location, from, configure);
            return;
        }
        if (other.SelectedTab is null || other.SelectedTab.Session is { IsConnected: true })
            other.AddTab(location, index: other.SelectedTab is null ? null : other.Tabs.IndexOf(other.SelectedTab) + 1, configure: configure);
        else
        {
            configure?.Invoke(other.SelectedTab);
            _ = other.SelectedTab.NavigateAsync(location);
        }
        Activate(other);
    }

    [RelayCommand]
    private void OpenLocalPlace(LocalPlace place) => Open(new FolderLocation(place.Path), OpenTarget.Current);

    [RelayCommand]
    private void OpenThisPc() => Open(new ThisPcLocation(), OpenTarget.Current);

    [RelayCommand]
    private void OpenHome() => Open(new HomeLocation(), OpenTarget.Current);

    [RelayCommand]
    private void OpenNode(NodeViewModel? node)
    {
        switch (node)
        {
            case ServerNodeViewModel s: OpenServer(s, OpenTarget.Current); break;
            case GroupNodeViewModel g: OpenGroup(g, OpenTarget.Current); break;
        }
    }

    [RelayCommand]
    private void OpenNodeInNewTab(NodeViewModel? node)
    {
        switch (node)
        {
            case ServerNodeViewModel s: OpenServer(s, OpenTarget.NewTab); break;
            case GroupNodeViewModel g: OpenGroup(g, OpenTarget.NewTab); break;
        }
    }

    [RelayCommand]
    private void OpenNodeInOtherPane(NodeViewModel? node)
    {
        switch (node)
        {
            case ServerNodeViewModel s: OpenServer(s, OpenTarget.OtherPane); break;
            case GroupNodeViewModel g: OpenGroup(g, OpenTarget.OtherPane); break;
        }
    }

    [RelayCommand]
    private void OpenNodeDesktop(NodeViewModel? node)
    {
        if (node is ServerNodeViewModel s) OpenDesktop(s, OpenTarget.Current);
    }

    [RelayCommand]
    private void OpenNodeFiles(NodeViewModel? node)
    {
        if (node is ServerNodeViewModel s) OpenFiles(s, OpenTarget.Current);
    }

    /// <summary>Ctrl+Shift+D: the desktop of the server the active tab is about.</summary>
    [RelayCommand]
    private void OpenActiveServerDesktop()
    {
        if (ActiveTab?.Server is { } s) OpenDesktop(s, OpenTarget.Current);
        else if (TreeSelection is ServerNodeViewModel t) OpenDesktop(t, OpenTarget.Current);
    }

    [RelayCommand]
    private void OpenNodeInExplorer(NodeViewModel? node)
    {
        if (node is not ServerNodeViewModel s) return;
        _ = OpenServerInExplorerAsync(s);
    }

    private async Task OpenServerInExplorerAsync(ServerNodeViewModel s)
    {
        try
        {
            // Establish the session with the stored credentials first so Explorer doesn't prompt.
            await Services.Connections.EnsureSessionAsync(s.Host, null, State.ResolveCredential(s.Model), CancellationToken.None);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{s.RootPath}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (SmbException ex)
        {
            var t = SmbErrorTranslator.Translate(ex.Code);
            Toasts.Show($"{Loc.T(t.TitleKey)} — {s.Name}", ToastKind.Error);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Toasts.Show(ex.Message, ToastKind.Error);
        }
    }

    [RelayCommand]
    private void CopyNodePath(NodeViewModel? node)
    {
        if (node is ServerNodeViewModel s && FileClipboard.SetText(s.RootPath))
            Toasts.Show(Loc.F("Toast.PathCopied", s.RootPath), ToastKind.Success);
    }

    [RelayCommand]
    private void CopyNodeHost(NodeViewModel? node)
    {
        if (node is ServerNodeViewModel s && FileClipboard.SetText(s.Host))
            Toasts.Show(Loc.F("Toast.PathCopied", s.Host), ToastKind.Success);
    }

    [RelayCommand]
    private async Task DisconnectNode(NodeViewModel? node)
    {
        if (node is not ServerNodeViewModel s) return;
        await Services.Connections.DisconnectHostAsync(s.Host);
        Toasts.Show(Loc.F("Toast.Disconnected", s.Name));
        NotifyConnectionsChanged();
    }

    // ───────────── Panes & tabs ─────────────
    partial void OnActivePaneChanged(PaneViewModel value) => OnPaneTabChanged(value);

    public void OnPaneTabChanged(PaneViewModel pane)
    {
        // Switching away from the full-screen tab (Ctrl+Tab after releasing the keyboard) leaves full screen.
        if (FullScreenTab is { } fs && ReferenceEquals(fs.Pane, pane) && !ReferenceEquals(pane.SelectedTab, fs)) SetFullScreen(fs, false);
        if (!ReferenceEquals(pane, ActivePane)) return;
        OnPropertyChanged(nameof(ActiveTab));
        OnPropertyChanged(nameof(WindowTitle));
        SyncTreeSelection();
    }

    public void NotifyActiveTabChrome(BrowserTabViewModel tab)
    {
        if (!ReferenceEquals(tab, ActiveTab)) return;
        OnPropertyChanged(nameof(WindowTitle));
        SyncTreeSelection();
    }

    private void SyncTreeSelection()
    {
        NodeViewModel? node = ActiveTab?.Current switch
        {
            GroupLocation g => FindGroup(g.GroupId),
            FolderLocation => ActiveTab.Server,
            _ => null,
        };
        if (node is not null && !node.IsSelected) SelectInTree(node);
    }

    [RelayCommand]
    private void NewTab() => ActivePane.NewTabCommand.Execute(null);

    [RelayCommand]
    private void CloseTab() => ActivePane.CloseTab(null);

    [RelayCommand]
    private void NextTab() => ActivePane.SelectNext(+1);

    [RelayCommand]
    private void PreviousTab() => ActivePane.SelectNext(-1);

    [RelayCommand]
    private void OpenPalette() => Palette.Open();

    // ───────────── Dialogs & menu ─────────────
    [RelayCommand]
    private void OpenSettings() => Dialogs.ShowSettings(Owner, new SettingsViewModel(this));

    [RelayCommand]
    private void ManageProfiles() => Dialogs.ShowProfiles(Owner, new ProfilesViewModel(this));

    public void AfterProfilesChanged()
    {
        foreach (var n in _map.Values) n.Refresh();
        foreach (var tab in AllTabs) tab.RefreshIdentity();
        foreach (var s in AllServers()) Services.Connections.Invalidate(s.Host);
        State.MarkDirty();
    }

    [RelayCommand]
    private void ImportFromWindows()
    {
        var vm = new ImportViewModel(this);
        if (Dialogs.ShowImport(Owner, vm) != true) return;
        var count = vm.Apply();
        if (count > 0) Toasts.Show(Loc.F("Toast.Imported", count), ToastKind.Success);
    }

    /// <summary>Adds servers (from net use or a file) under a group with the given name, creating it if needed.</summary>
    public GroupNodeViewModel AddImported(string groupName, IEnumerable<TreeNode> nodes)
    {
        var group = RootNodes.OfType<GroupNodeViewModel>().FirstOrDefault(g => string.Equals(g.Name, groupName, StringComparison.CurrentCultureIgnoreCase));
        if (group is null)
        {
            group = (GroupNodeViewModel)CreateVm(new GroupNode { Name = groupName }, null);
            Insert(group, null);
        }
        foreach (var n in nodes)
        {
            var vm = CreateVm(n, group);
            Insert(vm, group);
        }
        Changed();
        foreach (var s in group.AllServers()) CheckHealth(s);
        return group;
    }

    /// <summary>Opens the remote desktop of every server in a group (one tab each, reusing open ones).</summary>
    [RelayCommand]
    private void ConnectGroupDesktops(GroupNodeViewModel? group)
    {
        if (group is null) return;
        var servers = group.AllServers().Where(s => s.HasDesktop).ToList();
        if (servers.Count == 0) return;
        if (servers.Count > 8 && !Dialogs.Confirm(Owner, Loc.T("Group.ConnectDesktops"), Loc.F("Group.ConfirmConnectMany", servers.Count, group.Name),
                Loc.T("Desktop.Connect"), danger: false))
            return;
        foreach (var s in servers) OpenDesktop(s, OpenTarget.NewTab);
    }

    /// <summary>Disconnects every live desktop session of a group's servers (after one confirmation).</summary>
    [RelayCommand]
    private void DisconnectGroupDesktops(GroupNodeViewModel? group)
    {
        if (group is null) return;
        var servers = group.AllServers().ToHashSet();
        var live = AllSessions.Where(s => s.IsConnected && servers.Contains(s.Server)).ToList();
        if (live.Count == 0 || !ConfirmEndSessions(live)) return;
        foreach (var s in live) s.Disconnect();
    }

    /// <summary>Live desktop sessions among a group's servers (for its context menu).</summary>
    public int ConnectedDesktops(GroupNodeViewModel group)
    {
        var servers = group.AllServers().ToHashSet();
        return AllSessions.Count(s => s.IsConnected && servers.Contains(s.Server));
    }

    [RelayCommand]
    private void BulkAdd(GroupNodeViewModel? parent)
    {
        var count = Dialogs.ShowBulkAdd(Owner, new BulkAddViewModel(this, parent ?? SelectedGroupForNew()));
        if (count > 0) Toasts.Show(Loc.F("Toast.BulkAdded", count), ToastKind.Success);
    }

    /// <summary>Adds several new servers under one group (bulk add) and shows them.</summary>
    public void AddServers(GroupNodeViewModel? parent, IReadOnlyList<ServerNode> servers)
    {
        if (servers.Count == 0) return;
        if (parent is not null) parent.IsExpanded = true;
        ServerNodeViewModel? last = null;
        foreach (var s in servers)
        {
            last = (ServerNodeViewModel)CreateVm(s, parent);
            Insert(last, parent);
            CheckHealth(last);
        }
        Changed();
        SelectInTree(last);
    }

    /// <summary>Adds a whole imported group (keeping its own credentials and settings) at the top of the tree.</summary>
    public GroupNodeViewModel AddImportedGroup(GroupNode group, IEnumerable<SmartGroup> smartGroups)
    {
        var vm = (GroupNodeViewModel)CreateVm(group, null);
        Insert(vm, null);
        TryAddSmartGroups(smartGroups.ToList());
        Changed();
        foreach (var s in vm.AllServers()) CheckHealth(s);
        SelectInTree(vm);
        OpenGroup(vm, OpenTarget.NewTab); // show what arrived
        return vm;
    }

    [RelayCommand]
    private void ImportRdg()
    {
        var dlg = new OpenFileDialog { Filter = "Remote Desktop Connection Manager (*.rdg)|*.rdg", CheckFileExists = true };
        if (dlg.ShowDialog(Owner) != true) return;
        if (RdgImportViewModel.Load(this, dlg.FileName) is not { } preview) return;
        if (Dialogs.ShowRdgImport(Owner, preview) != true) return;
        var count = preview.Apply();
        Toasts.Show(Loc.F("Toast.Imported", count), ToastKind.Success);
    }

    [RelayCommand]
    private void ImportFile()
    {
        var dlg = new OpenFileDialog { Filter = "Remote Directory Manager (*.rdm)|*.rdm|JSON (*.json)|*.json", CheckFileExists = true };
        if (dlg.ShowDialog(Owner) != true) return;
        var result = ConnectionStore.Load(dlg.FileName);
        if (result.Outcome is LoadOutcome.Corrupt or LoadOutcome.Missing)
        {
            Toasts.Show(Loc.F("File.OperationFailed", result.Error ?? dlg.FileName), ToastKind.Error);
            return;
        }
        var doc = result.Value;
        ConnectionStore.ReassignIds(doc);
        // Imported DPAPI blobs from another PC/user simply won't decrypt; the editor will ask to re-enter.
        State.Document.Profiles.AddRange(doc.Profiles);
        var name = Path.GetFileNameWithoutExtension(dlg.FileName);
        var count = TreeIndex.AllServers(doc.Root).Count();
        AddImported(name, doc.Root.Children);
        TryAddSmartGroups(doc.SmartGroups);
        RefreshViewCounts();
        Toasts.Show(Loc.F("Toast.Imported", count), ToastKind.Success);
    }

    [RelayCommand]
    private void ExportFile()
    {
        var dlg = new SaveFileDialog { Filter = "Remote Directory Manager (*.rdm)|*.rdm", FileName = "connections.rdm", AddExtension = true };
        if (dlg.ShowDialog(Owner) != true) return;
        var includeSecrets = Dialogs.Confirm(Owner, Loc.T("Menu.ExportFile"), Loc.T("Import.ExportSecrets"), Loc.T("Common.Yes"), danger: false, cancelText: Loc.T("Common.No"));
        try
        {
            ConnectionStore.Save(dlg.FileName, ConnectionStore.CloneForExport(State.Document, includeSecrets));
            Toasts.Show(Loc.T("Toast.Exported"), ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toasts.Show(Loc.F("File.OperationFailed", ex.Message), ToastKind.Error);
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{State.Paths.DataDirectory}\"") { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception ex) { Toasts.Show(ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private async Task CheckUpdates()
    {
        Toasts.Show(Loc.T("Update.Checking"));
        await Updates.CheckAsync(userInitiated: true);
        switch (Updates.Status)
        {
            case UpdateStatus.UpToDate: Toasts.Show(Loc.F("Update.UpToDate", UpdateService.CurrentVersionText), ToastKind.Success); break;
            case UpdateStatus.DevBuild: Toasts.Show(Loc.T("Update.DevBuild")); break;
            case UpdateStatus.Failed: Toasts.Show(Loc.F("Update.Failed", Updates.LastError), ToastKind.Error); break;
        }
    }

    [RelayCommand]
    private void About() => Dialogs.ShowAbout(Owner);

    [RelayCommand]
    private void OpenReleases()
    {
        try { Process.Start(new ProcessStartInfo(UpdateService.ReleasesUrl) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public string UpdateReadyText => Updates.ReadyRelease is { } r ? Loc.F("Update.Ready", r.Version) : "";

    private void OnUpdateStatusChanged()
    {
        IsUpdateBannerVisible = Updates.Status == UpdateStatus.Ready;
        OnPropertyChanged(nameof(UpdateReadyText));
        if (Updates.Status == UpdateStatus.NotWritable && Updates.AvailableRelease is { } r)
            Toasts.Show(Loc.F("Update.NotWritable", r.Version), ToastKind.Info, Loc.T("Update.Download"), () => OpenReleasesCommand.Execute(null));
        if (Updates.Status == UpdateStatus.Failed && Updates.LastError == "SHA-256 mismatch")
            Toasts.Show(Loc.T("Update.HashMismatch"), ToastKind.Error);
    }

    [RelayCommand]
    private void RestartToUpdate()
    {
        if (Services.Operations.RunningCount > 0)
        {
            Toasts.Show(Loc.T("Update.Busy"));
            return;
        }
        if (!ConfirmEndAllSessions()) return;
        SessionsEndConfirmed = true;
        // Persist tabs/settings first (the relaunched instance restores them), but keep the app fully alive
        // until the swap succeeded: on failure the user simply keeps working.
        SaveSession();
        if (Updates.TryApply(relaunch: true))
        {
            App.SkipUpdateOnExit = true;
            Application.Current.Shutdown();
        }
        else Toasts.Show(Loc.F("Update.Failed", Updates.LastError), ToastKind.Error);
    }

    [RelayCommand]
    private void DismissUpdateBanner() => IsUpdateBannerVisible = false;

    // ───────────── Lifetime ─────────────
    public void BuildLocalPlaces()
    {
        LocalPlaces.Clear();
        void Add(string key, string? path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                LocalPlaces.Add(new LocalPlace(Loc.T(key), path, Services.Icons.ForLocalPath(path)));
        }
        Add("Tree.Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        Add("Tree.Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("Tree.Downloads", KnownFolders.Downloads);
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            string name;
            try { name = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name.TrimEnd('\\') : $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})"; }
            catch (IOException) { name = d.Name.TrimEnd('\\'); }
            LocalPlaces.Add(new LocalPlace(name, d.Name, Services.Icons.ForLocalPath(d.Name)));
        }
    }

    private void Restore(PaneViewModel pane, SavedPane? saved)
    {
        if (saved is null || saved.Tabs.Count == 0)
        {
            pane.AddTab(new HomeLocation());
            return;
        }
        BrowserTabViewModel? active = null;
        for (var i = 0; i < saved.Tabs.Count; i++)
        {
            var loc = Location.FromSaved(saved.Tabs[i]);
            if (loc is GroupLocation g && FindGroup(g.GroupId) is null) loc = new HomeLocation();
            if (loc is DesktopLocation d && FindTab(t => t.Current is DesktopLocation o && o.ServerId == d.ServerId) is not null)
                loc = new HomeLocation(); // one session per server
            // Only the active tab loads right away; the others load when first shown to avoid connecting to every server at startup.
            var tab = pane.AddTab(loc, select: i == saved.ActiveIndex);
            if (i == saved.ActiveIndex) active = tab;
        }
        pane.SelectedTab = active ?? pane.Tabs[0];
    }

    /// <summary>Set once the user agreed to end the open desktop sessions (so closing doesn't ask twice).</summary>
    public bool SessionsEndConfirmed { get; set; }

    public void PrepareShutdown()
    {
        var s = State.Settings;
        SaveSession();
        CloseDetachedWindows();
        foreach (var pane in Panes) pane.DisposeAll();
        if (s.DisconnectOnExit) Services.Connections.DisconnectAllOpenedByUs();
    }
}
