using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RDMan.Core.Models;
using RDMan.Core.Rdp;
using RDMan.Infrastructure;

namespace RDMan.ViewModels;

/// <summary>
/// Remote Desktop Gateway block of the server/group editor: inherit, direct, or a gateway (always / except local
/// addresses) with how to sign in to it. Saved as one unit on the node, like RDCMan's gateway tab.
/// </summary>
public sealed partial class GatewayEditorViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly string? _existingPassword;

    public GatewayEditorViewModel(MainViewModel main, RdpGateway? current)
    {
        _main = main;
        Modes.Add(new OptionChoice(null, ""));
        Modes.Add(new OptionChoice(RdpGatewayUsage.Off, Loc.T("Gateway.Off")));
        Modes.Add(new OptionChoice(RdpGatewayUsage.Always, Loc.T("Gateway.Always")));
        Modes.Add(new OptionChoice(RdpGatewayUsage.BypassLocal, Loc.T("Gateway.BypassLocal")));
        Logons.Add(new OptionChoice(RdpGatewayLogon.SameAsServer, Loc.T("Gateway.Logon.SameAsServer")));
        Logons.Add(new OptionChoice(RdpGatewayLogon.Windows, Loc.T("Gateway.Logon.Windows")));
        Logons.Add(new OptionChoice(RdpGatewayLogon.Custom, Loc.T("Gateway.Logon.Custom")));
        _selectedMode = current is null ? Modes[0] : Modes.FirstOrDefault(m => Equals(m.Value, current.Usage)) ?? Modes[0];
        _host = current?.Host ?? "";
        _selectedLogon = Logons.FirstOrDefault(l => Equals(l.Value, current?.Logon ?? RdpGatewayLogon.SameAsServer)) ?? Logons[0];
        _username = current?.Username ?? "";
        _existingPassword = current?.ProtectedPassword;
        HasStoredPassword = !string.IsNullOrEmpty(_existingPassword);
    }

    public ObservableCollection<OptionChoice> Modes { get; } = new();
    public ObservableCollection<OptionChoice> Logons { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGateway), nameof(IsOverridden))]
    private OptionChoice _selectedMode;

    [ObservableProperty] private string _host;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomLogon))]
    private OptionChoice _selectedLogon;

    [ObservableProperty] private string _username;

    /// <summary>Typed in the password box; null = keep the stored one.</summary>
    public string? NewPassword { get; set; }

    public bool HasStoredPassword { get; }

    /// <summary>A gateway is set here (not inherited, not "direct"): its fields are shown.</summary>
    public bool IsGateway => SelectedMode.Value is RdpGatewayUsage.Always or RdpGatewayUsage.BypassLocal;
    public bool IsCustomLogon => Equals(SelectedLogon.Value, RdpGatewayLogon.Custom);
    public bool IsOverridden => SelectedMode.Value is not null;

    /// <summary>Rewrites the "Inherit: …" entry from what the parent resolves to.</summary>
    public void RefreshInherited(EffectiveRdpSettings parent)
    {
        var g = parent.Gateway;
        var value = g.Value is { } gw ? Loc.F("Gateway.Via", gw.Host) : Loc.T("Gateway.Direct");
        var text = g.Source is null ? Loc.F("Rdp.Default", value) : Loc.F("Rdp.InheritedFrom", value, g.Source.Name);
        var wasSelected = SelectedMode.Value is null;
        Modes[0] = new OptionChoice(null, Loc.F("Rdp.InheritChoice", text));
        if (wasSelected) SelectedMode = Modes[0];
    }

    /// <summary>Null when valid, otherwise what to fix.</summary>
    public string? Validate()
    {
        if (!IsGateway) return null;
        if (string.IsNullOrWhiteSpace(Host) || Host.Trim().Any(char.IsWhiteSpace)) return Loc.T("Gateway.Validation.Host");
        if (IsCustomLogon && string.IsNullOrWhiteSpace(Username)) return Loc.T("Gateway.Validation.User");
        return null;
    }

    public void ApplyTo(RdpProfile profile)
    {
        profile.Gateway = SelectedMode.Value switch
        {
            null => null,
            RdpGatewayUsage.Off => new RdpGateway { Usage = RdpGatewayUsage.Off },
            RdpGatewayUsage usage => new RdpGateway
            {
                Usage = usage,
                Host = Host.Trim(),
                Logon = (RdpGatewayLogon)SelectedLogon.Value!,
                Username = IsCustomLogon ? Username.Trim() : null,
                ProtectedPassword = !IsCustomLogon ? null
                    : NewPassword is { Length: > 0 } typed ? _main.State.Secrets.Protect(typed) : _existingPassword,
            },
            _ => null,
        };
    }
}
