using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDMan.Core.Persistence;

namespace RDMan.ViewModels;

/// <summary>One pane of the editor grid: a strip of tabs, one visible at a time.</summary>
public sealed partial class PaneViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public PaneViewModel(MainViewModel main, string id)
    {
        _main = main;
        Id = id;
    }

    public string Id { get; }

    public ObservableCollection<BrowserTabViewModel> Tabs { get; } = new();

    [ObservableProperty] private BrowserTabViewModel? _selectedTab;
    [ObservableProperty] private bool _isActive;

    partial void OnSelectedTabChanged(BrowserTabViewModel? value)
    {
        value?.EnsureStarted(); // background tabs load lazily on first view
        foreach (var t in Tabs) t.NotifySessionVisibility();
        _main.OnPaneTabChanged(this);
    }

    public BrowserTabViewModel AddTab(Location location, bool select = true, int? index = null, Action<BrowserTabViewModel>? configure = null)
    {
        var tab = new BrowserTabViewModel(_main, this, location);
        configure?.Invoke(tab); // before selection: selecting starts the tab
        if (index is { } i && i >= 0 && i <= Tabs.Count) Tabs.Insert(i, tab);
        else Tabs.Add(tab);
        _main.RegisterTab(tab);
        if (select) SelectedTab = tab;
        return tab;
    }

    [RelayCommand]
    private void NewTab()
    {
        var index = SelectedTab is null ? Tabs.Count : Tabs.IndexOf(SelectedTab) + 1;
        AddTab(new HomeLocation(), index: index);
    }

    [RelayCommand]
    public void CloseTab(BrowserTabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab is null) return;
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        if (!tab.ConfirmLeaveDesktop()) return;
        if (Tabs.Count == 1)
        {
            // Like VS Code: the last tab of a pane closes the pane, unless it's the only pane — that one resets to Home.
            if (_main.HasMultiplePanes)
            {
                _main.ClosePane(this);
                return;
            }
            if (tab.Current is HomeLocation) return;
            tab.Session?.Disconnect(); // already confirmed above; don't ask twice
            _ = tab.NavigateAsync(new HomeLocation());
            return;
        }
        Tabs.RemoveAt(index);
        _main.ReleaseTab(tab);
        if (ReferenceEquals(SelectedTab, tab) || SelectedTab is null)
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    [RelayCommand]
    private void CloseOtherTabs(BrowserTabViewModel? keep)
    {
        keep ??= SelectedTab;
        var others = Tabs.Where(t => !ReferenceEquals(t, keep)).ToList();
        if (!_main.ConfirmEndSessions(others)) return;
        foreach (var t in others)
        {
            Tabs.Remove(t);
            _main.ReleaseTab(t);
        }
        SelectedTab = keep;
    }

    [RelayCommand]
    private void DuplicateTab(BrowserTabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab is null) return;
        // A second session with the same server would sign the first one out: duplicate its files view instead.
        var location = tab.Current is DesktopLocation
            ? tab.Server is { HasFiles: true } s ? new FolderLocation(s.RootPath) : new HomeLocation()
            : tab.Current;
        AddTab(location, index: Tabs.IndexOf(tab) + 1);
    }

    public void SelectNext(int delta)
    {
        if (Tabs.Count == 0 || SelectedTab is null) return;
        var i = (Tabs.IndexOf(SelectedTab) + delta + Tabs.Count) % Tabs.Count;
        SelectedTab = Tabs[i];
    }

    /// <summary>Whether this pane can be closed on its own (there is another pane).</summary>
    public bool CanClose => _main.HasMultiplePanes;

    /// <summary>The grid changed (panes added/removed): refresh anything that depends on it.</summary>
    public void NotifyLayout()
    {
        OnPropertyChanged(nameof(CanClose));
        foreach (var t in Tabs) t.NotifyPaneContext();
    }

    [RelayCommand]
    private void SplitRight(BrowserTabViewModel? tab) => _main.SplitWithCopy(this, Core.Layout.DropSide.Right, tab);

    [RelayCommand]
    private void SplitDown(BrowserTabViewModel? tab) => _main.SplitWithCopy(this, Core.Layout.DropSide.Bottom, tab);

    [RelayCommand]
    private void ClosePane() => _main.CloseWholePane(this);

    public SavedPane Save() => new()
    {
        Tabs = Tabs.Select(t => t.Current.ToSaved()).ToList(),
        ActiveIndex = SelectedTab is null ? 0 : Math.Max(0, Tabs.IndexOf(SelectedTab)),
    };

    public void DisposeAll()
    {
        foreach (var t in Tabs) t.Dispose();
    }
}
