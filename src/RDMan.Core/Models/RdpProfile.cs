using System.Text.Json.Serialization;
namespace RDMan.Core.Models;

public enum RdpAudio { Local, Remote, Off }

/// <summary>How the user's sign-in is protected when it reaches the server.</summary>
public enum RdpSignInProtection
{
    /// <summary>Normal CredSSP sign-in (credentials are delegated to the server).</summary>
    Standard,
    /// <summary>Restricted Admin: no reusable credentials on the server (admin accounts only).</summary>
    RestrictedAdmin,
    /// <summary>Remote Credential Guard: Kerberos requests are redirected back to this PC.</summary>
    RemoteCredentialGuard,
}

/// <summary>What to do when the server's identity (certificate) can't be verified.</summary>
public enum RdpServerCheck
{
    /// <summary>Ask, like mstsc does by default.</summary>
    Warn,
    /// <summary>Refuse to connect.</summary>
    Require,
    /// <summary>Connect without checking.</summary>
    Skip,
}

/// <summary>When the Remote Desktop Gateway is used.</summary>
public enum RdpGatewayUsage
{
    /// <summary>Connect directly (an explicit "no gateway" that overrides a parent's).</summary>
    Off,
    /// <summary>Always through the gateway.</summary>
    Always,
    /// <summary>Through the gateway, except for addresses on the local network.</summary>
    BypassLocal,
}

/// <summary>How RDMan signs in to the gateway.</summary>
public enum RdpGatewayLogon
{
    /// <summary>The same account as the server (credential sharing).</summary>
    SameAsServer,
    /// <summary>The Windows user signed in on this PC.</summary>
    Windows,
    /// <summary>Its own user name and password.</summary>
    Custom,
}

/// <summary>Remote Desktop Gateway (RD Gateway) settings; set as a whole on a group or server.</summary>
public sealed class RdpGateway
{
    public RdpGatewayUsage Usage { get; set; } = RdpGatewayUsage.Always;
    public string Host { get; set; } = "";
    public RdpGatewayLogon Logon { get; set; } = RdpGatewayLogon.SameAsServer;
    public string? Username { get; set; }
    /// <summary>DPAPI-protected (see <c>SecretProtector</c>). Never plaintext.</summary>
    public string? ProtectedPassword { get; set; }

    public RdpGateway Clone() => (RdpGateway)MemberwiseClone();
}

/// <summary>
/// Remote desktop options of a group or server. Every property is nullable: null means "inherit from the
/// parent group" (the root falls back to <c>RdpSettingsResolver</c> defaults). One editor, one rule — instead
/// of RDCMan's "inherit from parent" checkbox on every settings tab.
/// </summary>
public sealed class RdpProfile
{
    public bool? RedirectClipboard { get; set; }
    public bool? RedirectDrives { get; set; }
    public bool? RedirectPrinters { get; set; }
    public bool? RedirectSmartCards { get; set; }
    public RdpAudio? Audio { get; set; }

    /// <summary>Windows key combinations (Win, Alt+Tab…) go to the server while the session has focus.</summary>
    public bool? WindowsKeysToServer { get; set; }

    public bool? NetworkLevelAuthentication { get; set; }
    public RdpServerCheck? ServerCheck { get; set; }
    public RdpSignInProtection? SignInProtection { get; set; }

    /// <summary>Connect to the administrative/console session.</summary>
    public bool? AdminSession { get; set; }

    /// <summary>16, 24 or 32.</summary>
    public int? ColorDepth { get; set; }

    /// <summary>Remote Desktop Gateway; null = inherit (the default is a direct connection).</summary>
    public RdpGateway? Gateway { get; set; }

    public RdpProfile Clone()
    {
        var copy = (RdpProfile)MemberwiseClone();
        copy.Gateway = Gateway?.Clone();
        return copy;
    }

    [JsonIgnore]
    public bool IsEmpty =>
        RedirectClipboard is null && RedirectDrives is null && RedirectPrinters is null && RedirectSmartCards is null &&
        Audio is null && WindowsKeysToServer is null && NetworkLevelAuthentication is null && ServerCheck is null &&
        SignInProtection is null && AdminSession is null && ColorDepth is null && Gateway is null;
}
