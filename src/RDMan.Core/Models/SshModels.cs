using System.Text.Json.Serialization;

namespace RDMan.Core.Models;

/// <summary>SSH terminal endpoint of a server. Null or disabled = the server offers no terminal.</summary>
public sealed class SshEndpoint
{
    public const int DefaultPort = 22;

    public bool Enabled { get; set; } = true;

    /// <summary>Explicit port. Null = the port from ~/.ssh/config, otherwise 22 (exactly like <c>ssh host</c>).</summary>
    public int? Port { get; set; }

    /// <summary>
    /// Resolve the server's host through ~/.ssh/config the way <c>ssh &lt;host&gt;</c> does (HostName, User,
    /// IdentityFile, Port, ProxyJump…). Values set in RDMan win over the config, like command-line options do.
    /// </summary>
    public bool UseSshConfig { get; set; } = true;

    /// <summary>Port forwards opened with this server's terminal (in addition to the config's *Forward lines).</summary>
    public List<SshForward> Forwards { get; set; } = new();

    public SshEndpoint Clone()
    {
        var c = (SshEndpoint)MemberwiseClone();
        c.Forwards = Forwards.Select(f => f.Clone()).ToList();
        return c;
    }
}

/// <summary>How a node authenticates its SSH terminal. Inherited down the tree like the other credentials.</summary>
public enum SshAuthMode
{
    /// <summary>Use the parent group's setting (the root falls back to <see cref="Auto"/>).</summary>
    Inherit = 0,
    /// <summary>
    /// What <c>ssh</c> does: the SSH agent, then the config's IdentityFile keys, then the default keys
    /// (~/.ssh/id_ed25519, id_ecdsa, id_rsa), then ask for a password.
    /// </summary>
    Auto = 1,
    /// <summary>A private key stored (encrypted) in RDMan's key vault.</summary>
    Key = 2,
    /// <summary>Username + password stored on this node, or a saved credential profile.</summary>
    Password = 3,
    /// <summary>Only the SSH agent (Windows OpenSSH agent or Pageant).</summary>
    Agent = 4,
}

public sealed class SshAuthSettings
{
    public SshAuthMode Mode { get; set; } = SshAuthMode.Inherit;

    /// <summary>Remote user. Null = the user from ~/.ssh/config, otherwise RDMan asks.</summary>
    public string? Username { get; set; }

    /// <summary><see cref="SshAuthMode.Key"/>: the vault key to use.</summary>
    public Guid? KeyId { get; set; }

    /// <summary><see cref="SshAuthMode.Password"/>: DPAPI-protected password (never plaintext).</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary><see cref="SshAuthMode.Password"/>: a saved credential profile instead of a password on the node.</summary>
    public Guid? ProfileId { get; set; }

    public SshAuthSettings Clone() => (SshAuthSettings)MemberwiseClone();
}

/// <summary>A private key imported into RDMan, kept DPAPI-encrypted inside the connections file.</summary>
public sealed class SshKeyEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>ssh-ed25519, ssh-rsa, ecdsa-sha2-nistp256…</summary>
    public string Algorithm { get; set; } = "";
    public int Bits { get; set; }
    /// <summary>"SHA256:…" of the public key, as printed by <c>ssh-keygen -lf</c>.</summary>
    public string Fingerprint { get; set; } = "";
    public string? Comment { get; set; }
    /// <summary>Where the key was imported from (informational; the key no longer needs the file).</summary>
    public string? SourcePath { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.Now;
    /// <summary>The key file's text, DPAPI-protected. Null after an export without secrets.</summary>
    public string? ProtectedKey { get; set; }
    /// <summary>The key's passphrase, DPAPI-protected, when the user chose to remember it.</summary>
    public string? ProtectedPassphrase { get; set; }
    /// <summary>OpenSSH certificate (<c>*-cert.pub</c> contents) for certificate authentication.</summary>
    public string? Certificate { get; set; }

    [JsonIgnore] public bool HasKeyMaterial => !string.IsNullOrEmpty(ProtectedKey);

    public SshKeyEntry Clone() => (SshKeyEntry)MemberwiseClone();
}

