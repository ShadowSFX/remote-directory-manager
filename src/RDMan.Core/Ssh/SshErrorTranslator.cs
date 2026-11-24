namespace RDMan.Core.Ssh;

/// <summary>Why an SSH connection failed or ended, classified by the app from SSH.NET/socket exceptions.</summary>
public enum SshFailure
{
    Unknown,
    HostNotFound,
    Refused,
    Timeout,
    Unreachable,
    AuthFailed,
    NoUser,
    KeyUnreadable,
    KeyPassphrase,
    HostKeyChanged,
    HostKeyRevoked,
    HostKeyRejected,
    Protocol,
    ConnectionLost,
    ServerClosed,
    ShellRejected,
    JumpFailed,
    JumpInvalid,
    AgentUnavailable,
    WebViewMissing,
}

public enum SshErrorAction
{
    None,
    Reconnect,
    EditCredentials,
    EditServer,
    ReplaceHostKey,
    InstallWebView,
}

public sealed record SshTranslatedError(SshFailure Failure, string TitleKey, string DetailKey, SshErrorAction Action, bool IsError = true);

/// <summary>Maps an <see cref="SshFailure"/> to localized, actionable text keys (SshErr.*).</summary>
public static class SshErrorTranslator
{
    public static SshTranslatedError Translate(SshFailure failure)
    {
        SshTranslatedError Make(string key, SshErrorAction action, bool isError = true)
            => new(failure, $"SshErr.{key}.Title", $"SshErr.{key}.Detail", action, isError);

        return failure switch
        {
            SshFailure.HostNotFound => Make("HostNotFound", SshErrorAction.EditServer),
            SshFailure.Refused => Make("Refused", SshErrorAction.Reconnect),
            SshFailure.Timeout => Make("Timeout", SshErrorAction.Reconnect),
            SshFailure.Unreachable => Make("Unreachable", SshErrorAction.Reconnect),
            SshFailure.AuthFailed => Make("AuthFailed", SshErrorAction.EditCredentials),
            SshFailure.NoUser => Make("NoUser", SshErrorAction.EditCredentials),
            SshFailure.KeyUnreadable => Make("KeyUnreadable", SshErrorAction.EditCredentials),
            SshFailure.KeyPassphrase => Make("KeyPassphrase", SshErrorAction.EditCredentials),
            SshFailure.HostKeyChanged => Make("HostKeyChanged", SshErrorAction.ReplaceHostKey),
            SshFailure.HostKeyRevoked => Make("HostKeyRevoked", SshErrorAction.None),
            SshFailure.HostKeyRejected => Make("HostKeyRejected", SshErrorAction.Reconnect, isError: false),
            SshFailure.Protocol => Make("Protocol", SshErrorAction.Reconnect),
            SshFailure.ConnectionLost => Make("ConnectionLost", SshErrorAction.Reconnect),
            SshFailure.ServerClosed => Make("ServerClosed", SshErrorAction.Reconnect, isError: false),
            SshFailure.ShellRejected => Make("ShellRejected", SshErrorAction.Reconnect),
            SshFailure.JumpFailed => Make("JumpFailed", SshErrorAction.Reconnect),
            SshFailure.JumpInvalid => Make("JumpInvalid", SshErrorAction.EditServer),
            SshFailure.AgentUnavailable => Make("AgentUnavailable", SshErrorAction.EditCredentials),
            SshFailure.WebViewMissing => Make("WebViewMissing", SshErrorAction.InstallWebView),
            _ => Make("Unknown", SshErrorAction.Reconnect),
        };
    }
}
