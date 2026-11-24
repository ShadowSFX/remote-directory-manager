using RDMan.Services.Ssh;

namespace RDMan.ViewModels;

/// <summary>SSH terminal: shared environment (ssh config, target resolution) and terminal-wide actions.</summary>
public sealed partial class MainViewModel
{
    private SshEnvironment? _ssh;

    /// <summary>The user's OpenSSH setup (~/.ssh/config, known_hosts) as RDMan sees it.</summary>
    public SshEnvironment Ssh => _ssh ??= new SshEnvironment(State);
}
