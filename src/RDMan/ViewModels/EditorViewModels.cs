using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDMan.Core.Credentials;
using RDMan.Core.Models;
using RDMan.Core.Rdp;
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

public enum EditorSection { General, Files, Desktop, Notes }

public sealed record SectionChoice(EditorSection Section, string Glyph, string Label);

/// <summary>The credentials block shared by the server and group editors (files or remote desktop).</summary>
public sealed partial class CredentialEditorViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly CredentialSettings _original;
    private readonly Func<EffectiveCredential> _inherited;
    private readonly Func<string>? _inheritText;

    public CredentialEditorViewModel(MainViewModel main, CredentialSettings original, Func<EffectiveCredential> inherited,
        bool isDesktop = false, Func<string>? inheritText = null)
    {
        _main = main;
        _original = original;
        _inherited = inherited;
        _inheritText = inheritText;
        IsDesktop = isDesktop;
        _mode = isDesktop && original.Mode == CredentialMode.Guest ? CredentialMode.Inherit : original.Mode;
        _username = original.Username ?? "";
        _profileId = original.ProfileId;
        HasStoredPassword = original.Mode == CredentialMode.Custom && original.ProtectedPassword is not null;
        PasswordUnreadable = HasStoredPassword && main.State.Secrets.TryUnprotect(original.ProtectedPassword) is null;
        ReloadProfiles();
    }

    /// <summary>Remote desktop credential: no guest; "my Windows user" means Windows' saved credential for the computer.</summary>
    public bool IsDesktop { get; }
    public bool IsFiles => !IsDesktop;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsCustom), nameof(IsProfile), nameof(IsGuest), nameof(IsInherit), nameof(IsWindows), nameof(InheritText))]
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
    public bool IsWindows => Mode == CredentialMode.CurrentUser;

    public ObservableCollection<ProfileChoice> Profiles { get; } = new();
    public bool HasProfiles => Profiles.Count > 0;

    public string InheritText
    {
        get
        {
            if (_inheritText is not null) return _inheritText();
            var eff = _inherited();
            if (eff.SourceNode is null) return Loc.T("Cred.InheritsDefault");
            return Loc.F("Cred.InheritsFrom", Describe(eff, IsDesktop), eff.SourceNode.Name);
        }
    }

    public static string Describe(EffectiveCredential eff, bool desktop) => eff.Kind switch
    {
        EffectiveCredentialKind.Explicit => eff.Profile is not null ? $"{eff.Profile.Name} ({eff.Username})" : eff.Username!,
        EffectiveCredentialKind.Guest when !desktop => Loc.T("Status.Guest"),
        _ => desktop ? Loc.T("Desktop.SavedWindowsCredential") : Loc.T("Status.WindowsUser"),
    };

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

