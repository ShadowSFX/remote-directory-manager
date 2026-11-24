using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDMan.Core.Models;
using RDMan.Core.Persistence;
using RDMan.Core.Smb;
using RDMan.Infrastructure;
using RDMan.Services;
using RDMan.Views;

namespace RDMan.ViewModels;

/// <summary>Settings apply live (theme, language, density…) and persist immediately.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private AppSettings S => _main.State.Settings;

    public SettingsViewModel(MainViewModel main) => _main = main;

    public ThemePreference Theme
    {
        get => S.Theme;
        set
        {
            if (S.Theme == value) return;
            S.Theme = value;
            ThemeManager.Instance.Apply(value);
            Save();
        }
    }

    public string Language
    {
        get => S.Language;
        set
        {
            if (S.Language == value) return;
            S.Language = value;
            Loc.Instance.Apply(value);
            Save();
        }
    }

    public RowDensity Density
    {
        get => S.Density;
        set
        {
            if (S.Density == value) return;
            S.Density = value;
            App.ApplyDensity(value);
            Save();
        }
    }

    public bool ShowHiddenFiles { get => S.ShowHiddenFiles; set { S.ShowHiddenFiles = value; Save(); RefreshTabs(); } }
    public bool ShowHiddenShares { get => S.ShowHiddenShares; set { S.ShowHiddenShares = value; Save(); RefreshTabs(); } }
    public bool RestoreTabs { get => S.RestoreTabs; set { S.RestoreTabs = value; Save(); } }
    public bool DisconnectOnExit { get => S.DisconnectOnExit; set { S.DisconnectOnExit = value; Save(); } }
    public bool AutoUpdate { get => S.AutoUpdate; set { S.AutoUpdate = value; Save(); } }

    public int[] Intervals { get; } = [15, 30, 60, 120, 300];

    public int HealthInterval
    {
        get => S.HealthCheckIntervalSeconds;
        set
        {
            S.HealthCheckIntervalSeconds = value;
            _main.Services.Health.SetInterval(value);
            Save();
        }
    }

    public string VersionText => Loc.F("Settings.Version", UpdateService.CurrentVersionText);
    public string DataDirectory => _main.State.Paths.DataDirectory;

    private void Save()
    {
        OnPropertyChanged(string.Empty);
        _main.State.MarkSettingsDirty();
    }

    private void RefreshTabs()
    {
        foreach (var t in _main.Left.Tabs.Concat(_main.Right.Tabs).Where(t => t.Mode == TabMode.Folder))
            _ = t.Refresh();
    }

    [RelayCommand]
    private void OpenDataFolder() => _main.OpenDataFolderCommand.Execute(null);

    [RelayCommand]
    private Task CheckUpdates() => _main.CheckUpdatesCommand.ExecuteAsync(null);
}

public sealed partial class ProfileItemViewModel : ObservableObject
{
    public ProfileItemViewModel(CredentialProfile profile, int usage, bool unreadable)
    {
        Profile = profile;
        _name = profile.Name;
        _username = profile.Username;
        Usage = usage;
        PasswordUnreadable = unreadable;
        HasStoredPassword = profile.ProtectedPassword is not null;
    }

    public CredentialProfile Profile { get; }
    public int Usage { get; }
    public bool PasswordUnreadable { get; }
    public bool HasStoredPassword { get; }
    public string UsageText => Usage == 0 ? Loc.T("Profiles.Unused") : Loc.F("Profiles.InUse", Usage);

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _username;
    public string NewPassword { get; set; } = "";
    [ObservableProperty] private bool _isExpanded;
}