public enum SshForwardKind { Local, Remote, Dynamic }

/// <summary>One port forward: <c>-L</c>, <c>-R</c> or <c>-D</c>.</summary>
public sealed class SshForward
{
    public SshForwardKind Kind { get; set; } = SshForwardKind.Local;
    /// <summary>Listening address (local for L/D, on the server for R). Null = loopback.</summary>
    public string? BindAddress { get; set; }
    public int BindPort { get; set; }
    /// <summary>Destination (not used by <see cref="SshForwardKind.Dynamic"/>).</summary>
    public string? TargetHost { get; set; }
    public int TargetPort { get; set; }
    public bool AutoStart { get; set; } = true;
    public string? Label { get; set; }

    public SshForward Clone() => (SshForward)MemberwiseClone();

    /// <summary>"127.0.0.1:5432 → db:5432" style summary (no localization needed: it's addresses).</summary>
    public override string ToString()
    {
        var bind = $"{BindAddress ?? "localhost"}:{BindPort}";
        return Kind switch
        {
            SshForwardKind.Dynamic => $"SOCKS {bind}",
            SshForwardKind.Remote => $"{bind} ← {TargetHost}:{TargetPort}",
            _ => $"{bind} → {TargetHost}:{TargetPort}",
        };
    }
}

/// <summary>
/// Terminal options of a group or server. Every property is nullable: null = inherit from the parent group
/// (the root falls back to <c>SshSettingsResolver.Defaults</c> and the app's terminal settings).
/// </summary>
public sealed class SshProfile
{
    /// <summary>Color scheme id (see <c>TerminalSchemes</c>); "auto" follows the app theme.</summary>
    public string? ColorScheme { get; set; }
    /// <summary>Font size in points; null = the app's terminal setting.</summary>
    public double? FontSize { get; set; }
    /// <summary>Start bash with RDMan's session-only powerline prompt and shell integration.</summary>
    public bool? InjectPrompt { get; set; }
    /// <summary>Show live CPU/RAM/disk of the server in the tab (Linux).</summary>
    public bool? ShowStats { get; set; }
    /// <summary>Write the session to a text log under RDMan.data\logs.</summary>
    public bool? LogSession { get; set; }
    /// <summary>Seconds between keepalives (0 = off). Null = config ServerAliveInterval, otherwise 30.</summary>
    public int? KeepAliveSeconds { get; set; }
    /// <summary>Jump host(s), "user@host:port,host2" like ProxyJump. "none" = no jump even if the config has one.</summary>
    public string? ProxyJump { get; set; }
    /// <summary>Command typed into the shell right after it starts (e.g. "cd /var/www").</summary>
    public string? StartupCommand { get; set; }
    /// <summary>Let programs on the server write to the Windows clipboard (OSC 52).</summary>
    public bool? AllowClipboardWrite { get; set; }

    public SshProfile Clone() => (SshProfile)MemberwiseClone();

    [JsonIgnore]
    public bool IsEmpty =>
        ColorScheme is null && FontSize is null && InjectPrompt is null && ShowStats is null && LogSession is null &&
        KeepAliveSeconds is null && ProxyJump is null && StartupCommand is null && AllowClipboardWrite is null;
}

/// <summary>A saved command, global or limited to the servers of one group.</summary>
public sealed class Snippet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Text sent to the terminal. <c>{{name}}</c> placeholders are asked for; {{host}} and {{user}} are filled in.</summary>
    public string Body { get; set; } = "";
    /// <summary>Only offered for servers under this group; null = everywhere.</summary>
    public Guid? GroupId { get; set; }
    /// <summary>Press Enter after inserting the text.</summary>
    public bool RunImmediately { get; set; }

    public Snippet Clone() => (Snippet)MemberwiseClone();
}