public sealed record OptionChoice(object? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// One remote desktop option with three kinds of value: inherit (shows what and from where), or an explicit choice.
/// Replaces RDCMan's "Inherit from parent" checkbox on every settings tab.
/// </summary>
public sealed partial class RdpOptionRowViewModel : ObservableObject
{
    private readonly Func<EffectiveRdpSettings, (string Value, TreeNode? Source)> _inherited;
    private readonly Action<RdpProfile, object?> _write;

    public RdpOptionRowViewModel(string labelKey, object? current, IReadOnlyList<OptionChoice> explicitChoices,
        Func<EffectiveRdpSettings, (string Value, TreeNode? Source)> inherited, Action<RdpProfile, object?> write)
    {
        LabelKey = labelKey;
        _inherited = inherited;
        _write = write;
        Choices.Add(new OptionChoice(null, ""));
        foreach (var c in explicitChoices) Choices.Add(c);
        _selected = Choices.FirstOrDefault(c => Equals(c.Value, current)) ?? Choices[0];
    }

    public string LabelKey { get; }
    public string Label => Loc.T(LabelKey);
    public ObservableCollection<OptionChoice> Choices { get; } = new();

    [ObservableProperty] private OptionChoice _selected;

    public bool IsOverridden => Selected.Value is not null;

    partial void OnSelectedChanged(OptionChoice value) => OnPropertyChanged(nameof(IsOverridden));

    /// <summary>Rewrites the "Inherit: …" entry for the current parent.</summary>
    public void RefreshInherited(EffectiveRdpSettings parent)
    {
        var (value, source) = _inherited(parent);
        var text = source is null ? Loc.F("Rdp.Default", value) : Loc.F("Rdp.InheritedFrom", value, source.Name);
        var wasSelected = Selected.Value is null;
        Choices[0] = new OptionChoice(null, Loc.F("Rdp.InheritChoice", text));
        if (wasSelected) Selected = Choices[0];
    }

    public void ApplyTo(RdpProfile profile) => _write(profile, Selected.Value);
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
        DesktopCredentials = new CredentialEditorViewModel(main, node.DesktopCredentials, ResolveDesktopInherited, isDesktop: true, inheritText: DesktopInheritText);
        Credentials.PropertyChanged += (_, _) => DesktopCredentials.RefreshInherited();
        Colors.Add(new ColorChoice(null, System.Windows.Media.Brushes.Transparent, Loc.T("Common.None")));
        foreach (var (key, _) in TagColors.All)
            Colors.Add(new ColorChoice(key, NodeViewModel.BrushFor(key)!, key));
        BuildRdpOptions(node.Rdp ?? new RdpProfile());
        RefreshRdpInherited();
        // Placeholder until the derived editor declares its sections (AddSections selects the first one).
        _selectedSection = new SectionChoice(EditorSection.General, "", "");
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralSection), nameof(IsFilesSection), nameof(IsDesktopSection), nameof(IsNotesSection))]
    private SectionChoice _selectedSection;

    public ObservableCollection<SectionChoice> Sections { get; } = new();
    public bool IsGeneralSection => SelectedSection.Section == EditorSection.General;
    public bool IsFilesSection => SelectedSection.Section == EditorSection.Files;
    public bool IsDesktopSection => SelectedSection.Section == EditorSection.Desktop;
    public bool IsNotesSection => SelectedSection.Section == EditorSection.Notes;

    public void ShowSection(EditorSection section) => SelectedSection = Sections.First(s => s.Section == section);

    protected void AddSections(params (EditorSection Section, string Glyph, string Key)[] sections)
    {
        Sections.Clear();
        foreach (var (s, g, k) in sections) Sections.Add(new SectionChoice(s, g, Loc.T(k)));
        SelectedSection = Sections[0];
    }

    public ObservableCollection<GroupChoice> Parents { get; } = new();
    public ObservableCollection<ColorChoice> Colors { get; } = new();
    public CredentialEditorViewModel Credentials { get; }
    public CredentialEditorViewModel DesktopCredentials { get; }
    public ObservableCollection<RdpOptionRowViewModel> RdpOptions { get; } = new();

    partial void OnSelectedParentChanged(GroupChoice value)
    {
        Credentials.RefreshInherited();
        DesktopCredentials.RefreshInherited();
        RefreshRdpInherited();
    }

    private EffectiveCredential ResolveInherited()
    {
        var parent = SelectedParent?.Group;
        if (parent is null) return new EffectiveCredential(EffectiveCredentialKind.CurrentUser, null, null, null, null);
        return Main.State.ResolveCredential(parent.Model);
    }

    /// <summary>Desktop "inherit": the nearest group's desktop credential, otherwise this node's files credential (as edited).</summary>
    private EffectiveCredential ResolveDesktopInherited()
    {
        for (var g = SelectedParent?.Group; g is not null; g = g.Parent)
            if (g.Model.DesktopCredentials.Mode != CredentialMode.Inherit)
                return Main.State.ResolveDesktopCredential(g.Model);
        return Credentials.Preview();
    }

    private string DesktopInheritText()
    {
        for (var g = SelectedParent?.Group; g is not null; g = g.Parent)
            if (g.Model.DesktopCredentials.Mode != CredentialMode.Inherit)
                return Loc.F("Cred.Desktop.UsesFrom", CredentialEditorViewModel.Describe(Main.State.ResolveDesktopCredential(g.Model), true), g.Name);
        return Loc.F("Cred.Desktop.SameAsFilesUses", CredentialEditorViewModel.Describe(Credentials.Preview(), true));
    }

    private void BuildRdpOptions(RdpProfile p)
    {
        string YesNo(bool v) => v ? Loc.T("Rdp.Yes") : Loc.T("Rdp.No");
        OptionChoice[] Bools() => [new(true, Loc.T("Rdp.Yes")), new(false, Loc.T("Rdp.No"))];

        RdpOptions.Add(new RdpOptionRowViewModel("Rdp.Clipboard", p.RedirectClipboard, Bools(),
            e => (YesNo(e.RedirectClipboard.Value), e.RedirectClipboard.Source), (r, v) => r.RedirectClipboard = (bool?)v));
        RdpOptions.Add(new RdpOptionRowViewModel("Rdp.Drives", p.RedirectDrives, Bools(),
            e => (YesNo(e.RedirectDrives.Value), e.RedirectDrives.Source), (r, v) => r.RedirectDrives = (bool?)v));
        RdpOptions.Add(new RdpOptionRowViewModel("Rdp.Printers", p.RedirectPrinters, Bools(),
            e => (YesNo(e.RedirectPrinters.Value), e.RedirectPrinters.Source), (r, v) => r.RedirectPrinters = (bool?)v));
        RdpOptions.Add(new RdpOptionRowViewModel("Rdp.SmartCards", p.RedirectSmartCards, Bools(),
            e => (YesNo(e.RedirectSmartCards.Value), e.RedirectSmartCards.Source), (r, v) => r.RedirectSmartCards = (bool?)v));
        RdpOptions.Add(new RdpOptionRowViewModel("Rdp.Audio", p.Audio,
            [new(RdpAudio.Local, Loc.T("Rdp.Audio.Local")), new(RdpAudio.Remote, Loc.T("Rdp.Audio.Remote")), new(RdpAudio.Off, Loc.T("Rdp.Audio.Off"))],
            e => (Loc.T("Rdp.Audio." + e.Audio.Value), e.Audio.Source), (r, v) => r.Audio = (RdpAudio?)v));
        RdpOptions.Add(new RdpOptionRowViewModel("Rdp.WindowsKeys", p.WindowsKeysToServer, Bools(),
            e => (YesNo(e.WindowsKeysToServer.Value), e.WindowsKeysToServer.Source), (r, v) => r.WindowsKeysToServer = (bool?)v));
        RdpOptions.Add(new RdpOptionRowViewModel("Rdp.ColorDepth", p.ColorDepth,
            [new(32, "32 bits"), new(24, "24 bits"), new(16, "16 bits")],
            e => ($"{e.ColorDepth.Value} bits", e.ColorDepth.Source), (r, v) => r.ColorDepth = (int?)v));
        RdpOptions.Add(new RdpOptionRowViewModel("Rdp.Nla", p.NetworkLevelAuthentication, Bools(),
            e => (YesNo(e.NetworkLevelAuthentication.Value), e.NetworkLevelAuthentication.Source), (r, v) => r.NetworkLevelAuthentication = (bool?)v));
        RdpOptions.Add(new RdpOptionRowViewModel("Rdp.ServerCheck", p.ServerCheck,
            [new(RdpServerCheck.Warn, Loc.T("Rdp.ServerCheck.Warn")), new(RdpServerCheck.Require, Loc.T("Rdp.ServerCheck.Require")), new(RdpServerCheck.Skip, Loc.T("Rdp.ServerCheck.Skip"))],
            e => (Loc.T("Rdp.ServerCheck." + e.ServerCheck.Value), e.ServerCheck.Source), (r, v) => r.ServerCheck = (RdpServerCheck?)v));
        RdpOptions.Add(new RdpOptionRowViewModel("Rdp.Protection", p.SignInProtection,
            [new(RdpSignInProtection.Standard, Loc.T("Rdp.Protection.Standard")), new(RdpSignInProtection.RestrictedAdmin, Loc.T("Rdp.Protection.RestrictedAdmin")),
             new(RdpSignInProtection.RemoteCredentialGuard, Loc.T("Rdp.Protection.RemoteCredentialGuard"))],
            e => (Loc.T("Rdp.Protection." + e.SignInProtection.Value), e.SignInProtection.Source), (r, v) => r.SignInProtection = (RdpSignInProtection?)v));
        RdpOptions.Add(new RdpOptionRowViewModel("Rdp.AdminSession", p.AdminSession, Bools(),
            e => (YesNo(e.AdminSession.Value), e.AdminSession.Source), (r, v) => r.AdminSession = (bool?)v));
    }

    private void RefreshRdpInherited()
    {
        var parent = RdpSettingsResolver.ResolveUnder(SelectedParent?.Group?.Group, Main.State.Index);
        foreach (var row in RdpOptions) row.RefreshInherited(parent);
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
        DesktopCredentials.ApplyTo(Node.DesktopCredentials);
        var rdp = new RdpProfile();
        foreach (var row in RdpOptions) row.ApplyTo(rdp);
        Node.Rdp = rdp;
    }

    /// <summary>Sets the error and shows the section where it can be fixed.</summary>
    protected bool Fail(string message, EditorSection section)
    {
        ValidationError = message;
        ShowSection(section);
        return false;
    }

    public abstract bool TryApply();

    public abstract string Title { get; }

    /// <summary>Non-null only for the server editor, so server-only fields bind to nothing for groups.</summary>
    public ServerEditorViewModel? ServerFields => this as ServerEditorViewModel;

    public bool IsServer => this is ServerEditorViewModel;
    public bool IsGroup => !IsServer;
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
        _filesEnabled = node.Files;
        _desktopEnabled = node.HasDesktop;
        _desktopPort = (node.Desktop?.Port ?? DesktopEndpoint.DefaultPort).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _defaultAction = node.DefaultAction;
        if (isNew && parent is not null) ColorKey = parent.ColorKey;
        AddSections(
            (EditorSection.General, "\uE946", "Server.Section.General"),
            (EditorSection.Files, "\uE8B7", "Server.Section.Files"),
            (EditorSection.Desktop, "\uE7F4", "Server.Section.Desktop"),
            (EditorSection.Notes, "\uE70B", "Server.Section.Notes"));
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

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBoth))] private bool _filesEnabled;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBoth))] private bool _desktopEnabled;
    [ObservableProperty] private string _desktopPort;
    [ObservableProperty] private ServerAction _defaultAction;

    /// <summary>The "on click, open" choice only matters when the server offers both.</summary>
    public bool HasBoth => FilesEnabled && DesktopEnabled;

    public ObservableCollection<string> DiscoveredShares { get; } = new();
    public bool HasDiscoveredShares => DiscoveredShares.Count > 0;

    public override string Title => IsNew ? Loc.T("Server.TitleNew") : Loc.T("Server.TitleEdit");

    partial void OnAddressChanged(string value)
    {
        TestResult = null;
        CheckDuplicate();
    }

    /// <summary>
    /// "\\10.0.0.5\www\sub" fills host, share and start folder; "host:3390" turns on the desktop on that port.
    /// Runs on paste, when the field loses focus and before testing/saving — never per keystroke, or typing
    /// "10.0.0.5:3390" would split at "10.0.0.5:3".
    /// </summary>
    public void CommitAddress()
    {
        var value = Address;
        if (value.Contains('\\') || value.Contains('/') || value.StartsWith("smb:", StringComparison.OrdinalIgnoreCase))
        {
            if (UncPath.TryParse(value, out var t, out _) && t is not null)
            {
                Address = t.Host;
                if (t.Share is not null) Share = t.Share;
                if (t.SubPath is not null) StartFolder = t.SubPath;
            }
        }
        else if (value.Contains(':') && HostPort.TryParse(value, out var host, out var port) && port is { } p)
        {
            Address = host;
            DesktopEnabled = true;
            DesktopPort = p.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        CheckDuplicate();
    }

    partial void OnShareChanged(string value)
    {
        TestResult = null;
        CheckDuplicate();
    }

    partial void OnDesktopEnabledChanged(bool value)
    {
        if (!value && DefaultAction == ServerAction.Desktop) DefaultAction = ServerAction.Files;
        if (value && !FilesEnabled) DefaultAction = ServerAction.Desktop;
    }

    partial void OnFilesEnabledChanged(bool value)
    {
        if (!value && DesktopEnabled) DefaultAction = ServerAction.Desktop;
        if (!value && !DesktopEnabled) DesktopEnabled = true; // a server must offer something
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

    private bool TryPort(out int port)
        => int.TryParse(DesktopPort.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) && HostPort.IsValidPort(port);

    public override bool TryApply()
    {
        CommitAddress();
        ValidationError = null;
        if (string.IsNullOrWhiteSpace(Name)) return Fail(Loc.T("Server.Validation.Name"), EditorSection.General);
        if (!UncPath.TryParse(Address, out var t, out var err) || t is null)
            return Fail(err == UncPath.ParseError.PortNotSupported ? Loc.T("Server.Validation.Port") : Loc.T("Server.Validation.Address"), EditorSection.General);
        if (!FilesEnabled && !DesktopEnabled) return Fail(Loc.T("Server.Validation.NoCapability"), EditorSection.General);

        var share = Share.Trim().Trim('\\', '/');
        var start = StartFolder.Trim().Trim('\\', '/').Replace('/', '\\');
        if (FilesEnabled)
        {
            if (share.Length > 0 && !UncPath.IsValidName(share)) return Fail(Loc.T("Server.Validation.Share"), EditorSection.Files);
            if (start.Length > 0 && start.Split('\\').Any(p => !UncPath.IsValidName(p) || p is "." or ".."))
                return Fail(Loc.T("Server.Validation.Share"), EditorSection.Files);
            if (Credentials.Validate() is { } fileErr) return Fail(fileErr, EditorSection.Files);
        }
        var port = DesktopEndpoint.DefaultPort;
        if (DesktopEnabled)
        {
            if (!TryPort(out port)) return Fail(Loc.T("Server.Validation.RdpPort"), EditorSection.Desktop);
            if (DesktopCredentials.Validate() is { } deskErr) return Fail(deskErr, EditorSection.Desktop);
        }

        ApplyCommon();
        Server.Host = t.Host;
        Server.Share = share.Length > 0 ? share : null;
        Server.StartFolder = Server.Share is null || start.Length == 0 ? null : start;
        Server.Tags = Tags.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Server.Files = FilesEnabled;
        if (DesktopEnabled) Server.Desktop = new DesktopEndpoint { Enabled = true, Port = port };
        else if (Server.Desktop is not null) Server.Desktop.Enabled = false; // keep the port for when it's turned back on
        Server.DefaultAction = HasBoth ? DefaultAction : DesktopEnabled ? ServerAction.Desktop : ServerAction.Files;
        return true;
    }

    [RelayCommand]
    private async Task Test()
    {
        CommitAddress();
        ValidationError = null;
        if (!UncPath.TryParse(Address, out var t, out _) || t is null)
        {
            ValidationError = Loc.T("Server.Validation.Address");
            ShowSection(EditorSection.General);
            return;
        }
        IsTesting = true;
        TestResult = null;
        var results = new List<string>();
        var ok = true;
        try
        {
            if (DesktopEnabled && TryPort(out var port))
            {
                var latency = await ConnectionService.ProbeAsync(t.Host, port, ConnectionService.ProbeTimeout, CancellationToken.None);
                ok &= latency is not null;
                results.Add(latency is null ? Loc.F("Server.TestDesktopFail", port) : Loc.F("Server.TestDesktopOk", port, latency));
            }
            if (FilesEnabled)
            {
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
                    results.Insert(0, share.Length > 0 ? Loc.T("Server.TestOkShare") : Loc.F("Server.TestOk", shares.Count));
                }
                catch (SmbException ex)
                {
                    ok = false;
                    var tr = SmbErrorTranslator.Translate(ex.Code);
                    results.Insert(0, $"{Loc.T(tr.TitleKey)}. {Loc.T(tr.DetailKey)}");
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    ok = false;
                    results.Insert(0, ex.Message);
                }
            }
            TestSucceeded = ok;
            TestResult = string.Join("  ·  ", results);
            foreach (var p in new[] { ConnectionService.SmbPort }.Concat(DesktopEnabled && TryPort(out var dp) ? [dp] : Array.Empty<int>()))
                _ = Main.Services.Health.CheckAsync(t.Host, p);
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
        : base(main, node, parent, isNew)
    {
        AddSections(
            (EditorSection.General, "\uE946", "Server.Section.General"),
            (EditorSection.Files, "\uE8B7", "Server.Section.Files"),
            (EditorSection.Desktop, "\uE7F4", "Server.Section.Desktop"),
            (EditorSection.Notes, "\uE70B", "Server.Section.Notes"));
    }

    public override string Title => IsNew ? Loc.T("Group.TitleNew") : Loc.T("Group.TitleEdit");

    public override bool TryApply()
    {
        ValidationError = null;
        if (string.IsNullOrWhiteSpace(Name)) return Fail(Loc.T("Server.Validation.Name"), EditorSection.General);
        if (Credentials.Validate() is { } fileErr) return Fail(fileErr, EditorSection.Files);
        if (DesktopCredentials.Validate() is { } deskErr) return Fail(deskErr, EditorSection.Desktop);
        ApplyCommon();
        return true;
    }
}