/// <summary>Named credentials reusable by servers and groups.</summary>
public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly List<CredentialProfile> _deleted = new();

    public ProfilesViewModel(MainViewModel main)
    {
        _main = main;
        foreach (var p in main.State.Document.Profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            Items.Add(new ProfileItemViewModel(p.Clone(), UsageOf(p.Id), p.ProtectedPassword is not null && main.State.Secrets.TryUnprotect(p.ProtectedPassword) is null));
    }

    public ObservableCollection<ProfileItemViewModel> Items { get; } = new();
    public bool IsEmpty => Items.Count == 0;
    [ObservableProperty] private string? _validationError;

    private int UsageOf(Guid id)
    {
        var count = 0;
        void Walk(TreeNode n)
        {
            if (n.Credentials.Mode == CredentialMode.Profile && n.Credentials.ProfileId == id) count++;
            if (n is GroupNode g) foreach (var c in g.Children) Walk(c);
        }
        foreach (var c in _main.State.Document.Root.Children) Walk(c);
        return count;
    }

    [RelayCommand]
    private void Add()
    {
        var item = new ProfileItemViewModel(new CredentialProfile { Name = "", Username = "" }, 0, false) { IsExpanded = true };
        Items.Add(item);
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void Remove(ProfileItemViewModel item)
    {
        if (item.Usage > 0 && !Dialogs.Confirm(null, Loc.T("Common.Delete"), Loc.F("Profiles.ConfirmDelete", item.Name, item.Usage), Loc.T("Common.Delete"), danger: true))
            return;
        Items.Remove(item);
        _deleted.Add(item.Profile);
        OnPropertyChanged(nameof(IsEmpty));
    }

    public bool TryApply()
    {
        foreach (var i in Items)
        {
            if (string.IsNullOrWhiteSpace(i.Name) || string.IsNullOrWhiteSpace(i.Username))
            {
                ValidationError = Loc.T("Profiles.Name") + " / " + Loc.T("Cred.Username");
                i.IsExpanded = true;
                return false;
            }
        }
        var doc = _main.State.Document;
        var secrets = _main.State.Secrets;
        foreach (var d in _deleted)
        {
            doc.Profiles.RemoveAll(p => p.Id == d.Id);
            ClearReferences(doc.Root, d.Id);
        }
        foreach (var i in Items)
        {
            var target = doc.Profiles.FirstOrDefault(p => p.Id == i.Profile.Id);
            if (target is null)
            {
                target = new CredentialProfile { Id = i.Profile.Id };
                doc.Profiles.Add(target);
            }
            target.Name = i.Name.Trim();
            target.Username = i.Username.Trim();
            if (i.NewPassword.Length > 0) target.ProtectedPassword = secrets.Protect(i.NewPassword);
            else if (target.ProtectedPassword is null) target.ProtectedPassword = secrets.Protect("");
            i.NewPassword = "";
        }
        _main.AfterProfilesChanged();
        return true;
    }

    private static void ClearReferences(TreeNode node, Guid id)
    {
        if (node.Credentials.Mode == CredentialMode.Profile && node.Credentials.ProfileId == id)
            node.Credentials = new CredentialSettings { Mode = CredentialMode.Inherit };
        if (node is GroupNode g) foreach (var c in g.Children) ClearReferences(c, id);
    }
}

public sealed partial class ImportRowViewModel : ObservableObject
{
    public ImportRowViewModel(SmbTarget target, string? drive, bool alreadyAdded)
    {
        Target = target;
        Drive = drive;
        AlreadyAdded = alreadyAdded;
        _isSelected = !alreadyAdded;
    }

    public SmbTarget Target { get; }
    public string? Drive { get; }
    public bool AlreadyAdded { get; }
    public bool CanSelect => !AlreadyAdded;
    public string Path => Target.FullPath;
    public string Detail => AlreadyAdded ? Loc.T("Import.AlreadyAdded") : Drive is null ? "" : Loc.F("Import.Drive", Drive);

    [ObservableProperty] private bool _isSelected;
}

/// <summary>Imports the SMB connections Windows already knows (net use: connected + remembered).</summary>
public sealed partial class ImportViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public ImportViewModel(MainViewModel main)
    {
        _main = main;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ConnectionService.EnumerateWindowsConnections(remembered: true))
        {
            if (!UncPath.TryParse(c.RemoteName, out var t, out _) || t is null || t.Share is null) continue;
            if (string.Equals(t.Share, "IPC$", StringComparison.OrdinalIgnoreCase)) continue;
            var key = new SmbTarget(t.Host, t.Share, null).FullPath;
            if (!seen.Add(key)) continue;
            var exists = main.AllServers().Any(s => UncPath.SameHost(s.Host, t.Host) && string.Equals(s.Share ?? "", t.Share, StringComparison.OrdinalIgnoreCase));
            var row = new ImportRowViewModel(new SmbTarget(t.Host, t.Share, null), c.LocalName, exists);
            row.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(ConfirmText));
                OnPropertyChanged(nameof(HasSelection));
            };
            Rows.Add(row);
        }
    }

    public ObservableCollection<ImportRowViewModel> Rows { get; } = new();
    public bool IsEmpty => Rows.Count == 0;
    public int SelectedCount => Rows.Count(r => r.IsSelected && r.CanSelect);
    public string ConfirmText => Loc.F("Import.Confirm", SelectedCount);
    public bool HasSelection => SelectedCount > 0;

    public int Apply()
    {
        var nodes = Rows.Where(r => r.IsSelected && r.CanSelect).Select(r => (TreeNode)new ServerNode
        {
            Name = $"{r.Target.Share} · {r.Target.Host}",
            Host = r.Target.Host,
            Share = r.Target.Share,
            Credentials = new CredentialSettings { Mode = CredentialMode.Inherit },
        }).ToList();
        if (nodes.Count == 0) return 0;
        _main.AddImported(Loc.T("Import.GroupName"), nodes);
        return nodes.Count;
    }
}
