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
        Left = new PaneViewModel(this, "left") { IsActive = true };
        Right = new PaneViewModel(this, "right");
        _activePane = Left;
        Palette = new CommandPaletteViewModel(this);
        BuildTree();
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

    public PaneViewModel Left { get; }
    public PaneViewModel Right { get; }

    public ObservableCollection<NodeViewModel> RootNodes { get; } = new();
    public ObservableCollection<LocalPlace> LocalPlaces { get; } = new();

    [ObservableProperty] private bool _isSplit;
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
        RefreshDashboards();
    }

    private void RefreshDashboards()
    {
        foreach (var tab in Left.Tabs.Concat(Right.Tabs))
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
        _ = Services.Health.CheckAsync(node.Host);
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
                _ = Services.Health.CheckAsync(s.Server.Host);
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
        foreach (var tab in Left.Tabs.Concat(Right.Tabs)) tab.RefreshIdentity();
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
        foreach (var tab in Left.Tabs.Concat(Right.Tabs)) tab.RefreshIdentity();
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
    }

    // ───────────── Opening ─────────────
    public void OpenServer(ServerNodeViewModel server, OpenTarget target)
        => Open(new FolderLocation(server.RootPath), target);

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
                if (ActiveTab is null) ActivePane.AddTab(location);
                else _ = ActiveTab.NavigateAsync(location);
                break;
        }
    }

    public void OpenInNewTab(Location location, PaneViewModel pane)
    {
        var index = pane.SelectedTab is null ? pane.Tabs.Count : pane.Tabs.IndexOf(pane.SelectedTab) + 1;
        pane.AddTab(location, index: index);
        Activate(pane);
    }

    public void OpenInOtherPane(Location location, PaneViewModel from)
    {
        if (!IsSplit) ToggleSplit();
        var other = ReferenceEquals(from, Left) ? Right : Left;
        if (other.SelectedTab is null) other.AddTab(location);
        else _ = other.SelectedTab.NavigateAsync(location);
        Activate(other);
    }

    public BrowserTabViewModel? OtherPaneTab(PaneViewModel pane)
        => !IsSplit ? null : (ReferenceEquals(pane, Left) ? Right : Left).SelectedTab;

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
    }

    // ───────────── Panes & tabs ─────────────
    public void Activate(PaneViewModel pane)
    {
        if (ReferenceEquals(ActivePane, pane)) return;
        ActivePane.IsActive = false;
        ActivePane = pane;
        pane.IsActive = true;
    }

    partial void OnActivePaneChanged(PaneViewModel value) => OnPaneTabChanged(value);

    public void OnPaneTabChanged(PaneViewModel pane)
    {
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
    public void ToggleSplit()
    {
        IsSplit = !IsSplit;
        if (IsSplit)
        {
            if (Right.Tabs.Count == 0)
            {
                var saved = State.Settings.Panes.ElementAtOrDefault(1);
                if (State.Settings.RestoreTabs && saved is { Tabs.Count: > 0 }) Restore(Right, saved);
                else Right.AddTab(ActiveTab?.Current ?? new HomeLocation());
            }
            Activate(Right);
        }
        else Activate(Left);
        State.Settings.SplitView = IsSplit;
        State.MarkSettingsDirty();
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
    private void FocusOtherPane()
    {
        if (IsSplit) Activate(ReferenceEquals(ActivePane, Left) ? Right : Left);
    }

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
        foreach (var tab in Left.Tabs.Concat(Right.Tabs)) tab.RefreshIdentity();
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
        foreach (var s in group.AllServers()) _ = Services.Health.CheckAsync(s.Host);
        return group;
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

    public void RestoreTabs()
    {
        var s = State.Settings;
        var panes = s.RestoreTabs ? s.Panes : new List<SavedPane>();
        Restore(Left, panes.ElementAtOrDefault(0));
        // The right pane is only rebuilt when it will be visible; otherwise its saved tabs are kept for later
        // (see SaveSession) instead of connecting to servers nobody can see.
        if (s.SplitView && panes.Count > 1 && panes[1].Tabs.Count > 0) Restore(Right, panes[1]);
        if (s.SplitView && s.RestoreTabs)
        {
            IsSplit = true;
            if (Right.Tabs.Count == 0) Right.AddTab(new HomeLocation());
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
            // Only the active tab loads right away; the others load when first shown to avoid connecting to every server at startup.
            var tab = pane.AddTab(loc, select: i == saved.ActiveIndex);
            if (i == saved.ActiveIndex) active = tab;
        }
        pane.SelectedTab = active ?? pane.Tabs[0];
    }

    private void SaveSession()
    {
        var s = State.Settings;
        var right = Right.Tabs.Count > 0 ? Right.Save() : s.Panes.ElementAtOrDefault(1) ?? new SavedPane();
        s.Panes = new List<SavedPane> { Left.Save(), right };
        s.SplitView = IsSplit;
        State.Flush();
    }

    public void PrepareShutdown()
    {
        var s = State.Settings;
        SaveSession();
        Left.DisposeAll();
        Right.DisposeAll();
        if (s.DisconnectOnExit) Services.Connections.DisconnectAllOpenedByUs();
    }
}
