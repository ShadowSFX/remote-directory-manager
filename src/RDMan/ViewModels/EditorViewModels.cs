using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDMan.Core.Credentials;
using RDMan.Core.Models;
using RDMan.Core.Smb;
using RDMan.Infrastructure;
using RDMan.Services;

namespace RDMan.ViewModels;

public sealed record GroupChoice(string Display, GroupNodeViewModel? Group, int Depth)
{
    public override string ToString() => Display;
}

public sealed record ColorChoice(string? Key, System.Windows.Media.Brush Brush, string Label);

public sealed record ProfileChoice(Guid Id, string Display);

/// <summary>The credentials block shared by the server and group editors.</summary>
public sealed partial class CredentialEditorViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly CredentialSettings _original;
    private readonly Func<EffectiveCredential> _inherited;

    public CredentialEditorViewModel(MainViewModel main, CredentialSettings original, Func<EffectiveCredential> inherited)
    {
        _main = main;
        _original = original;
        _inherited = inherited;
        _mode = original.Mode;
        _username = original.Username ?? "";
        _profileId = original.ProfileId;
        HasStoredPassword = original.Mode == CredentialMode.Custom && original.ProtectedPassword is not null;
        PasswordUnreadable = HasStoredPassword && main.State.Secrets.TryUnprotect(original.ProtectedPassword) is null;
        ReloadProfiles();
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsCustom), nameof(IsProfile), nameof(IsGuest), nameof(IsInherit), nameof(InheritText))]
    private CredentialMode _mode;

    [ObservableProperty] private string _username;
    [ObservableProperty] private Guid? _profileId;

    /// <summary>Set by the view from the PasswordBox (never bound, never kept longer than needed).</summary>
    public string NewPassword { get; set; } = "";

    public bool HasStoredPassword { get; }
    public bool PasswordUnreadable { get; }
    public string PasswordHint => HasStoredPassword && !PasswordUnreadable ? Loc.T("Cred.PasswordKeep") : "";

    public bool IsCustom => Mode == CredentialMode.Custom;
    public bool IsProfile => Mode == CredentialMode.Profile;
    public bool IsGuest => Mode == CredentialMode.Guest;
    public bool IsInherit => Mode == CredentialMode.Inherit;

    public ObservableCollection<ProfileChoice> Profiles { get; } = new();
    public bool HasProfiles => Profiles.Count > 0;

    public string InheritText
    {
        get
        {
            var eff = _inherited();
            if (eff.SourceNode is null) return Loc.T("Cred.InheritsDefault");
            var who = eff.Kind switch
            {
                EffectiveCredentialKind.Explicit => eff.Profile is not null ? $"{eff.Profile.Name} ({eff.Username})" : eff.Username!,
                EffectiveCredentialKind.Guest => Loc.T("Status.Guest"),
                _ => Loc.T("Status.WindowsUser"),
            };
            return Loc.F("Cred.InheritsFrom", who, eff.SourceNode.Name);
        }
    }

    public void RefreshInherited() => OnPropertyChanged(nameof(InheritText));

    public void ReloadProfiles()
    {
        Profiles.Clear();
        foreach (var p in _main.State.Document.Profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            Profiles.Add(new ProfileChoice(p.Id, string.IsNullOrWhiteSpace(p.Username) ? p.Name : $"{p.Name}  ·  {p.Username}"));
        if (ProfileId is { } id && Profiles.All(p => p.Id != id)) ProfileId = null;
        ProfileId ??= Profiles.FirstOrDefault()?.Id;
        OnPropertyChanged(nameof(HasProfiles));
    }

    [RelayCommand]
    private void ManageProfiles()
    {
        _main.ManageProfilesCommand.Execute(null);
        ReloadProfiles();
        RefreshInherited();
    }

    /// <summary>Validation message, or null when valid.</summary>
    public string? Validate()
    {
        if (Mode == CredentialMode.Custom && string.IsNullOrWhiteSpace(Username)) return Loc.T("Cred.Username");
        if (Mode == CredentialMode.Profile && ProfileId is null) return Loc.T("Cred.NoProfiles");
        return null;
    }

    /// <summary>Writes the edited values to <paramref name="target"/>. Secrets of other modes are dropped.</summary>
    public void ApplyTo(CredentialSettings target)
    {
        target.Mode = Mode;
        switch (Mode)
        {
            case CredentialMode.Custom:
                target.Username = Username.Trim();
                if (NewPassword.Length > 0) target.ProtectedPassword = _main.State.Secrets.Protect(NewPassword);
                else if (!HasStoredPassword || PasswordUnreadable) target.ProtectedPassword = _main.State.Secrets.Protect("");
                else target.ProtectedPassword = _original.ProtectedPassword;
                target.ProfileId = null;
                break;
            case CredentialMode.Profile:
                target.ProfileId = ProfileId;
                target.Username = null;
                target.ProtectedPassword = null;
                break;
            default:
                target.Username = null;
                target.ProtectedPassword = null;
                target.ProfileId = null;
                break;
        }
        NewPassword = "";
    }

    /// <summary>Credential to use for "Test connection" with the values currently in the form.</summary>
    public EffectiveCredential Preview()
    {
        switch (Mode)
        {
            case CredentialMode.Inherit: return _inherited();
            case CredentialMode.Guest: return new EffectiveCredential(EffectiveCredentialKind.Guest, EffectiveCredential.GuestUserName, null, null, null);
            case CredentialMode.Custom:
                var pwd = NewPassword.Length > 0 ? _main.State.Secrets.Protect(NewPassword) : HasStoredPassword && !PasswordUnreadable ? _original.ProtectedPassword : _main.State.Secrets.Protect("");
                return new EffectiveCredential(EffectiveCredentialKind.Explicit, Username.Trim(), pwd, null, null);
            case CredentialMode.Profile:
                var p = _main.State.Document.Profiles.FirstOrDefault(x => x.Id == ProfileId);
                return p is null
                    ? new EffectiveCredential(EffectiveCredentialKind.CurrentUser, null, null, null, null)
                    : new EffectiveCredential(EffectiveCredentialKind.Explicit, p.Username, p.ProtectedPassword, null, p);
            default: return new EffectiveCredential(EffectiveCredentialKind.CurrentUser, null, null, null, null);
        }
    }
}

