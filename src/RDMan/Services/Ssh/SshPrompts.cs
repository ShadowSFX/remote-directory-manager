namespace RDMan.Services.Ssh;

/// <summary>A question the server asks during sign-in (keyboard-interactive: passwords, one-time codes).</summary>
public sealed record SshPromptField(string Text, bool Echo);

/// <summary>What the user typed in the sign-in dialog.</summary>
public sealed record SshPasswordAnswer(string User, string Password, bool Remember);

/// <summary>
/// UI callbacks used while connecting. Implementations marshal to the UI thread; they're awaited (or, inside SSH.NET's
/// synchronous keyboard-interactive event, waited on) from background threads only, never from the UI thread.
/// A null result means the user cancelled.
/// </summary>
public interface ISshPrompts
{
    Task<string?> AskPassphraseAsync(string keyName, CancellationToken ct);

    Task<SshPasswordAnswer?> AskPasswordAsync(string? user, bool userEditable, CancellationToken ct);

    Task<string[]?> AskKeyboardInteractiveAsync(string? name, string? instruction, IReadOnlyList<SshPromptField> fields, CancellationToken ct);

    /// <summary>Progress lines for the terminal ("Connecting to …", "Authenticated with key …").</summary>
    void Progress(SshProgress step, string detail);
}

public enum SshProgress
{
    Resolving,
    Connecting,
    Connected,
    HostKeyTrusted,
    Jumping,
    Authenticating,
    Authenticated,
    Failed,
}
