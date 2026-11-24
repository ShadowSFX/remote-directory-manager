namespace RDMan.Core.Rdp;

public enum RdpErrorAction
{
    None,
    Reconnect,
    EditCredentials,
    CheckHost,
}

public sealed record RdpTranslatedError(int Code, int ExtendedCode, string TitleKey, string DetailKey, RdpErrorAction Action, bool IsError);

/// <summary>
/// Maps the Remote Desktop ActiveX disconnect reason (IMsTscAxEvents::OnDisconnected) and the extended reason
/// (ExtendedDisconnectReasonCode) to actionable messages. Values from learn.microsoft.com (termserv docs).
/// </summary>
public static class RdpErrorTranslator
{
    // discReason
    public const int LocalNotError = 1;
    public const int RemoteByUser = 2;
    public const int ByServer = 3;
    public const int DnsLookupFailed = 260;
    public const int ConnectionTimedOut = 264;
    public const int SocketConnectFailed = 516;
    public const int HostNotFound = 520;
    public const int SocketRecvFailed = 1028;
    public const int DnsLookupFailed2 = 1288;
    public const int GetHostByNameFailed = 1540;
    public const int TimeoutOccurred = 1796;
    public const int WinsockFdClose = 2308;
    public const int LicensingFailed = 2056;
    public const int LicensingTimeout = 2312;
    public const int LogonFailure = 2055;
    public const int NoSuchUser = 2567;
    public const int AccountDisabled = 2823;
    public const int AccountRestriction = 3079;
    public const int AccountLockedOut = 3335;
    public const int AccountExpired = 3591;
    public const int PasswordExpired = 3847;
    public const int PasswordMustChange = 4615;
    public const int DelegationPolicy = 5639;
    public const int NoAuthenticatingAuthority = 6151;
    public const int CertExpired = 6919;
    public const int FreshCredRequired = 8455;

    // ExtendedDisconnectReasonCode
    public const int ExApiInitiatedDisconnect = 1;
    public const int ExApiInitiatedLogoff = 2;
    public const int ExServerIdleTimeout = 3;
    public const int ExServerLogonTimeout = 4;
    public const int ExReplacedByOtherConnection = 5;
    public const int ExServerDeniedConnection = 7;
    public const int ExServerInsufficientPrivileges = 9;
    public const int ExServerFreshCredsRequired = 10;
    public const int ExRpcInitiatedDisconnectByUser = 11;
    public const int ExLogoffByUser = 12;
    public const int ExLicenseNoRemoteConnections = 266;

    public static RdpTranslatedError Translate(int code, int extended)
    {
        RdpTranslatedError Make(string key, RdpErrorAction action, bool isError = true)
            => new(code, extended, $"RdpErr.{key}.Title", $"RdpErr.{key}.Detail", action, isError);

        // Extended reasons are more specific about *why* the server ended the session.
        switch (extended)
        {
            case ExReplacedByOtherConnection: return Make("Replaced", RdpErrorAction.Reconnect);
            case ExServerIdleTimeout: return Make("Idle", RdpErrorAction.Reconnect);
            case ExServerLogonTimeout: return Make("LogonTimeout", RdpErrorAction.Reconnect);
            case ExLogoffByUser or ExApiInitiatedLogoff: return Make("LoggedOff", RdpErrorAction.Reconnect, isError: false);
            case ExRpcInitiatedDisconnectByUser: return Make("DisconnectedByAdmin", RdpErrorAction.Reconnect);
            case ExServerDeniedConnection or ExServerInsufficientPrivileges: return Make("Denied", RdpErrorAction.EditCredentials);
            case ExServerFreshCredsRequired: return Make("FreshCreds", RdpErrorAction.EditCredentials);
            case ExLicenseNoRemoteConnections: return Make("Licensing", RdpErrorAction.None);
        }

        return code switch
        {
            LocalNotError or RemoteByUser => Make("Closed", RdpErrorAction.Reconnect, isError: false),
            ByServer => Make("ByServer", RdpErrorAction.Reconnect),
            DnsLookupFailed or DnsLookupFailed2 or GetHostByNameFailed or HostNotFound => Make("NotFound", RdpErrorAction.CheckHost),
            ConnectionTimedOut or SocketConnectFailed or TimeoutOccurred => Make("Unreachable", RdpErrorAction.Reconnect),
            WinsockFdClose or SocketRecvFailed => Make("Dropped", RdpErrorAction.Reconnect),
            LogonFailure or NoSuchUser => Make("Logon", RdpErrorAction.EditCredentials),
            AccountDisabled or AccountLockedOut or AccountExpired or AccountRestriction => Make("Account", RdpErrorAction.EditCredentials),
            PasswordExpired or PasswordMustChange => Make("PasswordExpired", RdpErrorAction.EditCredentials),
            FreshCredRequired or DelegationPolicy => Make("FreshCreds", RdpErrorAction.EditCredentials),
            NoAuthenticatingAuthority => Make("NoAuthority", RdpErrorAction.EditCredentials),
            CertExpired => Make("Certificate", RdpErrorAction.Reconnect),
            LicensingFailed or LicensingTimeout => Make("Licensing", RdpErrorAction.None),
            _ => Make("Generic", RdpErrorAction.Reconnect),
        };
    }

    /// <summary>True when the session ended normally and no error panel should be shown.</summary>
    public static bool IsBenign(int code, int extended) => !Translate(code, extended).IsError;
}
