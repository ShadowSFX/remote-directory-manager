using System.IO;
using RDMan.Core.Models;
using RDMan.Core.Ssh;

namespace RDMan.Services.Ssh;

/// <summary>
/// The user's OpenSSH setup as RDMan sees it: ~/.ssh/config (re-read when it changes on disk, so editing it in
/// another program takes effect on the next connect) and target resolution for servers.
/// </summary>
public sealed class SshEnvironment(AppState state)
{
    private static SshConfig? _config;
    private static DateTime _configStamp;
    private static DateTime _checkedAt;
    private static readonly object Gate = new();

    public static string Home { get; } = SshConfig.DefaultHome;

    public static string ConfigPath => SshConfig.DefaultPath(Home);

    /// <summary>The parsed config (shared by every session and the health monitor; re-read when the file changes).</summary>
    public static SshConfig SharedConfig
    {
        get
        {
            lock (Gate)
            {
                // Checking the timestamp is cheap, but the health monitor asks often: once a second is enough.
                if (_config is not null && DateTime.UtcNow - _checkedAt < TimeSpan.FromSeconds(1)) return _config;
                _checkedAt = DateTime.UtcNow;
                var stamp = Stamp(ConfigPath);
                if (_config is null || stamp != _configStamp)
                {
                    _config = SshConfig.Load(ConfigPath, Home);
                    _configStamp = stamp;
                }
                return _config;
            }
        }
    }

    public SshConfig Config => SharedConfig;

    private static DateTime Stamp(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch (IOException) { return DateTime.MinValue; }
    }

    /// <summary>Host and port the health monitor should probe for a server's terminal (aliases resolved).</summary>
    /// <param name="proxyJump">The ProxyJump set in RDMan for this server or inherited from its groups (null = none set).</param>
    public static (string Host, int Port)? ProbeEndpoint(ServerNode server, string? proxyJump = null)
    {
        if (server.Terminal is not { Enabled: true } endpoint || string.IsNullOrWhiteSpace(server.Host)) return null;
        var host = server.Host.Trim();
        try
        {
            var cfg = endpoint.UseSshConfig ? SharedConfig.Resolve(host) : null;
            // Behind a jump host the target usually isn't reachable directly: probing it would always say "offline".
            // RDMan's own setting (even "none") wins over the config, like it does when connecting.
            var jump = string.IsNullOrWhiteSpace(proxyJump) ? cfg?.ProxyJump : proxyJump;
            if (IsJump(jump)) return null;
            return (cfg?.EffectiveHost ?? host, endpoint.Port ?? cfg?.Port ?? SshEndpoint.DefaultPort);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (host, endpoint.Port ?? SshEndpoint.DefaultPort);
        }
    }

    private static bool IsJump(string? spec) => !string.IsNullOrWhiteSpace(spec) && !spec.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);

    public EffectiveSshAuth ResolveAuth(TreeNode node)
        => SshAuthResolver.Resolve(node, state.Index, state.Document.Profiles, state.Document.SshKeys);

    public EffectiveSshSettings ResolveSettings(TreeNode node) => SshSettingsResolver.Resolve(node, state.Index);

    public SshTarget Resolve(ServerNode server)
        => SshTargetResolver.Resolve(server, ResolveAuth(server), ResolveSettings(server), Config);
}
