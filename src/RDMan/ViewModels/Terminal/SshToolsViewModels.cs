using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDMan.Core.Models;
using RDMan.Core.Ssh;
using RDMan.Infrastructure;
using RDMan.Services.Ssh;

namespace RDMan.ViewModels.Terminal;

// ───────────── SSH keys ─────────────

public sealed partial class SshKeyItemViewModel : ObservableObject
{
    public SshKeyItemViewModel(SshKeyEntry key, int usage)
    {
        Key = key;
        _name = key.Name;
        Usage = usage;
    }

    public SshKeyEntry Key { get; }

    [ObservableProperty] private string _name;

    public int Usage { get; }
    public string TypeText => $"{Fingerprint.ShortType(Key.Algorithm)} {Key.Bits}";
    public string UsageText => Usage == 0 ? Loc.T("SshKeys.Unused") : Loc.F(Usage == 1 ? "SshKeys.UsedOne" : "SshKeys.UsedMany", Usage);
    public string Details => string.Join("  ·  ", new[]
    {
        Key.Comment,
        Key.SourcePath is { } p ? Path.GetFileName(p) : null,
        Key.ProtectedPassphrase is not null ? Loc.T("SshKeys.PassphraseSaved") : null,
        Key.Certificate is not null ? Loc.T("SshKeys.Certificate") : null,
        Key.HasKeyMaterial ? null : Loc.T("SshKeys.Missing"),
    }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public bool IsMissing => !Key.HasKeyMaterial;
}

/// <summary>Private keys stored (encrypted) in RDMan: import, rename, delete.</summary>
public sealed partial class SshKeysViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public SshKeysViewModel(MainViewModel main)
    {
        _main = main;
        Reload();
    }

    public ObservableCollection<SshKeyItemViewModel> Items { get; } = new();
    public bool IsEmpty => Items.Count == 0;

    private void Reload()
    {
        Items.Clear();
        var usage = new Dictionary<Guid, int>();
        foreach (var n in Walk(_main.State.Document.Root))
            if (n.SshAuth is { Mode: SshAuthMode.Key, KeyId: { } id }) usage[id] = usage.GetValueOrDefault(id) + 1;
        foreach (var k in _main.State.Document.SshKeys.OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase))
            Items.Add(new SshKeyItemViewModel(k, usage.GetValueOrDefault(k.Id)));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static IEnumerable<TreeNode> Walk(TreeNode node)
    {
        yield return node;
        if (node is GroupNode g)
            foreach (var c in g.Children)
                foreach (var d in Walk(c)) yield return d;
    }

    [RelayCommand]
    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Term.Editor.Key.ImportTitle"),
            Filter = Loc.T("Term.Editor.Key.Filter"),
            InitialDirectory = Directory.Exists(Path.Combine(SshEnvironment.Home, ".ssh")) ? Path.Combine(SshEnvironment.Home, ".ssh") : SshEnvironment.Home,
            Multiselect = true,
        };
        if (dlg.ShowDialog(_main.Owner) != true) return;
        foreach (var file in dlg.FileNames) SshEditorViewModel.ImportKeyFile(_main, file, _main.Owner);
        Reload();
    }

    [RelayCommand]
    private void Delete(SshKeyItemViewModel? item)
    {
        if (item is null) return;
        var message = item.Usage == 0 ? Loc.F("SshKeys.ConfirmDelete", item.Name) : Loc.F("SshKeys.ConfirmDeleteUsed", item.Name, item.Usage);
        if (!Views.Dialogs.Confirm(_main.Owner, Loc.T("Common.Delete"), message, Loc.T("Common.Delete"), danger: true)) return;
        _main.State.Document.SshKeys.Remove(item.Key);
        foreach (var n in Walk(_main.State.Document.Root))
            if (n.SshAuth is { KeyId: { } id } a && id == item.Key.Id)
            {
                a.KeyId = null;
                if (a.Mode == SshAuthMode.Key) a.Mode = SshAuthMode.Inherit;
            }
        _main.State.MarkDirty();
        Reload();
    }

    /// <summary>Applies renames (empty names keep the old one; duplicates get a suffix).</summary>
    public void Save()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items)
        {
            var name = string.IsNullOrWhiteSpace(item.Name) ? item.Key.Name : item.Name.Trim();
            var unique = name;
            for (var i = 2; !used.Add(unique); i++) unique = $"{name} ({i})";
            item.Key.Name = unique;
        }
        _main.State.MarkDirty();
    }
}