public abstract partial class NodeEditorViewModel : ObservableObject
{
    protected NodeEditorViewModel(MainViewModel main, TreeNode node, GroupNodeViewModel? parent, bool isNew)
    {
        Main = main;
        Node = node;
        IsNew = isNew;
        _name = node.Name;
        _description = node.Description ?? "";
        _colorKey = node.Color;
        foreach (var c in GroupChoicesFor(node)) Parents.Add(c);
        _selectedParent = Parents.FirstOrDefault(p => ReferenceEquals(p.Group, parent)) ?? Parents[0];
        Credentials = new CredentialEditorViewModel(main, node.Credentials, ResolveInherited);
        Colors.Add(new ColorChoice(null, System.Windows.Media.Brushes.Transparent, Loc.T("Common.None")));
        foreach (var (key, hex) in TagColors.All)
            Colors.Add(new ColorChoice(key, NodeViewModel.BrushFor(key)!, key));
    }

    protected MainViewModel Main { get; }
    public TreeNode Node { get; }
    public bool IsNew { get; }
    public bool FocusCredentials { get; init; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string? _colorKey;
    [ObservableProperty] private GroupChoice _selectedParent;
    [ObservableProperty] private string? _validationError;

    public ObservableCollection<GroupChoice> Parents { get; } = new();
    public ObservableCollection<ColorChoice> Colors { get; } = new();
    public CredentialEditorViewModel Credentials { get; }

    partial void OnSelectedParentChanged(GroupChoice value) => Credentials.RefreshInherited();

    private EffectiveCredential ResolveInherited()
    {
        var parent = SelectedParent?.Group;
        if (parent is null) return new EffectiveCredential(EffectiveCredentialKind.CurrentUser, null, null, null, null);
        return Main.State.ResolveCredential(parent.Model);
    }

    private IEnumerable<GroupChoice> GroupChoicesFor(TreeNode editing)
    {
        yield return new GroupChoice(Loc.T("Group.Root"), null, 0);
        foreach (var c in Walk(Main.RootNodes, 0, editing)) yield return c;
    }

    private static IEnumerable<GroupChoice> Walk(IEnumerable<NodeViewModel> nodes, int depth, TreeNode editing)
    {
        foreach (var g in nodes.OfType<GroupNodeViewModel>())
        {
            if (ReferenceEquals(g.Model, editing)) continue; // a group can't live inside itself or its children
            yield return new GroupChoice(g.Name, g, depth);
            foreach (var c in Walk(g.Children, depth + 1, editing)) yield return c;
        }
    }

    protected void ApplyCommon()
    {
        Node.Name = Name.Trim();
        Node.Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        Node.Color = ColorKey;
        Credentials.ApplyTo(Node.Credentials);
    }

    public abstract bool TryApply();

    public abstract string Title { get; }

    /// <summary>Non-null only for the server editor, so server-only fields bind to nothing for groups.</summary>
    public ServerEditorViewModel? ServerFields => this as ServerEditorViewModel;

    public bool IsServer => this is ServerEditorViewModel;
}

public sealed partial class ServerEditorViewModel : NodeEditorViewModel
{
    public ServerEditorViewModel(MainViewModel main, ServerNode node, GroupNodeViewModel? parent, bool isNew)
        : base(main, node, parent, isNew)
    {
        _address = node.Host;
        _share = node.Share ?? "";
        _startFolder = node.StartFolder ?? "";
        _tags = string.Join(", ", node.Tags);
        if (isNew && parent is not null) ColorKey = parent.ColorKey;
    }

