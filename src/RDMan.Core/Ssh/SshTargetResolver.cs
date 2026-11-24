using System.Globalization;
using RDMan.Core.Models;
using RDMan.Core.Rdp;

namespace RDMan.Core.Ssh;

public sealed record EffectiveSshSettings(
    Resolved<string> ColorScheme,
    Resolved<double?> FontSize,
    Resolved<bool> InjectPrompt,
    Resolved<bool> ShowStats,
    Resolved<bool> LogSession,
    Resolved<int?> KeepAliveSeconds,
    Resolved<string?> ProxyJump,
    Resolved<string?> StartupCommand,
    Resolved<bool> AllowClipboardWrite);

/// <summary>Merges <see cref="SshProfile"/> field by field up to the root, keeping each value's origin (like RDP).</summary>
public static class SshSettingsResolver
{
    public const string AutoScheme = "auto";

    public static readonly SshProfile Defaults = new()
    {
        ColorScheme = AutoScheme,
        InjectPrompt = true,
        ShowStats = true,
        LogSession = false,
        AllowClipboardWrite = false,
    };

    public static EffectiveSshSettings Resolve(TreeNode node, TreeIndex index) => Resolve(index.SelfAndAncestors(node).ToList());

    public static EffectiveSshSettings ResolveParent(TreeNode node, TreeIndex index)
        => index.ParentOf(node.Id) is { } parent ? Resolve(parent, index) : Resolve([]);

    public static EffectiveSshSettings ResolveUnder(GroupNode? parent, TreeIndex index)
        => parent is null ? Resolve([]) : Resolve(parent, index);

    private static EffectiveSshSettings Resolve(IReadOnlyList<TreeNode> chain)
    {
        Resolved<T> Pick<T>(Func<SshProfile, T?> get) where T : struct
        {
            foreach (var n in chain)
                if (n.Ssh is not null && get(n.Ssh) is { } v) return new Resolved<T>(v, n);
            return new Resolved<T>(get(Defaults) ?? default, null);
        }

        Resolved<T?> PickNullable<T>(Func<SshProfile, T?> get) where T : struct
        {
            foreach (var n in chain)
                if (n.Ssh is not null && get(n.Ssh) is { } v) return new Resolved<T?>(v, n);
            return new Resolved<T?>(get(Defaults), null);
        }

        Resolved<string?> PickText(Func<SshProfile, string?> get)
        {
            foreach (var n in chain)
                if (n.Ssh is not null && get(n.Ssh) is { } v) return new Resolved<string?>(v, n);
            return new Resolved<string?>(get(Defaults), null);
        }

        var scheme = PickText(p => string.IsNullOrWhiteSpace(p.ColorScheme) ? null : p.ColorScheme);
        var size = PickNullable(p => p.FontSize is >= 6 and <= 72 ? p.FontSize : null);
        var keepAlive = PickNullable(p => p.KeepAliveSeconds is >= 0 and <= 3600 ? p.KeepAliveSeconds : null);

        return new EffectiveSshSettings(
            new Resolved<string>(scheme.Value ?? AutoScheme, scheme.Source),
            size,
            Pick(p => p.InjectPrompt),
            Pick(p => p.ShowStats),
            Pick(p => p.LogSession),
            keepAlive,
            PickText(p => string.IsNullOrWhiteSpace(p.ProxyJump) ? null : p.ProxyJump.Trim()),
            PickText(p => string.IsNullOrWhiteSpace(p.StartupCommand) ? null : p.StartupCommand),
            Pick(p => p.AllowClipboardWrite));
    }
}

/// <summary>The authentication a server's terminal will use, and where each part came from.</summary>
public sealed record EffectiveSshAuth(
    SshAuthMode Mode,
    TreeNode? ModeSource,
    string? Username,
    TreeNode? UsernameSource,
    SshKeyEntry? Key,
    string? ProtectedPassword,
    CredentialProfile? Profile,
    /// <summary>Broken configuration (missing key or profile): RDMan falls back to <see cref="SshAuthMode.Auto"/>.</summary>
    bool IsMisconfigured = false);

public static class SshAuthResolver
{
    /// <summary>
    /// The method comes from the nearest node whose mode isn't Inherit (root: Auto); the user name is inherited
    /// independently (nearest non-empty), so a group can say "ec2-user + this .pem" and a server can still
    /// override only the user.
    /// </summary>
    public static EffectiveSshAuth Resolve(TreeNode node, TreeIndex index, IReadOnlyList<CredentialProfile> profiles, IReadOnlyList<SshKeyEntry> keys)
        => Resolve(index.SelfAndAncestors(node).ToList(), profiles, keys);

    public static EffectiveSshAuth ResolveUnder(GroupNode? parent, TreeIndex index, IReadOnlyList<CredentialProfile> profiles, IReadOnlyList<SshKeyEntry> keys)
        => Resolve(parent is null ? [] : index.SelfAndAncestors(parent).ToList(), profiles, keys);

