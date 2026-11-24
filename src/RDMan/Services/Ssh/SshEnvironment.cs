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
    private SshConfig? _config;
    private DateTime _configStamp;
    private readonly object _gate = new();

    public string Home { get; } = SshConfig.DefaultHome;

    public string ConfigPath => SshConfig.DefaultPath(Home);

    public SshConfig Config
    {
        get
        {
            lock (_gate)
            {
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

    private static DateTime Stamp(string path)
    {
        try
        {
            // Includes can change too; the main file's time is a good enough signal plus a short cache.
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch (IOException) { return DateTime.MinValue; }
    }

    public EffectiveSshAuth ResolveAuth(TreeNode node)
        => SshAuthResolver.Resolve(node, state.Index, state.Document.Profiles, state.Document.SshKeys);

    public EffectiveSshSettings ResolveSettings(TreeNode node) => SshSettingsResolver.Resolve(node, state.Index);

    public SshTarget Resolve(ServerNode server)
        => SshTargetResolver.Resolve(server, ResolveAuth(server), ResolveSettings(server), Config);
}