    public ServerNode Server => (ServerNode)Node;

    [ObservableProperty] private string _address;
    [ObservableProperty] private string _share;
    [ObservableProperty] private string _startFolder;
    [ObservableProperty] private string _tags;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string? _testResult;
    [ObservableProperty] private bool _testSucceeded;
    [ObservableProperty] private string? _duplicateWarning;

    public ObservableCollection<string> DiscoveredShares { get; } = new();
    public bool HasDiscoveredShares => DiscoveredShares.Count > 0;

    public override string Title => IsNew ? Loc.T("Server.TitleNew") : Loc.T("Server.TitleEdit");

    /// <summary>Pasting "\\10.0.0.5\www\sub" fills host, share and start folder in one go.</summary>
    partial void OnAddressChanged(string value)
    {
        TestResult = null;
        if (value.Contains('\\') || value.Contains('/') || value.StartsWith("smb:", StringComparison.OrdinalIgnoreCase))
        {
            if (UncPath.TryParse(value, out var t, out _) && t is not null)
            {
                _address = t.Host;
                OnPropertyChanged(nameof(Address));
                if (t.Share is not null) Share = t.Share;
                if (t.SubPath is not null) StartFolder = t.SubPath;
            }
        }
        if (string.IsNullOrWhiteSpace(Name) && UncPath.IsValidHost(_address)) { /* keep empty: the user names it */ }
        CheckDuplicate();
    }

    partial void OnShareChanged(string value)
    {
        TestResult = null;
        CheckDuplicate();
    }

    private void CheckDuplicate()
    {
        var host = Address.Trim();
        var share = string.IsNullOrWhiteSpace(Share) ? null : Share.Trim().Trim('\\', '/');
        var dup = Main.AllServers().FirstOrDefault(s => !ReferenceEquals(s.Server, Server)
            && UncPath.SameHost(s.Host, host)
            && string.Equals(s.Share ?? "", share ?? "", StringComparison.OrdinalIgnoreCase));
        DuplicateWarning = dup is null ? null : Loc.F("Server.Duplicate", dup.Name);
    }