    public static EffectiveSshAuth ResolveParent(TreeNode node, TreeIndex index, IReadOnlyList<CredentialProfile> profiles, IReadOnlyList<SshKeyEntry> keys)
        => index.ParentOf(node.Id) is { } parent ? Resolve(parent, index, profiles, keys) : Resolve([], profiles, keys);

    private static EffectiveSshAuth Resolve(IReadOnlyList<TreeNode> chain, IReadOnlyList<CredentialProfile> profiles, IReadOnlyList<SshKeyEntry> keys)
    {
        string? user = null;
        TreeNode? userSource = null;
        foreach (var n in chain)
        {
            if (!string.IsNullOrWhiteSpace(n.SshAuth?.Username))
            {
                user = n.SshAuth.Username.Trim();
                userSource = n;
                break;
            }
        }

        foreach (var n in chain)
        {
            var a = n.SshAuth;
            if (a is null || a.Mode == SshAuthMode.Inherit) continue;
            switch (a.Mode)
            {
                case SshAuthMode.Key:
                    var key = a.KeyId is { } kid ? keys.FirstOrDefault(k => k.Id == kid) : null;
                    if (key is null || !key.HasKeyMaterial)
                        return new EffectiveSshAuth(SshAuthMode.Auto, n, user, userSource, key, null, null, IsMisconfigured: true);
                    return new EffectiveSshAuth(SshAuthMode.Key, n, user, userSource, key, null, null);
                case SshAuthMode.Password when a.ProfileId is { } pid:
                    var profile = profiles.FirstOrDefault(p => p.Id == pid);
                    if (profile is null || string.IsNullOrWhiteSpace(profile.Username))
                        return new EffectiveSshAuth(SshAuthMode.Auto, n, user, userSource, null, null, profile, IsMisconfigured: true);
                    return new EffectiveSshAuth(SshAuthMode.Password, n, profile.Username.Trim(), n, null, profile.ProtectedPassword, profile);
                case SshAuthMode.Password:
                    return new EffectiveSshAuth(SshAuthMode.Password, n, user, userSource, null, a.ProtectedPassword, null);
                default:
                    return new EffectiveSshAuth(a.Mode, n, user, userSource, null, null, null);
            }
        }
        return new EffectiveSshAuth(SshAuthMode.Auto, null, user, userSource, null, null, null);
    }
}

/// <summary>Everything needed to open one SSH connection (the target itself, or one jump hop).</summary>
public sealed record SshTarget(
    string Alias,
    string HostName,
    int Port,
    string? User,
    EffectiveSshAuth Auth,
    IReadOnlyList<string> IdentityFiles,
    IReadOnlyList<string> CertificateFiles,
    bool IdentitiesOnly,
    IReadOnlyList<SshDestination> Jumps,
    bool JumpSpecInvalid,
    int KeepAliveSeconds,
    int KeepAliveCountMax,
    int ConnectTimeoutSeconds,
    IReadOnlyList<SshForward> Forwards,
    IReadOnlyList<string> KnownHostsFiles,
    bool HashKnownHosts,
    string StrictHostKeyChecking,
    SshConfigResult? Config)
{
    /// <summary>"ubuntu@45.79.2.143:22" (port shown only when not 22).</summary>
    public string Display => new SshDestination(User, HostName, Port == 22 ? null : Port).ToString();

    /// <summary>Equivalent OpenSSH command line (for "Copy ssh command").</summary>
    public string ToCommandLine(string? keyPath = null)
    {
        var parts = new List<string> { "ssh" };
        if (Port != 22) parts.Add($"-p {Port.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrEmpty(keyPath)) parts.Add($"-i \"{keyPath}\"");
        if (Jumps.Count > 0) parts.Add("-J " + string.Join(',', Jumps));
        // With the config in play, the alias reproduces everything else exactly.
        var host = Config is { MatchedSpecificHost: true } ? Alias : HostName;
        parts.Add(string.IsNullOrEmpty(User) || Config is { MatchedSpecificHost: true } && Config.User == User ? host : $"{User}@{host}");
        return string.Join(' ', parts);
    }
}

public static class SshTargetResolver
{
    public const int DefaultKeepAlive = 30;
    public const int DefaultKeepAliveCountMax = 3;
    public const int DefaultConnectTimeout = 15;

    public static readonly string[] DefaultIdentityNames = ["id_ed25519", "id_ecdsa", "id_rsa"];

    /// <summary>
    /// Builds the connection target for a server: RDMan's explicit values win, then ~/.ssh/config (when the
    /// endpoint uses it), then OpenSSH defaults.
    /// </summary>
    public static SshTarget Resolve(ServerNode server, EffectiveSshAuth auth, EffectiveSshSettings settings, SshConfig config, string? localUser = null)
    {
        var endpoint = server.Terminal ?? new SshEndpoint();
        var alias = server.Host.Trim();
        var cfg = endpoint.UseSshConfig ? config.Resolve(alias, localUser) : null;
        var forwards = new List<SshForward>(endpoint.Forwards.Select(f => f.Clone()));
        if (cfg is not null) forwards.AddRange(ConfigForwards(cfg));

        var jumpText = settings.ProxyJump.Value ?? cfg?.ProxyJump;
        var jumpOk = SshDestination.TryParseJumpList(jumpText, out var jumps);

        return Build(alias, cfg, endpoint.Port, auth.Username, auth, settings.KeepAliveSeconds.Value, jumps, !jumpOk, forwards, config);
    }

