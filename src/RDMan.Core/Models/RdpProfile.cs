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

    public RdpProfile Clone() => (RdpProfile)MemberwiseClone();

    public bool IsEmpty =>
        RedirectClipboard is null && RedirectDrives is null && RedirectPrinters is null && RedirectSmartCards is null &&
        Audio is null && WindowsKeysToServer is null && NetworkLevelAuthentication is null && ServerCheck is null &&
        SignInProtection is null && AdminSession is null && ColorDepth is null;
}
