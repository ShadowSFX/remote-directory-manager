namespace RDMan.Core.Smb;

/// <summary>What the UI should offer next to an error.</summary>
public enum ErrorAction
{
    Retry,
    EditCredentials,
    CloseExistingSessions,
    EnableGuestOrUseCredentials,
    CheckHost,
}

public sealed record TranslatedError(int Code, string TitleKey, string DetailKey, ErrorAction PrimaryAction);

/// <summary>
/// Maps Win32 error codes returned by WNet*/file APIs to localized, actionable messages.
/// Codes and their meaning were verified with FormatMessage on Windows 11 (see tests).
/// </summary>
public static class SmbErrorTranslator
{
    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorBadNetPath = 53;
    public const int ErrorUnexpectedNetError = 59;
    public const int ErrorNetNameDeleted = 64;
    public const int ErrorNetworkAccessDenied = 65;
    public const int ErrorBadNetName = 67;
    public const int ErrorInvalidPassword = 86;
    public const int ErrorSemTimeout = 121;
    public const int ErrorNoNetOrBadPath = 1203;
    public const int ErrorSessionCredentialConflict = 1219;
    public const int ErrorNoNetwork = 1222;
    public const int ErrorNetworkUnreachable = 1231;
    public const int ErrorHostUnreachable = 1232;
    public const int ErrorNotAuthenticated = 1244;
    public const int ErrorGuestLogonBlocked = 1272;
    public const int ErrorNoLogonServers = 1311;
    public const int ErrorLogonFailure = 1326;
    public const int ErrorAccountRestriction = 1327;
    public const int ErrorPasswordExpired = 1330;
    public const int ErrorAccountDisabled = 1331;
    public const int ErrorLogonTypeNotGranted = 1385;
    public const int ErrorAccountLockedOut = 1909;
    public const int ErrorNotConnected = 2250;

    /// <summary>Synthetic code: TCP 445 did not answer within the probe timeout.</summary>
    public const int PortUnreachable = -445;

    public static TranslatedError Translate(int code) => code switch
    {
        ErrorAccessDenied or ErrorNetworkAccessDenied =>
            new(code, "Err.AccessDenied.Title", "Err.AccessDenied.Detail", ErrorAction.EditCredentials),
        ErrorLogonFailure or ErrorInvalidPassword or ErrorNotAuthenticated =>
            new(code, "Err.Logon.Title", "Err.Logon.Detail", ErrorAction.EditCredentials),
        ErrorAccountRestriction =>
            new(code, "Err.Restriction.Title", "Err.Restriction.Detail", ErrorAction.EditCredentials),
        ErrorPasswordExpired or ErrorAccountDisabled or ErrorAccountLockedOut or ErrorLogonTypeNotGranted =>
            new(code, "Err.Account.Title", "Err.Account.Detail", ErrorAction.EditCredentials),
        ErrorSessionCredentialConflict =>
            new(code, "Err.Conflict.Title", "Err.Conflict.Detail", ErrorAction.CloseExistingSessions),
        ErrorGuestLogonBlocked =>
            new(code, "Err.GuestBlocked.Title", "Err.GuestBlocked.Detail", ErrorAction.EnableGuestOrUseCredentials),
        ErrorBadNetName =>
            new(code, "Err.ShareNotFound.Title", "Err.ShareNotFound.Detail", ErrorAction.CheckHost),
        ErrorFileNotFound or ErrorPathNotFound =>
            new(code, "Err.PathNotFound.Title", "Err.PathNotFound.Detail", ErrorAction.Retry),
        ErrorBadNetPath or ErrorNoNetOrBadPath or ErrorNetworkUnreachable or ErrorHostUnreachable or ErrorSemTimeout or PortUnreachable =>
            new(code, "Err.Unreachable.Title", "Err.Unreachable.Detail", ErrorAction.Retry),
        ErrorNetNameDeleted or ErrorUnexpectedNetError =>
            new(code, "Err.Dropped.Title", "Err.Dropped.Detail", ErrorAction.Retry),
        ErrorNoNetwork =>
            new(code, "Err.NoNetwork.Title", "Err.NoNetwork.Detail", ErrorAction.Retry),
        ErrorNoLogonServers =>
            new(code, "Err.NoLogonServers.Title", "Err.NoLogonServers.Detail", ErrorAction.EditCredentials),
        _ => new(code, "Err.Generic.Title", "Err.Generic.Detail", ErrorAction.Retry),
    };

    /// <summary>Extracts the Win32 code from an IOException/UnauthorizedAccessException HResult (0x8007xxxx).</summary>
    public static int Win32CodeFromHResult(int hresult)
    {
        const int facilityMask = unchecked((int)0xFFFF0000);
        const int win32Facility = unchecked((int)0x80070000);
        return (hresult & facilityMask) == win32Facility ? hresult & 0xFFFF : hresult;
    }
}