// ───────────── Import from ~/.ssh/config ─────────────

public sealed partial class ConfigHostItem : ObservableObject
{
    public ConfigHostItem(string alias, SshConfigResult resolved, bool exists)
    {
        Alias = alias;
        Exists = exists;
        _isChecked = !exists;
        var user = string.IsNullOrEmpty(resolved.User) ? "" : resolved.User + "@";
        var port = resolved.Port is { } p && p != 22 ? ":" + p : "";
        Target = $"{user}{resolved.EffectiveHost}{port}";
        var extras = new List<string>();
        if (resolved.IdentityFiles.Count > 0) extras.Add(Loc.F("Term.Editor.Preview.Key", Path.GetFileName(resolved.IdentityFiles[0])));
        if (resolved.ProxyJump is { } j && !j.Equals("none", StringComparison.OrdinalIgnoreCase)) extras.Add(Loc.F("Term.Editor.Preview.Jump", j));
        if (exists) extras.Add(Loc.T("SshImport.Exists"));
        Details = string.Join("  ·  ", extras);
    }

    public string Alias { get; }
    public string Target { get; }
    public string Details { get; }
    public bool Exists { get; }
    public bool CanImport => !Exists;

    [ObservableProperty] private bool _isChecked;
}

/// <summary>Creates terminal servers for the hosts declared in ~/.ssh/config (resolved live at connect time).</summary>
public sealed partial class SshConfigImportViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public SshConfigImportViewModel(MainViewModel main)
    {
        _main = main;
        var config = SshEnvironment.SharedConfig;
        ConfigPath = SshEnvironment.ConfigPath;
        var existing = main.AllServers().Where(s => s.HasTerminal).Select(s => s.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in config.DeclaredHosts)
        {
            var r = config.Resolve(alias);
            // "Host pangya-vps 45.79.2.143" declares two names for one machine: offer the alias, not the bare IP twice.
            if (System.Net.IPAddress.TryParse(alias, out _) && Hosts.Any(h => string.Equals(h.Target.Split('@').Last(), r.EffectiveHost, StringComparison.OrdinalIgnoreCase)))
                continue;
            Hosts.Add(new ConfigHostItem(alias, r, existing.Contains(alias) || existing.Contains(r.EffectiveHost)));
        }
        Groups.Add(new GroupChoice(Loc.T("Group.Root"), null, 0));
        foreach (var g in WalkGroups(main.RootNodes, 0)) Groups.Add(g);
        SelectedGroup = Groups[0];
        Notes = string.Join("\n", config.Notes);
    }

    private static IEnumerable<GroupChoice> WalkGroups(IEnumerable<NodeViewModel> nodes, int depth)
    {
        foreach (var g in nodes.OfType<GroupNodeViewModel>())
        {
            yield return new GroupChoice(g.Name, g, depth);
            foreach (var c in WalkGroups(g.Children, depth + 1)) yield return c;
        }
    }

    public string ConfigPath { get; }
    public string Notes { get; }
    public bool HasNotes => Notes.Length > 0;
    public ObservableCollection<ConfigHostItem> Hosts { get; } = new();
    public ObservableCollection<GroupChoice> Groups { get; } = new();
    public bool IsEmpty => Hosts.Count == 0;

    [ObservableProperty] private GroupChoice _selectedGroup;

    /// <summary>Adds the checked hosts; returns how many.</summary>
    public int Import() => _main.AddSshConfigHosts(Hosts.Where(h => h.IsChecked && h.CanImport).Select(h => h.Alias).ToList(), SelectedGroup.Group);
}
