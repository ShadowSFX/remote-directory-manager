using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDMan.Core.Credentials;
using RDMan.Core.Models;
using RDMan.Core.Search;
using RDMan.Core.Smb;
using RDMan.Infrastructure;
using RDMan.Interop;
using RDMan.Services;
using RDMan.Views;

namespace RDMan.ViewModels;

public enum TabMode { Dashboard, Folder, Error, Desktop }

public sealed record BreadcrumbSegment(string Label, Location Target, bool IsServer, Brush? Color);

public sealed partial class ErrorInfo : ObservableObject
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string CodeText { get; init; }
    public required ErrorAction Action { get; init; }
    public string? PrimaryText { get; init; }
    public bool HasPrimary => !string.IsNullOrEmpty(PrimaryText);
}

public sealed partial class BrowserTabViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly AppServices _svc;
    private readonly Stack<Location> _back = new();
    private readonly Stack<Location> _forward = new();
    private ObservableCollection<FileItemViewModel> _items = new();
    private readonly DispatcherTimer _watchDebounce;
    private readonly DispatcherTimer _loadingDelay;
    private CancellationTokenSource? _loadCts;
    private FileSystemWatcher? _watcher;
    private string _foldedFilter = "";
    private bool _sessionUp;
    private bool _disposed;

    public BrowserTabViewModel(MainViewModel main, PaneViewModel pane, Location start)
    {
        _main = main;
        _svc = main.Services;
        Pane = pane;
        _current = start;
        _itemsView = CreateView(_items);
        _watchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _watchDebounce.Tick += async (_, _) => { _watchDebounce.Stop(); await ReloadInPlaceAsync(); };
        _loadingDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _loadingDelay.Tick += (_, _) => { _loadingDelay.Stop(); ShowSpinner = IsLoading; };
        _svc.Clipboard.Changed += OnClipboardChanged;
        _svc.Operations.Completed += OnOperationCompleted;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    private PaneViewModel _pane = null!;

    /// <summary>The pane showing this tab; changes when the tab is dragged to another pane.</summary>
    public PaneViewModel Pane
    {
        get => _pane;
        set
        {
            if (ReferenceEquals(_pane, value)) return;
            _pane = value;
            OnPropertyChanged();
            NotifyPaneContext();
        }
    }

    // ───────────── State ─────────────
    [ObservableProperty] private Location _current;
    [ObservableProperty] private TabMode _mode = TabMode.Dashboard;
    [ObservableProperty] private bool _isLoading;

    /// <summary>This tab's session fills the monitor (RDMan hides every other piece of chrome).</summary>
    [ObservableProperty] private bool _isFullScreen;
    [ObservableProperty] private bool _showSpinner;
    [ObservableProperty] private string _loadingText = "";
    [ObservableProperty] private ErrorInfo? _error;
    [ObservableProperty] private DashboardViewModel? _dashboard;
    [ObservableProperty] private ServerNodeViewModel? _server;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _isEditingPath;
    [ObservableProperty] private string _pathText = "";
    [ObservableProperty] private string _connectedAs = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _selectionText = "";
    [ObservableProperty] private bool _isFilterOpen;

    private string _sortColumn = "Name";
    private bool _sortDescending;

    private ListCollectionView _itemsView;

    /// <summary>Sorted + filtered view over the folder contents. Replaced wholesale on navigation (one sort instead of N inserts).</summary>
    public ListCollectionView Items => _itemsView;

    private ListCollectionView CreateView(ObservableCollection<FileItemViewModel> source)
    {
        var view = new ListCollectionView(source)
        {
            CustomSort = new FileItemComparer(_sortColumn, _sortDescending),
            Filter = o => _foldedFilter.Length == 0 || ConnectionSearch.Fold(((FileItemViewModel)o).Name).Contains(_foldedFilter, StringComparison.Ordinal),
        };
        return view;
    }
    public ObservableCollection<BreadcrumbSegment> Breadcrumb { get; } = new();
    public IReadOnlyList<FileItemViewModel> Selection { get; private set; } = [];

    public string SortColumn => _sortColumn;
    public bool SortDescending => _sortDescending;

    /// <summary>Raised when the view should put this item into rename mode / scroll it into view.</summary>
    public event EventHandler<FileItemViewModel>? RequestRename;
    public event EventHandler<FileItemViewModel>? RequestSelect;
    public event EventHandler? RequestFocusList;

    public FolderLocation? Folder => Current as FolderLocation;
    public bool IsLocal => Current is ThisPcLocation || Folder is { IsUnc: false };
    public bool IsVirtualFolder => Current is ThisPcLocation || Folder is { IsServerRoot: true };

    /// <summary>A real folder you can paste into / create folders in.</summary>
    public bool CanWrite => Mode == TabMode.Folder && Folder is not null && !Folder.IsServerRoot;

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public bool CanGoUp => ParentOf(Current) is not null;
    public bool HasItems => _items.Count > 0;
    public bool IsEmptyFolder => Mode == TabMode.Folder && !IsLoading && _items.Count == 0;
    public bool IsFilteredEmpty => Mode == TabMode.Folder && _items.Count > 0 && Items.Count == 0;
    public string EmptyText => Folder is { IsServerRoot: true } ? Loc.T("Folder.NoShares") : Loc.T("Folder.Empty");
    public string EmptyHint => CanWrite ? Loc.T("Folder.EmptyHint") : "";
    public string NoMatchText => Loc.F("Folder.NoMatches", Filter);

    public string Title => Current switch
    {
        HomeLocation => Loc.T("Tree.Home"),
        GroupLocation g => _main.FindGroup(g.GroupId)?.Name ?? Loc.T("Tree.Home"),
        ThisPcLocation => Loc.T("Tree.ThisPc"),
        FolderLocation f when f.IsServerRoot => Server?.Name ?? f.DisplayLeaf,
        FolderLocation f when f.Target is { SubPath: null or "" } && Server is { Share: not null } => Server.Name,
        FolderLocation f => f.DisplayLeaf,
        DesktopLocation => Server?.Name ?? Loc.T("Cap.Desktop"),
        _ => "",
    };

    public string Glyph => Current switch
    {
        HomeLocation => "",
        GroupLocation => "",
        ThisPcLocation => "",
        FolderLocation { IsUnc: true } => "",
        DesktopLocation => "",
        _ => "",
    };

    public Brush? IdentityBrush => Server?.ColorBrush;
    public bool HasIdentity => Server is not null || Folder is { IsUnc: true };
    public bool IsDesktop => Current is DesktopLocation;

    /// <summary>The server offers both views: the identity band shows the Files | Desktop switch.</summary>
    public bool ShowViewSwitch => Server is { HasBoth: true } && (IsDesktop || Folder is { IsUnc: true });
    public string IdentityName => Server?.Name ?? Folder?.Target?.Host ?? "";
    public string IdentityAddress => Current is DesktopLocation && Server is { } ds
        ? $"{ds.Host}:{ds.DesktopPort}"
        : Folder?.Target is { } t ? (t.Share is null ? t.ServerRoot : t.ShareRoot!) : "";

    public HostHealth? Health => Current is DesktopLocation ? Server?.DesktopHealth : Folder?.Target is { } t ? _svc.Health.Get(t.Host) : null;

    /// <summary>The embedded remote desktop of this tab (only in Desktop mode).</summary>
    [ObservableProperty] private RdpSessionViewModel? _session;
    private bool _connectOnLoad;

    /// <summary>Next load of a desktop location connects right away (explicit "open desktop"); restored tabs wait for a click.</summary>
    public void RequestConnect() => _connectOnLoad = true;

    /// <summary>The native session surface is shown only for the selected tab of its pane, in desktop mode, while connecting/connected.</summary>
    public bool IsSessionVisible => Mode == TabMode.Desktop && ReferenceEquals(Pane.SelectedTab, this) && Session is { IsSurfaceVisible: true };

    public void NotifySessionVisibility() => OnPropertyChanged(nameof(IsSessionVisible));

    /// <summary>Raised when the user releases the keyboard from the session (Ctrl+Alt+←/→).</summary>
    public event EventHandler? SessionFocusReleased;

    [RelayCommand]
    private void ShowFiles() => _main.OpenCompanion(this, ServerAction.Files);

    [RelayCommand]
    private void ShowDesktop() => _main.OpenCompanion(this, ServerAction.Desktop);

    public string StatusText
    {
        get
        {
            if (Mode != TabMode.Folder) return "";
            var total = _items.Count;
            var shown = Items.Count;
            var baseText = total == 1 ? Loc.T("Status.Item") : Loc.F("Status.Items", total);
            if (_foldedFilter.Length > 0) baseText = Loc.F("Status.Filtered", shown, total);
            return baseText;
        }
    }

    // ───────────── Navigation ─────────────
    public Task NavigateAsync(Location target, bool pushHistory = true)
    {
        if (!Equals(target, Current) && !ConfirmLeaveDesktop()) return Task.CompletedTask;
        if (pushHistory && !Equals(target, Current))
        {
            _back.Push(Current);
            _forward.Clear();
        }
        Current = target;
        _started = true;
        return LoadAsync();
    }

    [RelayCommand]
    private async Task Back()
    {
        if (_back.Count == 0 || !ConfirmLeaveDesktop()) return;
        _forward.Push(Current);
        Current = _back.Pop();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task Forward()
    {
        if (_forward.Count == 0 || !ConfirmLeaveDesktop()) return;
        _back.Push(Current);
        Current = _forward.Pop();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task Up()
    {
        var parent = ParentOf(Current);
        if (parent is null) return;
        var leaf = Folder?.Path;
        await NavigateAsync(parent);
        // Select the folder we came from, like Explorer.
        if (leaf is not null && _items.FirstOrDefault(i => string.Equals(i.FullPath, leaf, StringComparison.OrdinalIgnoreCase)) is { } came)
            RequestSelect?.Invoke(this, came);
    }

    private Location? ParentOf(Location loc) => loc switch
    {
        FolderLocation f => f.Parent() is { } p ? p : ServerGroupFor(f),
        GroupLocation g => _main.FindGroup(g.GroupId)?.Parent is { } pg ? new GroupLocation(pg.Model.Id) : new HomeLocation(),
        DesktopLocation => Server?.Parent is { } sg ? new GroupLocation(sg.Model.Id) : new HomeLocation(),
        _ => null,
    };

    /// <summary>Leaving a connected desktop ends that session: ask first (RDCMan silently keeps or drops it).</summary>
    public bool ConfirmLeaveDesktop()
    {
        if (Session is not { IsConnected: true } s) return true;
        return Dialogs.Confirm(_main.Owner, Loc.T("Desktop.Disconnect"), Loc.F("Desktop.ConfirmLeave", s.Server.Name), Loc.T("Desktop.Disconnect"), danger: false);
    }

    private Location? ServerGroupFor(FolderLocation f)
    {
        // Up from \\host goes to the server's group dashboard (or Home), keeping the tree metaphor.
        if (!f.IsServerRoot || Server is null) return null;
        return Server.Parent is { } g ? new GroupLocation(g.Model.Id) : new HomeLocation();
    }

    [RelayCommand]
    public async Task Refresh()
    {
        if (Mode == TabMode.Folder && Error is null && Folder is not null) await ReloadInPlaceAsync(force: true);
        else
        {
            if (Folder?.Target is { } t) _svc.Connections.Invalidate(t.Host);
            await LoadAsync();
        }
    }

    [RelayCommand]
    private void EditPath()
    {
        PathText = Current switch
        {
            FolderLocation f => f.Path,
            _ => "",
        };
        IsEditingPath = true;
    }

    [RelayCommand]
    private async Task CommitPath()
    {
        var text = PathText.Trim();
        IsEditingPath = false;
        if (text.Length == 0) return;
        var target = ResolveTypedPath(text);
        if (target is null)
        {
            _svc.Toasts.Show(Loc.T("Server.Validation.Address"), ToastKind.Error);
            return;
        }
        await NavigateAsync(target);
    }

    [RelayCommand]
    private void CancelPath() => IsEditingPath = false;

    private Location? ResolveTypedPath(string text)
    {
        // A friendly name typed in the bar opens that server ("prod-web").
        var byName = TreeIndexServers().FirstOrDefault(s => string.Equals(s.Name, text, StringComparison.CurrentCultureIgnoreCase));
        if (byName is not null) return new FolderLocation(byName.RootPath);
        if (text.Length >= 2 && text[1] == ':' || text.StartsWith(@"\\", StringComparison.Ordinal) && !text.StartsWith(@"\\?", StringComparison.Ordinal))
        {
            if (text.StartsWith(@"\\", StringComparison.Ordinal) && !UncPath.TryParse(text, out _, out _)) return null;
            return new FolderLocation(Environment.ExpandEnvironmentVariables(text));
        }
        if (text.Contains('%')) return new FolderLocation(Environment.ExpandEnvironmentVariables(text));
        if (UncPath.TryParse(text, out var t, out _) && t is not null) return new FolderLocation(t.FullPath);
        return null;
    }

    private IEnumerable<ServerNodeViewModel> TreeIndexServers() => _main.AllServers();

    // ───────────── Loading ─────────────
    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = _loadCts = new CancellationTokenSource();
        var ct = cts.Token;
        StopWatcher();
        Error = null;
        IsFilterOpen = false;
        Filter = "";
        Selection = [];
        UpdateSelectionText();
        _sessionUp = false;
        Server = ResolveServer();
        if (Current is not DesktopLocation) EndSession();
        UpdateConnectedAs();
        RebuildBreadcrumb();
        NotifyChrome();

        switch (Current)
        {
            case DesktopLocation:
                if (Server is null)
                {
                    // The server was deleted from the tree.
                    Current = new HomeLocation();
                    await LoadAsync();
                    return;
                }
                Dashboard = null;
                ReplaceItems([]);
                if (Session is null || !ReferenceEquals(Session.Server, Server))
                {
                    EndSession();
                    Session = new RdpSessionViewModel(_main, Server);
                    Session.PropertyChanged += OnSessionChanged;
                    Session.FocusReleased += OnSessionFocusReleased;
                    Session.Activated += OnSessionActivated;
                    Session.FullScreenRequested += OnSessionFullScreenRequested;
                }
                Mode = TabMode.Desktop;
                EndLoading();
                if (_connectOnLoad && !Session.IsConnected && !Session.IsBusy)
                {
                    _connectOnLoad = false;
                    Session.Connect();
                }
                UpdateConnectedAs();
                return;
            case HomeLocation:
                ShowDashboard(new DashboardViewModel(Loc.T("Home.Title"), null, _main.RootNodes));
                return;
            case GroupLocation g:
                var group = _main.FindGroup(g.GroupId);
                if (group is null)
                {
                    Current = new HomeLocation();
                    await LoadAsync();
                    return;
                }
                ShowDashboard(new DashboardViewModel(group.Name, group, group.Children));
                return;
            case ThisPcLocation:
                Dashboard = null;
                ReplaceItems(BuildThisPc());
                Mode = TabMode.Folder;
                EndLoading(); // a cancelled folder load may have left the spinner armed
                return;
        }

        var folder = (FolderLocation)Current;
        Dashboard = null;
        BeginLoading(folder.IsUnc ? Loc.F("Folder.Connecting", Server?.Name ?? folder.Target?.Host) : Loc.T("Folder.Reading"));
        try
        {
            var items = await FetchAsync(folder, ct);
            if (ct.IsCancellationRequested) return;
            _sessionUp = folder.IsUnc;
            UpdateConnectedAs();
            ReplaceItems(items);
            Mode = TabMode.Folder;
            StartWatcher(folder);
            if (Server is not null)
            {
                Server.Server.LastConnected = DateTimeOffset.Now;
                _svc.State.MarkDirty();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is SmbException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (ct.IsCancellationRequested) return;
            ShowError(ex);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Never leave a tab stuck: log it and show a readable error instead.
            App.Log(ex);
            ShowError(ex);
        }
        finally
        {
            if (!ct.IsCancellationRequested) EndLoading();
        }
    }

    private void ShowDashboard(DashboardViewModel dashboard)
    {
        ReplaceItems([]);
        Dashboard = dashboard;
        Mode = TabMode.Dashboard;
        EndLoading();
        NotifyChrome();
    }

    private void BeginLoading(string text)
    {
        LoadingText = text;
        IsLoading = true;
        _loadingDelay.Stop();
        _loadingDelay.Start();
        NotifyChrome();
    }

    private void EndLoading()
    {
        _loadingDelay.Stop();
        IsLoading = false;
        ShowSpinner = false;
        NotifyChrome();
    }

    private async Task<List<FileItemViewModel>> FetchAsync(FolderLocation folder, CancellationToken ct)
    {
        var target = folder.Target;
        if (target is not null)
        {
            var cred = Server is not null
                ? _svc.State.ResolveCredential(Server.Model)
                : new EffectiveCredential(EffectiveCredentialKind.CurrentUser, null, null, null, null);
            await _svc.Connections.EnsureSessionAsync(target.Host, null, cred, ct);
            if (target.Share is null)
            {
                LoadingText = Loc.T("Folder.Reading");
                var shares = await _svc.Connections.ListSharesAsync(target.Host, _svc.State.Settings.ShowHiddenShares, ct);
                var folderIcon = _svc.Icons.ForFolder().Icon;
                return shares.Select(s => new FileItemViewModel(FileItemKind.Share, s.Name, $@"{target.ServerRoot}\{s.Name}", -1, null,
                    Loc.T("Type.Share"), folderIcon, s.IsHidden, s.Remark)).ToList();
            }
        }
        LoadingText = Loc.T("Folder.Reading");
        var entries = await FileSystemService.ListAsync(folder.Path, _svc.State.Settings.ShowHiddenFiles, ct);
        return entries.Select(ToItem).ToList();
    }

    private FileItemViewModel ToItem(FsEntry e)
    {
        var (icon, type) = e.IsDirectory ? _svc.Icons.ForFolder() : _svc.Icons.ForFile(e.Name);
        if (string.IsNullOrEmpty(type))
            type = e.IsDirectory ? Loc.T("Type.Folder") : Path.GetExtension(e.Name) is { Length: > 1 } ext ? Loc.F("Type.FileExt", ext[1..].ToUpperInvariant()) : Loc.T("Type.File");
        var item = new FileItemViewModel(e.IsDirectory ? FileItemKind.Folder : FileItemKind.File, e.Name, e.FullPath,
            e.IsDirectory ? -1 : e.Size, e.Modified, type, icon, e.IsHidden);
        item.IsCut = _svc.Clipboard.CutPaths.Contains(e.FullPath);
        return item;
    }

    private List<FileItemViewModel> BuildThisPc()
    {
        var list = new List<FileItemViewModel>();
        void Known(Environment.SpecialFolder f, string key)
        {
            var path = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                list.Add(new FileItemViewModel(FileItemKind.KnownFolder, Loc.T(key), path, -1, null, Loc.T("Type.Folder"), _svc.Icons.ForLocalPath(path), false));
        }
        Known(Environment.SpecialFolder.Desktop, "Tree.Desktop");
        Known(Environment.SpecialFolder.MyDocuments, "Tree.Documents");
        var downloads = KnownFolders.Downloads;
        if (downloads is not null)
            list.Add(new FileItemViewModel(FileItemKind.KnownFolder, Loc.T("Tree.Downloads"), downloads, -1, null, Loc.T("Type.Folder"), _svc.Icons.ForLocalPath(downloads), false));
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady || d.DriveType == DriveType.Network) continue;
            string label;
            long free = -1;
            try
            {
                label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name.TrimEnd('\\') : $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})";
                free = d.AvailableFreeSpace;
            }
            catch (IOException) { label = d.Name.TrimEnd('\\'); }
            catch (UnauthorizedAccessException) { label = d.Name.TrimEnd('\\'); }
            list.Add(new FileItemViewModel(FileItemKind.Drive, label, d.Name, free, null, Loc.T("Type.Drive"), _svc.Icons.ForLocalPath(d.Name), false));
        }
        return list;
    }

    private void ReplaceItems(IEnumerable<FileItemViewModel> items)
    {
        _items = new ObservableCollection<FileItemViewModel>(items);
        _itemsView = CreateView(_items);
        OnPropertyChanged(nameof(Items));
        NotifyChrome();
    }

    /// <summary>Re-reads the folder and merges changes in place so scroll position and selection survive.</summary>
    private async Task ReloadInPlaceAsync(bool force = false)
    {
        if (_disposed || Folder is not { } folder || Mode != TabMode.Folder || IsLoading) return;
        if (_items.Any(i => i.IsRenaming)) { if (!force) { _watchDebounce.Start(); return; } }
        var ct = _loadCts?.Token ?? CancellationToken.None;
        try
        {
            if (force && folder.Target is { } t) _ = _svc.Health.CheckAsync(t.Host);
            var fresh = await FetchAsync(folder, ct);
            if (ct.IsCancellationRequested || !Equals(folder, Current)) return;
            // Samba / case-sensitive NTFS folders may hold "a.txt" and "A.txt": never assume unique keys.
            var byPath = new Dictionary<string, FileItemViewModel>(StringComparer.Ordinal);
            foreach (var f in fresh) byPath.TryAdd(f.FullPath, f);
            for (var i = _items.Count - 1; i >= 0; i--)
                if (!byPath.ContainsKey(_items[i].FullPath)) _items.RemoveAt(i);
            var existing = new Dictionary<string, FileItemViewModel>(StringComparer.Ordinal);
            foreach (var i in _items) existing.TryAdd(i.FullPath, i);
            foreach (var f in fresh)
            {
                if (existing.TryGetValue(f.FullPath, out var current))
                {
                    current.Size = f.Size;
                    current.Modified = f.Modified;
                }
                else _items.Add(f);
            }
            Items.Refresh();
            NotifyChrome();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is SmbException or IOException or UnauthorizedAccessException)
        {
            if (force) ShowError(ex);
        }
    }

    private void ShowError(Exception ex)
    {
        var code = ex switch
        {
            SmbException s => s.Code,
            UnauthorizedAccessException => SmbErrorTranslator.ErrorAccessDenied,
            DirectoryNotFoundException => SmbErrorTranslator.ErrorPathNotFound,
            _ => SmbErrorTranslator.Win32CodeFromHResult(ex.HResult),
        };
        var t = SmbErrorTranslator.Translate(code);
        var primary = t.PrimaryAction switch
        {
            ErrorAction.CloseExistingSessions => Loc.T("Err.Conflict.Action"),
            ErrorAction.EnableGuestOrUseCredentials when Server is not null => Loc.T("Err.GuestBlocked.Action"),
            ErrorAction.EditCredentials when Server is not null => Loc.T("Err.EditCredentials"),
            _ => null,
        };
        var systemMessage = code == SmbErrorTranslator.PortUnreachable ? "TCP 445" : new System.ComponentModel.Win32Exception(code).Message;
        Error = new ErrorInfo
        {
            Title = Loc.T(t.TitleKey),
            Detail = Loc.T(t.DetailKey),
            CodeText = code == SmbErrorTranslator.PortUnreachable ? $"{Folder?.Target?.Host}:445" : Loc.F("Err.Code", code, systemMessage),
            Action = t.PrimaryAction,
            PrimaryText = primary,
        };
        ReplaceItems([]);
        Mode = TabMode.Error;
        NotifyChrome();
    }

    [RelayCommand]
    private async Task ErrorPrimary()
    {
        if (Error is null) return;
        switch (Error.Action)
        {
            case ErrorAction.CloseExistingSessions when Folder?.Target is { } t:
                var closed = await _svc.Connections.DisconnectHostAsync(t.Host);
                _svc.Toasts.Show(Loc.F("Toast.Disconnected", Server?.Name ?? t.Host), ToastKind.Info);
                _ = closed;
                await LoadAsync();
                break;
            case ErrorAction.EditCredentials or ErrorAction.EnableGuestOrUseCredentials when Server is not null:
                if (_main.EditNode(Server, focusCredentials: true))
                {
                    if (Folder?.Target is { } host) _svc.Connections.Invalidate(host.Host);
                    await LoadAsync();
                }
                break;
            default:
                await LoadAsync();
                break;
        }
    }

    [RelayCommand]
    private Task Retry()
    {
        if (Folder?.Target is { } t) _svc.Connections.Invalidate(t.Host);
        return LoadAsync();
    }

    private void EndSession()
    {
        if (Session is null) return;
        Session.PropertyChanged -= OnSessionChanged;
        Session.FocusReleased -= OnSessionFocusReleased;
        Session.Activated -= OnSessionActivated;
        Session.FullScreenRequested -= OnSessionFullScreenRequested;
        if (IsFullScreen) _main.SetFullScreen(this, false);
        Session.Dispose();
        Session = null;
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RdpSessionViewModel.State) && IsFullScreen && Session is not { IsConnected: true })
            _main.SetFullScreen(this, false); // disconnected/failed: bring the window back to show what happened
        if (e.PropertyName is nameof(RdpSessionViewModel.State) or nameof(RdpSessionViewModel.ConnectedAs))
        {
            NotifySessionVisibility();
            UpdateConnectedAs();
            _main.NotifyActiveTabChrome(this);
        }
    }

    private void OnSessionFocusReleased(object? sender, EventArgs e) => SessionFocusReleased?.Invoke(this, EventArgs.Empty);

    private void OnSessionFullScreenRequested(object? sender, bool on) => _main.SetFullScreen(this, on);

    // Clicks inside the remote desktop never reach WPF's mouse events: activate the pane from the session's focus.
    private void OnSessionActivated(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(_main.ActivePane, Pane)) _main.Activate(Pane);
    }

    partial void OnModeChanged(TabMode value) => NotifySessionVisibility();

    partial void OnSessionChanged(RdpSessionViewModel? value) => NotifySessionVisibility();

    private ServerNodeViewModel? ResolveServer()
    {
        if (Current is DesktopLocation d) return _main.FindServer(d.ServerId);
        if (Folder is not { IsUnc: true } f) return null;
        var node = _svc.State.FindServerForPath(f.Path);
        return node is null ? null : _main.FindServer(node.Id);
    }

    public void RefreshIdentity()
    {
        Server = ResolveServer();
        if (Current is DesktopLocation && Server is null)
        {
            // The server was deleted: end its session and leave (LoadAsync sends a desktop without a server Home).
            EndSession();
            _ = LoadAsync();
            return;
        }
        UpdateConnectedAs();
        RebuildBreadcrumb();
        NotifyChrome();
    }

    private void UpdateConnectedAs()
    {
        if (Current is DesktopLocation)
        {
            ConnectedAs = Session is { State: DesktopState.Connected or DesktopState.Connecting } s ? s.ConnectedAs : "";
            return;
        }
        if (Folder is not { IsUnc: true })
        {
            ConnectedAs = "";
            return;
        }
        var cred = Server is not null ? _svc.State.ResolveCredential(Server.Model) : null;
        var who = cred?.Kind switch
        {
            EffectiveCredentialKind.Explicit => cred.Username!,
            EffectiveCredentialKind.Guest => Loc.T("Status.Guest"),
            _ => Loc.T("Status.WindowsUser"),
        };
        // "Connected as" only once the share actually answered; before that (or on error) it's just the credential in use.
        ConnectedAs = Loc.F(_sessionUp ? "Status.ConnectedAs" : "Status.UsingCredential", who);
    }

    private void RebuildBreadcrumb()
    {
        Breadcrumb.Clear();
        switch (Current)
        {
            case HomeLocation:
                Breadcrumb.Add(new BreadcrumbSegment(Loc.T("Tree.Home"), Current, false, null));
                break;
            case GroupLocation g:
                var group = _main.FindGroup(g.GroupId);
                Breadcrumb.Add(new BreadcrumbSegment(Loc.T("Tree.Home"), new HomeLocation(), false, null));
                if (group is not null)
                {
                    foreach (var a in group.Ancestors().Reverse())
                        Breadcrumb.Add(new BreadcrumbSegment(a.Name, new GroupLocation(a.Model.Id), false, null));
                    Breadcrumb.Add(new BreadcrumbSegment(group.Name, Current, false, null));
                }
                break;
            case ThisPcLocation:
                Breadcrumb.Add(new BreadcrumbSegment(Loc.T("Tree.ThisPc"), Current, false, null));
                break;
            case FolderLocation f when f.Target is { } t:
                Breadcrumb.Add(new BreadcrumbSegment(Server?.Name ?? t.Host, new FolderLocation(t.ServerRoot), true, Server?.ColorBrush));
                if (t.Share is not null)
                {
                    Breadcrumb.Add(new BreadcrumbSegment(t.Share, new FolderLocation(t.ShareRoot!), false, null));
                    if (!string.IsNullOrEmpty(t.SubPath))
                    {
                        var acc = t.ShareRoot!;
                        foreach (var part in t.SubPath.Split('\\'))
                        {
                            acc += "\\" + part;
                            Breadcrumb.Add(new BreadcrumbSegment(part, new FolderLocation(acc), false, null));
                        }
                    }
                }
                break;
            case FolderLocation f:
                Breadcrumb.Add(new BreadcrumbSegment(Loc.T("Tree.ThisPc"), new ThisPcLocation(), false, null));
                var root = Path.GetPathRoot(f.Path) ?? f.Path;
                Breadcrumb.Add(new BreadcrumbSegment(root.TrimEnd('\\'), new FolderLocation(root), false, null));
                var rest = f.Path.Length > root.Length ? f.Path[root.Length..] : "";
                var path = root.TrimEnd('\\');
                foreach (var part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    path += "\\" + part;
                    Breadcrumb.Add(new BreadcrumbSegment(part, new FolderLocation(path), false, null));
                }
                break;
        }
    }

    [RelayCommand]
    private Task NavigateSegment(BreadcrumbSegment segment) => NavigateAsync(segment.Target);

    private void NotifyChrome()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(IsVirtualFolder));
        OnPropertyChanged(nameof(IsEmptyFolder));
        OnPropertyChanged(nameof(IsFilteredEmpty));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(NoMatchText));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IdentityBrush));
        OnPropertyChanged(nameof(HasIdentity));
        OnPropertyChanged(nameof(IdentityName));
        OnPropertyChanged(nameof(IdentityAddress));
        OnPropertyChanged(nameof(Health));
        OnPropertyChanged(nameof(IsLocal));
        OnPropertyChanged(nameof(IsDesktop));
        OnPropertyChanged(nameof(ShowViewSwitch));
        BackCommand.NotifyCanExecuteChanged();
        _main.NotifyActiveTabChrome(this);
    }

    partial void OnServerChanged(ServerNodeViewModel? value) => OnPropertyChanged(nameof(IdentityBrush));

    // ───────────── Filter & sort ─────────────
    partial void OnFilterChanged(string value)
    {
        _foldedFilter = ConnectionSearch.Fold(value ?? "").Trim();
        Items.Refresh();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsFilteredEmpty));
        OnPropertyChanged(nameof(NoMatchText));
    }

    [RelayCommand]
    private void ToggleFilter()
    {
        IsFilterOpen = !IsFilterOpen;
        if (!IsFilterOpen) Filter = "";
    }

    [RelayCommand]
    private void SortBy(string column)
    {
        if (_sortColumn == column) _sortDescending = !_sortDescending;
        else
        {
            _sortColumn = column;
            _sortDescending = column is "Modified" or "Size"; // newest / biggest first feels natural
        }
        Items.CustomSort = new FileItemComparer(_sortColumn, _sortDescending);
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));
    }

    // ───────────── Selection ─────────────
    public void SetSelection(IEnumerable<FileItemViewModel> items)
    {
        Selection = items.ToList();
        UpdateSelectionText();
    }

    private void UpdateSelectionText()
    {
        SelectedCount = Selection.Count;
        if (Selection.Count == 0)
        {
            SelectionText = "";
            return;
        }
        var files = Selection.Where(s => s.Kind == FileItemKind.File).ToList();
        SelectionText = files.Count > 0
            ? Loc.F("Status.SelectedSize", Selection.Count, FileSystemService.FormatSize(files.Sum(f => Math.Max(0, f.Size))))
            : Loc.F("Status.Selected", Selection.Count);
    }

    // ───────────── Item actions ─────────────
    public async Task OpenItemAsync(FileItemViewModel item, bool newTab = false)
    {
        if (item.IsRenaming) return;
        if (item.IsContainer)
        {
            var loc = new FolderLocation(item.FullPath);
            if (newTab) _main.OpenInNewTab(loc, Pane);
            else await NavigateAsync(loc);
            return;
        }
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(item.FullPath) });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — user dismissed the "Open with" / security prompt.
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            _svc.Toasts.Show(Loc.F("File.OpenFailed", item.Name, ex.Message), ToastKind.Error);
        }
    }

    [RelayCommand]
    private async Task OpenSelected()
    {
        if (Selection.Count == 1) await OpenItemAsync(Selection[0]);
        else
            foreach (var s in Selection.Where(s => !s.IsContainer).Take(15))
                await OpenItemAsync(s);
    }

    [RelayCommand]
    private void OpenSelectedInNewTab()
    {
        foreach (var s in Selection.Where(s => s.IsContainer).Take(10))
            _main.OpenInNewTab(new FolderLocation(s.FullPath), Pane);
    }

    [RelayCommand]
    private void OpenSelectedInOtherPane()
    {
        var folder = Selection.FirstOrDefault(s => s.IsContainer);
        if (folder is not null) _main.OpenInOtherPane(new FolderLocation(folder.FullPath), Pane);
    }

    private List<string> SelectedRealPaths() => Selection
        .Where(s => s.Kind is FileItemKind.File or FileItemKind.Folder)
        .Select(s => s.FullPath)
        .ToList();

    [RelayCommand]
    private void Copy() => SetClipboard(ClipboardIntent.Copy);

    [RelayCommand]
    private void Cut() => SetClipboard(ClipboardIntent.Move);

    private void SetClipboard(ClipboardIntent intent)
    {
        var paths = SelectedRealPaths();
        if (paths.Count == 0) return;
        _svc.Clipboard.Set(paths, intent);
    }

    [RelayCommand]
    public async Task Paste()
    {
        if (!CanWrite || Folder is null)
        {
            _svc.Toasts.Show(Loc.T("Toast.CannotPasteHere"));
            return;
        }
        var files = _svc.Clipboard.Get();
        if (files is null)
        {
            _svc.Toasts.Show(Loc.T("Toast.NothingToPaste"));
            return;
        }
        var moved = await TransferAsync(files.Paths, Folder.Path, files.Intent == ClipboardIntent.Move);
        if (files.Intent == ClipboardIntent.Move && moved) _svc.Clipboard.ClearAfterMove();
    }

    /// <summary>
    /// Copy or move into <paramref name="destination"/> through the Windows copy engine.
    /// Returns true only when the operation ran to completion (not refused, failed or cancelled).
    /// </summary>
    public async Task<bool> TransferAsync(IReadOnlyList<string> sources, string destination, bool move)
    {
        var valid = sources.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (valid.Count == 0) return false;
        if (valid.Any(s => destination.IsUnder(s)))
        {
            _svc.Toasts.Show(Loc.T("File.CannotDropOnSelf"), ToastKind.Error);
            return false;
        }
        if (move && valid.All(s => string.Equals(s.ParentFolder(), destination.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            return false; // moving onto its own folder is a no-op, like Explorer
        var result = move
            ? await _svc.Operations.MoveAsync(_main.OwnerHandle, valid, destination)
            : await _svc.Operations.CopyAsync(_main.OwnerHandle, valid, destination);
        if (!result.Success && result.Error is not null)
            _svc.Toasts.Show(Loc.F("File.OperationFailed", result.Error), ToastKind.Error);
        return result.Success && !result.Aborted;
    }

    [RelayCommand]
    private async Task Delete(bool permanently)
    {
        var paths = SelectedRealPaths();
        if (paths.Count == 0) return;
        var result = await _svc.Operations.DeleteAsync(_main.OwnerHandle, paths, permanently);
        if (!result.Success && result.Error is not null)
            _svc.Toasts.Show(Loc.F("File.OperationFailed", result.Error), ToastKind.Error);
    }

    [RelayCommand]
    private void Rename()
    {
        var item = Selection.FirstOrDefault(s => s.Kind is FileItemKind.File or FileItemKind.Folder);
        if (item is null) return;
        foreach (var other in _items.Where(i => i.IsRenaming && !ReferenceEquals(i, item))) other.IsRenaming = false;
        item.BeginRename();
        RequestRename?.Invoke(this, item);
    }

    public async Task CommitRenameAsync(FileItemViewModel item)
    {
        if (!item.IsRenaming) return;
        item.IsRenaming = false;
        var newName = item.EditName.Trim();
        if (newName.Length == 0 || newName == item.Name) { RequestFocusList?.Invoke(this, EventArgs.Empty); return; }
        if (!UncPath.IsValidName(newName))
        {
            _svc.Toasts.Show(Loc.T("File.InvalidName"), ToastKind.Error);
            item.BeginRename();
            item.EditName = newName;
            RequestRename?.Invoke(this, item);
            return;
        }
        var result = await _svc.Operations.RenameAsync(_main.OwnerHandle, item.FullPath, newName);
        if (!result.Success && result.Error is not null)
            _svc.Toasts.Show(Loc.F("File.OperationFailed", result.Error), ToastKind.Error);
        await ReloadInPlaceAsync(force: true);
        var renamed = _items.FirstOrDefault(i => string.Equals(i.Name, newName, StringComparison.OrdinalIgnoreCase));
        if (renamed is not null) RequestSelect?.Invoke(this, renamed);
        RequestFocusList?.Invoke(this, EventArgs.Empty);
    }

    public void CancelRename(FileItemViewModel item)
    {
        item.IsRenaming = false;
        RequestFocusList?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task NewFolder()
    {
        if (!CanWrite || Folder is null) { _svc.Toasts.Show(Loc.T("File.NotInShare")); return; }
        var parent = Folder.Path;
        try
        {
            var name = await Task.Run(() =>
            {
                var n = FileSystemService.UniqueChildName(parent, Loc.T("File.NewFolderName"));
                Directory.CreateDirectory(Path.Combine(parent, n));
                return n;
            });
            await ReloadInPlaceAsync(force: true);
            var created = _items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            if (created is null) return;
            RequestSelect?.Invoke(this, created);
            created.BeginRename();
            RequestRename?.Invoke(this, created);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _svc.Toasts.Show(Loc.F("File.OperationFailed", ex.Message), ToastKind.Error);
        }
    }

    [RelayCommand]
    private void CopyPath()
    {
        var text = Selection.Count > 0
            ? string.Join(Environment.NewLine, Selection.Select(s => s.FullPath))
            : (Folder?.Path ?? "");
        if (text.Length == 0) return;
        if (FileClipboard.SetText(text)) _svc.Toasts.Show(Loc.F("Toast.PathCopied", text.Length > 80 ? text[..77] + "…" : text), ToastKind.Success);
    }

    [RelayCommand]
    private void OpenInExplorer()
    {
        var path = Folder?.Path;
        if (path is null) return;
        try
        {
            var sel = Selection.FirstOrDefault();
            var args = sel is not null && sel.Kind is FileItemKind.File or FileItemKind.Folder ? $"/select,\"{sel.FullPath}\"" : $"\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _svc.Toasts.Show(ex.Message, ToastKind.Error);
        }
    }

    [RelayCommand]
    private void Properties()
    {
        var target = Selection.FirstOrDefault()?.FullPath ?? Folder?.Path;
        if (target is null) return;
        var info = new NativeMethods.SHELLEXECUTEINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHELLEXECUTEINFO>(),
            fMask = NativeMethods.SEE_MASK_INVOKEIDLIST,
            hwnd = _main.OwnerHandle,
            lpVerb = "properties",
            lpFile = target,
            nShow = NativeMethods.SW_SHOWNORMAL,
        };
        NativeMethods.ShellExecuteEx(ref info);
    }

    [RelayCommand]
    private async Task CopyToOtherPane() => await TransferToOtherPaneAsync(move: false);

    [RelayCommand]
    private async Task MoveToOtherPane() => await TransferToOtherPaneAsync(move: true);

    private async Task TransferToOtherPaneAsync(bool move)
    {
        var other = _main.OtherPaneTab(Pane);
        if (other is null || !other.CanWrite || other.Folder is null)
        {
            _svc.Toasts.Show(Loc.T("Toast.CannotPasteHere"));
            return;
        }
        await TransferAsync(SelectedRealPaths(), other.Folder.Path, move);
    }

    public bool CanUseOtherPane => _main.OtherPaneTab(Pane)?.CanWrite == true;

    /// <summary>Title of the tab "copy/move to the other pane" would target (the pane used before this one).</summary>
    public string OtherPaneTitle => _main.OtherPaneTab(Pane)?.Title ?? "";

    /// <summary>The grid or this tab's pane changed.</summary>
    public void NotifyPaneContext()
    {
        OnPropertyChanged(nameof(CanUseOtherPane));
        OnPropertyChanged(nameof(OtherPaneTitle));
        NotifySessionVisibility();
    }

    // ───────────── Watchers & events ─────────────
    private void StartWatcher(FolderLocation folder)
    {
        if (folder.IsServerRoot) return;
        try
        {
            var w = new FileSystemWatcher(folder.Path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler h = (_, _) => Kick();
            w.Created += h;
            w.Deleted += h;
            w.Changed += h;
            w.Renamed += (_, _) => Kick();
            w.Error += (_, _) => Kick();
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // Some SMB servers don't support change notifications; F5 still works.
        }
    }

    private void Kick()
    {
        if (_disposed) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _watchDebounce.Stop();
            _watchDebounce.Start();
        });
    }

    private void StopWatcher()
    {
        _watchDebounce.Stop();
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnClipboardChanged(object? sender, EventArgs e)
    {
        var cut = _svc.Clipboard.CutPaths;
        foreach (var i in _items) i.IsCut = cut.Contains(i.FullPath);
    }

    private void OnOperationCompleted(object? sender, IReadOnlyCollection<string> folders)
    {
        // Refresh when an operation touched this folder (covers servers without change notifications).
        if (Folder is not { } f) return;
        if (!folders.Any(p => string.Equals(p.TrimEnd('\\'), f.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return;
        Kick();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RebuildBreadcrumb();
        UpdateConnectedAs();
        UpdateSelectionText();
        Dashboard?.RefreshLanguage();
        foreach (var i in _items) i.RefreshCulture();
        NotifyChrome();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        StopWatcher();
        EndSession();
        _loadingDelay.Stop();
        _watchDebounce.Stop();
        _svc.Clipboard.Changed -= OnClipboardChanged;
        _svc.Operations.Completed -= OnOperationCompleted;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>Called by the main VM when a server's settings changed (name, color, credentials).</summary>
    public void OnServerEdited(ServerNodeViewModel server)
    {
        if (ReferenceEquals(Server, server) || Server is null) RefreshIdentity();
    }

    private bool _started;

    /// <summary>Loads the tab the first time it is shown.</summary>
    public void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        _ = LoadAsync();
    }

    /// <summary>Reloads (used for dashboards after the tree changed). Only for tabs already shown.</summary>
    public Task StartAsync()
    {
        if (!_started) return Task.CompletedTask;
        return LoadAsync();
    }
}

public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string? Downloads
    {
        get
        {
            try
            {
                if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var p) == 0)
                {
                    var s = System.Runtime.InteropServices.Marshal.PtrToStringUni(p);
                    NativeMethods.CoTaskMemFree(p);
                    return s;
                }
            }
            catch (System.Runtime.InteropServices.COMException) { }
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