    private string? ValidateFields(out SmbTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(Name)) return Loc.T("Server.Validation.Name");
        if (!UncPath.TryParse(Address, out var t, out var err) || t is null)
            return err == UncPath.ParseError.PortNotSupported ? Loc.T("Server.Validation.Port") : Loc.T("Server.Validation.Address");
        var share = Share.Trim().Trim('\\', '/');
        if (share.Length > 0 && !UncPath.IsValidName(share)) return Loc.T("Server.Validation.Share");
        var start = StartFolder.Trim().Trim('\\', '/').Replace('/', '\\');
        if (start.Length > 0 && start.Split('\\').Any(p => !UncPath.IsValidName(p) || p is "." or "..")) return Loc.T("Server.Validation.Share");
        target = new SmbTarget(t.Host, share.Length > 0 ? share : null, start.Length > 0 ? start : null);
        return Credentials.Validate();
    }

    public override bool TryApply()
    {
        ValidationError = ValidateFields(out var target);
        if (ValidationError is not null || target is null) return false;
        ApplyCommon();
        Server.Host = target.Host;
        Server.Share = target.Share;
        Server.StartFolder = target.Share is null ? null : target.SubPath;
        Server.Tags = Tags.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return true;
    }

    [RelayCommand]
    private async Task Test()
    {
        ValidationError = null;
        if (!UncPath.TryParse(Address, out var t, out _) || t is null)
        {
            ValidationError = Loc.T("Server.Validation.Address");
            return;
        }
        IsTesting = true;
        TestResult = null;
        try
        {
            var cred = Credentials.Preview();
            Main.Services.Connections.Invalidate(t.Host);
            await Main.Services.Connections.EnsureSessionAsync(t.Host, null, cred, CancellationToken.None);
            var shares = await Main.Services.Connections.ListSharesAsync(t.Host, Main.State.Settings.ShowHiddenShares, CancellationToken.None);
            DiscoveredShares.Clear();
            foreach (var s in shares) DiscoveredShares.Add(s.Name);
            OnPropertyChanged(nameof(HasDiscoveredShares));
            var share = Share.Trim();
            if (share.Length > 0 && !shares.Any(s => string.Equals(s.Name, share, StringComparison.OrdinalIgnoreCase)))
            {
                // Hidden shares aren't listed; verify by touching the share directly.
                await Task.Run(() => System.IO.Directory.Exists($@"\\{UncPath.ToUncHost(t.Host)}\{share}") ? 0 : throw new SmbException(SmbErrorTranslator.ErrorBadNetName));
            }
            TestSucceeded = true;
            TestResult = share.Length > 0 ? Loc.T("Server.TestOkShare") : Loc.F("Server.TestOk", shares.Count);
            _ = Main.Services.Health.CheckAsync(t.Host);
        }
        catch (SmbException ex)
        {
            TestSucceeded = false;
            var tr = SmbErrorTranslator.Translate(ex.Code);
            TestResult = $"{Loc.T(tr.TitleKey)}. {Loc.T(tr.DetailKey)}";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            TestSucceeded = false;
            TestResult = ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void PickShare(string share) => Share = share;
}

public sealed partial class GroupEditorViewModel : NodeEditorViewModel
{
    public GroupEditorViewModel(MainViewModel main, GroupNode node, GroupNodeViewModel? parent, bool isNew)
        : base(main, node, parent, isNew) { }

    public override string Title => IsNew ? Loc.T("Group.TitleNew") : Loc.T("Group.TitleEdit");

    public override bool TryApply()
    {
        ValidationError = string.IsNullOrWhiteSpace(Name) ? Loc.T("Server.Validation.Name") : Credentials.Validate();
        if (ValidationError is not null) return false;
        ApplyCommon();
        return true;
    }
}
