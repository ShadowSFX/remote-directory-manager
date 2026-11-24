using System.IO;
using System.IO.Pipes;
using RDMan.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace RDMan.Services.Ssh;

/// <summary>
/// Keys held by an SSH agent (Windows OpenSSH agent service, or Pageant ≥ 0.75 through its named pipe), exposed to
/// SSH.NET as an <see cref="IPrivateKeySource"/>: SSH.NET asks each algorithm to sign and the agent does it, so the
/// private key never leaves the agent.
/// </summary>
public sealed class AgentKeySource : IPrivateKeySource
{
    private readonly Func<Stream> _connect;
    private readonly List<HostAlgorithm> _algorithms = new();

    private AgentKeySource(Func<Stream> connect, IReadOnlyList<AgentIdentity> identities)
    {
        _connect = connect;
        Identities = identities;
        foreach (var id in identities)
        {
            if (id.KeyType == "ssh-rsa")
            {
                // Modern servers refuse SHA-1 RSA signatures; offer the SHA-2 variants first.
                _algorithms.Add(new AgentAlgorithm(this, "rsa-sha2-512", id.PublicKeyBlob, AgentSignFlags.RsaSha2_512));
                _algorithms.Add(new AgentAlgorithm(this, "rsa-sha2-256", id.PublicKeyBlob, AgentSignFlags.RsaSha2_256));
                _algorithms.Add(new AgentAlgorithm(this, "ssh-rsa", id.PublicKeyBlob, AgentSignFlags.None));
            }
            else
            {
                _algorithms.Add(new AgentAlgorithm(this, id.KeyType, id.PublicKeyBlob, AgentSignFlags.None));
            }
        }
    }

    public IReadOnlyList<AgentIdentity> Identities { get; }

    public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms => _algorithms;

    /// <summary>Connects to whichever agent is running; null when none answers or it holds no keys.</summary>
    public static async Task<AgentKeySource?> TryOpenAsync(CancellationToken ct)
    {
        foreach (var pipe in CandidatePipes())
        {
            Func<Stream> connect = () =>
            {
                var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                client.Connect(1500);
                return client;
            };
            try
            {
                await using var stream = await Task.Run(connect, ct).ConfigureAwait(false);
                var ids = await new AgentClient(stream).RequestIdentitiesAsync(ct).ConfigureAwait(false);
                if (ids.Count > 0) return new AgentKeySource(connect, ids);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or AgentException or UnauthorizedAccessException)
            {
                // Not running or refused: try the next agent.
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidatePipes()
    {
        yield return "openssh-ssh-agent";
        // Pageant names its pipe "pageant.<user>.<sha256 of a per-user secret>"; find it instead of recomputing.
        string[] pipes;
        try { pipes = Directory.GetFiles(@"\\.\pipe\"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        var prefix = $"pageant.{Environment.UserName}.";
        foreach (var p in pipes)
        {
            var name = Path.GetFileName(p);
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) yield return name;
        }
    }

    private byte[] Sign(byte[] blob, byte[] data, AgentSignFlags flags)
    {
        // SSH.NET calls Sign synchronously on its own thread; one short-lived pipe connection per signature.
        using var stream = _connect();
        return new AgentClient(stream).SignAsync(blob, data, flags).GetAwaiter().GetResult();
    }

    private sealed class AgentAlgorithm(AgentKeySource owner, string name, byte[] blob, AgentSignFlags flags) : HostAlgorithm(name)
    {
        public override byte[] Data => blob;

        public override byte[] Sign(byte[] data) => owner.Sign(blob, data, flags);

        public override bool VerifySignature(byte[] data, byte[] signature) => throw new NotSupportedException();
    }
}