    /// <summary>A jump hop: resolved through the same config, authenticated the way <c>ssh</c> would (Auto).</summary>
    public static SshTarget ResolveHop(SshDestination hop, SshConfig config, EffectiveSshAuth? auth = null, string? localUser = null)
    {
        var cfg = config.Resolve(hop.Host, localUser);
        var a = auth ?? new EffectiveSshAuth(SshAuthMode.Auto, null, null, null, null, null, null);
        // A hop's own ProxyJump is honored like OpenSSH does (chains), but never its forwards.
        var jumpOk = SshDestination.TryParseJumpList(cfg.ProxyJump, out var jumps);
        return Build(hop.Host, cfg, hop.Port, hop.User ?? a.Username, a, null, jumps, !jumpOk, [], config);
    }

    private static SshTarget Build(string alias, SshConfigResult? cfg, int? explicitPort, string? explicitUser, EffectiveSshAuth auth,
        int? keepAlive, IReadOnlyList<SshDestination> jumps, bool jumpInvalid, List<SshForward> forwards, SshConfig config)
    {
        var host = cfg?.EffectiveHost ?? alias;
        var port = explicitPort is > 0 and <= 65535 ? explicitPort.Value : cfg?.Port ?? 22;
        var user = !string.IsNullOrWhiteSpace(explicitUser) ? explicitUser : cfg?.User;

        var identities = new List<string>();
        if (auth.Mode == SshAuthMode.Auto)
        {
            if (cfg is not null) identities.AddRange(cfg.IdentityFiles);
            if (identities.Count == 0)
                identities.AddRange(DefaultIdentityNames.Select(n => Path.Combine(config.HomeDirectory, ".ssh", n)));
        }

        var knownHosts = cfg is { UserKnownHostsFiles.Count: > 0 } ? cfg.UserKnownHostsFiles.ToList()
            : [KnownHosts.DefaultPath(config.HomeDirectory)];

        return new SshTarget(
            alias, host, port, user, auth,
            identities.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            cfg?.CertificateFiles.ToList() ?? [],
            cfg?.IdentitiesOnly ?? false,
            jumps, jumpInvalid,
            keepAlive ?? cfg?.ServerAliveInterval ?? DefaultKeepAlive,
            cfg?.ServerAliveCountMax ?? DefaultKeepAliveCountMax,
            cfg?.ConnectTimeout ?? DefaultConnectTimeout,
            forwards,
            knownHosts,
            cfg?.HashKnownHosts ?? false,
            cfg?.StrictHostKeyChecking ?? "ask",
            cfg);
    }

    public static IEnumerable<SshForward> ConfigForwards(SshConfigResult cfg)
    {
        foreach (var l in cfg.LocalForwards)
            if (TryParseForward(SshForwardKind.Local, l, out var f)) yield return f;
        foreach (var r in cfg.RemoteForwards)
            if (TryParseForward(SshForwardKind.Remote, r, out var f)) yield return f;
        foreach (var d in cfg.DynamicForwards)
            if (TryParseForward(SshForwardKind.Dynamic, d, out var f)) yield return f;
    }

    /// <summary>
    /// "[bind:]port host:hostport" (L/R, also "port host hostport") or "[bind:]port" (D), as in ssh_config.
    /// </summary>
    public static bool TryParseForward(SshForwardKind kind, string text, out SshForward forward)
    {
        forward = null!;
        var parts = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        if (!TrySplitBind(parts[0], out var bind, out var bindPort)) return false;

        if (kind == SshForwardKind.Dynamic)
        {
            if (parts.Length != 1) return false;
            forward = new SshForward { Kind = kind, BindAddress = bind, BindPort = bindPort, AutoStart = true };
            return true;
        }

        string target;
        int targetPort;
        if (parts.Length == 2)
        {
            if (!TrySplitBind(parts[1], out var t, out targetPort) || t is null) return false;
            target = t;
        }
        else if (parts.Length == 3 && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out targetPort) && targetPort is > 0 and <= 65535)
        {
            target = parts[1];
        }
        else return false;

        forward = new SshForward { Kind = kind, BindAddress = bind, BindPort = bindPort, TargetHost = target, TargetPort = targetPort, AutoStart = true };
        return true;
    }

    /// <summary>"port", "addr:port", "[v6]:port", "*:port".</summary>
    private static bool TrySplitBind(string s, out string? address, out int port)
    {
        address = null;
        port = 0;
        string portText;
        if (s.StartsWith('['))
        {
            var close = s.IndexOf("]:", StringComparison.Ordinal);
            if (close < 0) return false;
            address = s[1..close];
            portText = s[(close + 2)..];
        }
        else
        {
            var colon = s.LastIndexOf(':');
            if (colon >= 0)
            {
                address = s[..colon];
                portText = s[(colon + 1)..];
                if (address.Length == 0) address = null;
            }
            else portText = s;
        }
        return int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
    }
}
