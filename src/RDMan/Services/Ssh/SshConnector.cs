using System.IO;
using System.Net;
using System.Net.Sockets;
using RDMan.Core.Models;
using RDMan.Core.Security;
using RDMan.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;

namespace RDMan.Services.Ssh;

/// <summary>A host key the user has to decide about (unknown, changed or revoked).</summary>
public sealed record PendingHostKey(
    string Host,
    int Port,
    string KeyType,
    byte[] Blob,
    HostKeyStatus Status,
    IReadOnlyList<string> KnownHostsFiles,
    bool Hash,
    bool OtherTypesKnown)
{
    public string Fingerprint => Core.Ssh.Fingerprint.Sha256(Blob);
    public int Bits => Core.Ssh.Fingerprint.Describe(Blob).Bits;
    public string RandomArt => Core.Ssh.Fingerprint.RandomArt(Blob, KeyType, Bits);
    public string HostLabel => KnownHosts.HostEntryName(Host, Port);
}

public sealed class SshConnectException(SshFailure failure, string detail, Exception? inner = null) : Exception(detail, inner)
{
    public SshFailure Failure { get; } = failure;
    public PendingHostKey? HostKey { get; init; }
    /// <summary>"publickey,password" from "Permission denied (…)", when the server said.</summary>
    public string? AllowedMethods { get; init; }
    /// <summary>The jump host that failed, when it wasn't the target.</summary>
    public string? Hop { get; init; }
    /// <summary>The user cancelled a prompt: not an error to show.</summary>
    public bool Cancelled { get; init; }
}

/// <summary>Everything a connection needs besides the target: secrets, prompts, decisions taken this session.</summary>
public sealed class SshConnectRequest
{
    public required SshTarget Target { get; init; }
    public required SshConfig Config { get; init; }
    public required ISecretProtector Secrets { get; init; }
    public required ISshPrompts Prompts { get; init; }
    /// <summary>Password from the node/profile or typed for this session (Password mode or a password-only server).</summary>
    public string? Password { get; set; }
    /// <summary>Passphrases typed this session, by key file path or vault key id.</summary>
    public Dictionary<string, string> Passphrases { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Host keys accepted with "Connect once" this session ("host:port|SHA256:…").</summary>
    public HashSet<string> TrustedOnce { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Filled with a description of what signed in ("key pangya_vps", "password"), for the terminal.</summary>
    public string? AuthenticatedWith { get; set; }
}

/// <summary>A connected client plus the jump hosts and tunnels it rides on. Disposing tears everything down.</summary>
public sealed class SshLink<TClient>(TClient client, IReadOnlyList<SshClient> hops) : IDisposable where TClient : BaseClient
{
    public TClient Client { get; } = client;
    public IReadOnlyList<SshClient> Hops { get; } = hops;

    public void Dispose()
    {
        SafeDispose(Client);
        // Innermost first: each hop carries the tunnel of the next one.
        for (var i = Hops.Count - 1; i >= 0; i--) SafeDispose(Hops[i]);
    }

    private static void SafeDispose(BaseClient c)
    {
        try
        {
            if (c.IsConnected) c.Disconnect();
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or SocketException or InvalidOperationException) { }
        try { c.Dispose(); }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or SocketException) { }
    }
}

/// <summary>
/// Opens SSH connections the way OpenSSH would for the same target: agent / keys / password / keyboard-interactive
/// (MFA) authentication, known_hosts verification, ProxyJump chains through local tunnels, keepalives.
/// </summary>
public static class SshConnector
{
    public static async Task<SshLink<TClient>> ConnectAsync<TClient>(SshConnectRequest request, Func<ConnectionInfo, TClient> factory, CancellationToken ct)
        where TClient : BaseClient
    {
        var target = request.Target;
        if (target.JumpSpecInvalid) throw new SshConnectException(SshFailure.JumpInvalid, "ProxyJump");
        if (string.IsNullOrWhiteSpace(target.User)) throw new SshConnectException(SshFailure.NoUser, target.HostName);

        var hops = new List<SshClient>();
        try
        {
            // Each hop is reached through a tunnel opened on the previous one: host:port is where to dial now.
            string dialHost = target.Jumps.Count == 0 ? target.HostName : "";
            int dialPort = target.Port;
            for (var i = 0; i < target.Jumps.Count; i++)
            {
                // The hop's user: "user@" in ProxyJump, else its config entry, else the same user as the target.
                var hopTarget = SshTargetResolver.ResolveHop(target.Jumps[i], request.Config);
                if (string.IsNullOrWhiteSpace(hopTarget.User)) hopTarget = hopTarget with { User = target.User };
                var (h, p) = i == 0 ? (hopTarget.HostName, hopTarget.Port) : (dialHost, dialPort);
                request.Prompts.Progress(SshProgress.Jumping, hopTarget.Display);
                SshClient hop;
                try
                {
                    hop = await ConnectOneAsync(request, hopTarget, h, p, ci => new SshClient(ci), isHop: true, ct).ConfigureAwait(false);
                }
                catch (SshConnectException ex) when (ex.HostKey is null && !ex.Cancelled)
                {
                    throw new SshConnectException(SshFailure.JumpFailed, ex.Message, ex) { Hop = hopTarget.Display };
                }
                hops.Add(hop);

                var next = i + 1 < target.Jumps.Count
                    ? SshTargetResolver.ResolveHop(target.Jumps[i + 1], request.Config)
                    : target;
                (dialHost, dialPort) = OpenTunnel(hop, next.HostName, next.Port);
            }

            var client = await ConnectOneAsync(request, target, dialHost, dialPort, factory, isHop: false, ct).ConfigureAwait(false);
            return new SshLink<TClient>(client, hops);
        }
        catch
        {
            for (var i = hops.Count - 1; i >= 0; i--)
            {
                try { hops[i].Dispose(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException) { }
            }
            throw;
        }
    }

    private static (string Host, int Port) OpenTunnel(SshClient hop, string host, int port)
    {
        var tunnel = new ForwardedPortLocal(IPAddress.Loopback.ToString(), 0, host, (uint)port);
        hop.AddForwardedPort(tunnel);
        tunnel.Start();
        var bound = (int)tunnel.BoundPort;
        if (bound == 0) throw new SshConnectException(SshFailure.JumpFailed, "tunnel");
        return (IPAddress.Loopback.ToString(), bound);
    }

    private static async Task<TClient> ConnectOneAsync<TClient>(SshConnectRequest request, SshTarget target, string dialHost, int dialPort,
        Func<ConnectionInfo, TClient> factory, bool isHop, CancellationToken ct) where TClient : BaseClient
    {
        var user = target.User!;
        var auth = await BuildAuthAsync(request, target, isHop, ct).ConfigureAwait(false);
        var ci = new ConnectionInfo(dialHost, dialPort, user, auth.Methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(target.ConnectTimeoutSeconds, 3, 120)),
            RetryAttempts = 1,
        };

        var client = factory(ci);
        PendingHostKey? pending = null;
        client.HostKeyReceived += (_, e) =>
        {
            var blob = e.HostKey;
            var type = Fingerprint.Describe(blob).Type;
            var check = KnownHostsStore.Check(target.KnownHostsFiles, target.HostName, target.Port, type, blob);
            var key = new PendingHostKey(target.HostName, target.Port, type, blob, check.Status, target.KnownHostsFiles, target.HashKnownHosts, check.OtherTypesKnown);
            switch (check.Status)
            {
                case HostKeyStatus.Trusted:
                    e.CanTrust = true;
                    break;
                case HostKeyStatus.Unknown when request.TrustedOnce.Contains($"{key.HostLabel}|{key.Fingerprint}"):
                    e.CanTrust = true;
                    break;
                case HostKeyStatus.Unknown when target.StrictHostKeyChecking is "no" or "off" or "accept-new":
                    // What OpenSSH does with these settings: accept a never-seen host, and remember it.
                    TrySave(key, replace: false);
                    e.CanTrust = true;
                    break;
                default:
                    pending = key;
                    e.CanTrust = false;
                    break;
            }
            if (e.CanTrust) request.Prompts.Progress(SshProgress.HostKeyTrusted, $"{Fingerprint.ShortType(type)} {key.Fingerprint}");
        };

        request.Prompts.Progress(SshProgress.Connecting, isHop ? target.Display : $"{target.HostName}:{target.Port}");
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            client.Dispose();
            if (auth.Cancelled) throw new SshConnectException(SshFailure.AuthFailed, "", ex) { Cancelled = true };
            if (pending is not null)
            {
                var failure = pending.Status switch
                {
                    HostKeyStatus.Changed => SshFailure.HostKeyChanged,
                    HostKeyStatus.Revoked => SshFailure.HostKeyRevoked,
                    _ => SshFailure.HostKeyRejected,
                };
                throw new SshConnectException(failure, ex.Message, ex) { HostKey = pending };
            }
            throw Classify(ex, auth.Tried);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw;
        }

        if (!isHop) request.AuthenticatedWith = auth.Describe();
        request.Prompts.Progress(SshProgress.Authenticated, user);
        return client;
    }

    private static void TrySave(PendingHostKey key, bool replace)
    {
        try { KnownHostsStore.Save(key.KnownHostsFiles, key.Host, key.Port, key.KeyType, key.Blob, key.Hash, replace); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Log(ex); }
    }

    /// <summary>Remembers a host key the user accepted (or replaces a changed one).</summary>
    public static void Trust(PendingHostKey key, bool replace) => KnownHostsStore.Save(key.KnownHostsFiles, key.Host, key.Port, key.KeyType, key.Blob, key.Hash, replace);

    // ───────────── Authentication ─────────────

    private sealed class AuthPlan
    {
        public List<AuthenticationMethod> Methods { get; } = new();
        public List<string> Tried { get; } = new();
        public string? KeyLabel { get; set; }
        public bool HasPassword { get; set; }
        public bool Cancelled { get; set; }
        public string Describe() => KeyLabel ?? (HasPassword ? "password" : "keyboard-interactive");
    }

    private static async Task<AuthPlan> BuildAuthAsync(SshConnectRequest request, SshTarget target, bool isHop, CancellationToken ct)
    {
        var plan = new AuthPlan();
        var user = target.User!;
        var sources = new List<IPrivateKeySource>();
        var mode = target.Auth.Mode;
        request.Prompts.Progress(SshProgress.Authenticating, user);

        if (mode == SshAuthMode.Agent || mode == SshAuthMode.Auto && !target.IdentitiesOnly)
        {
            var agent = await AgentKeySource.TryOpenAsync(ct).ConfigureAwait(false);
            if (agent is not null)
            {
                sources.Add(agent);
                plan.Tried.Add("agent");
                plan.KeyLabel ??= "agent";
            }
            else if (mode == SshAuthMode.Agent)
            {
                throw new SshConnectException(SshFailure.AgentUnavailable, "agent");
            }
        }

        if (mode == SshAuthMode.Key && !isHop && target.Auth.Key is { } entry)
        {
            var id = entry.Id.ToString();
            request.Passphrases.TryGetValue(id, out var typed);
            var result = SshKeyVault.TryLoad(entry, request.Secrets, typed, out var key);
            for (var attempt = 0; result is KeyLoadResult.NeedsPassphrase or KeyLoadResult.WrongPassphrase && attempt < 3; attempt++)
            {
                var pass = await request.Prompts.AskPassphraseAsync(entry.Name, ct).ConfigureAwait(false);
                if (pass is null) throw new SshConnectException(SshFailure.KeyPassphrase, entry.Name) { Cancelled = true };
                result = SshKeyVault.TryLoad(entry, request.Secrets, pass, out key);
                if (result == KeyLoadResult.Ok) request.Passphrases[id] = pass;
            }
            switch (result)
            {
                case KeyLoadResult.Ok when key is not null:
                    sources.Add(key);
                    plan.Tried.Add(entry.Name);
                    plan.KeyLabel = entry.Name;
                    break;
                case KeyLoadResult.WrongPassphrase or KeyLoadResult.NeedsPassphrase:
                    throw new SshConnectException(SshFailure.KeyPassphrase, entry.Name);
                default:
                    throw new SshConnectException(SshFailure.KeyUnreadable, entry.Name);
            }
        }

        if (mode == SshAuthMode.Auto || isHop)
        {
            foreach (var path in target.IdentityFiles)
            {
                if (!File.Exists(path)) continue;
                request.Passphrases.TryGetValue(path, out var typed);
                var result = SshKeyVault.TryLoadFile(path, typed, out var key);
                if (result is KeyLoadResult.NeedsPassphrase or KeyLoadResult.WrongPassphrase)
                {
                    var pass = await request.Prompts.AskPassphraseAsync(Path.GetFileName(path), ct).ConfigureAwait(false);
                    if (pass is null) continue; // skip this key, like ssh does when you press Enter
                    result = SshKeyVault.TryLoadFile(path, pass, out key);
                    if (result == KeyLoadResult.Ok) request.Passphrases[path] = pass;
                }
                if (result == KeyLoadResult.Ok && key is not null)
                {
                    sources.Add(key);
                    plan.Tried.Add(Path.GetFileName(path));
                    plan.KeyLabel ??= Path.GetFileName(path);
                }
            }
        }

        if (sources.Count > 0) plan.Methods.Add(new PrivateKeyAuthenticationMethod(user, sources.ToArray()));

        var password = isHop ? null : request.Password;
        if (!string.IsNullOrEmpty(password))
        {
            plan.Methods.Add(new PasswordAuthenticationMethod(user, password));
            plan.Tried.Add("password");
            plan.HasPassword = true;
        }

        // Last: the server asks its own questions (password on servers that only allow this, one-time codes, MFA).
        var kbd = new KeyboardInteractiveAuthenticationMethod(user);
        kbd.AuthenticationPrompt += (_, e) =>
        {
            if (plan.Cancelled) return;
            var prompts = e.Prompts;
            if (prompts.Count == 0) return;
            if (!string.IsNullOrEmpty(password) && prompts.Count == 1 && !prompts[0].IsEchoed
                && prompts[0].Request.Contains("assword", StringComparison.OrdinalIgnoreCase))
            {
                prompts[0].Response = password;
                return;
            }
            var fields = prompts.Select(p => new SshPromptField(p.Request.Trim(), p.IsEchoed)).ToList();
            // SSH.NET raises this synchronously on its own thread; the UI answers asynchronously.
            var answers = request.Prompts.AskKeyboardInteractiveAsync(e.Username, e.Instruction, fields, ct).GetAwaiter().GetResult();
            if (answers is null)
            {
                plan.Cancelled = true;
                foreach (var p in prompts) p.Response = "";
                return;
            }
            for (var i = 0; i < prompts.Count && i < answers.Length; i++) prompts[i].Response = answers[i];
        };
        plan.Methods.Add(kbd);
        plan.Tried.Add("keyboard-interactive");
        return plan;
    }

    // ───────────── Errors ─────────────

    public static SshConnectException Classify(Exception ex, IReadOnlyList<string>? tried = null)
    {
        if (ex is SshConnectException sce) return sce;
        var inner = ex;
        while (inner is AggregateException { InnerException: { } i }) inner = i;

        switch (inner)
        {
            case SocketException se:
                return new SshConnectException(se.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => SshFailure.HostNotFound,
                    SocketError.ConnectionRefused => SshFailure.Refused,
                    SocketError.TimedOut => SshFailure.Timeout,
                    SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown => SshFailure.Unreachable,
                    SocketError.ConnectionReset or SocketError.ConnectionAborted => SshFailure.ConnectionLost,
                    _ => SshFailure.Unknown,
                }, se.Message, se);
            case SshOperationTimeoutException:
                return new SshConnectException(SshFailure.Timeout, inner.Message, inner);
            case SshAuthenticationException:
                var allowed = ExtractMethods(inner.Message);
                return new SshConnectException(SshFailure.AuthFailed, string.Join(", ", tried ?? []), inner) { AllowedMethods = allowed };
            case SshPassPhraseNullOrEmptyException:
                return new SshConnectException(SshFailure.KeyPassphrase, inner.Message, inner);
            case SshConnectionException { DisconnectReason: DisconnectReason.KeyExchangeFailed or DisconnectReason.ProtocolError or DisconnectReason.ProtocolVersionNotSupported } c:
                return new SshConnectException(SshFailure.Protocol, c.Message, c);
            case SshConnectionException c:
                return new SshConnectException(SshFailure.ConnectionLost, c.Message, c);
            case ProxyException:
                return new SshConnectException(SshFailure.JumpFailed, inner.Message, inner);
            case IOException:
                return new SshConnectException(SshFailure.ConnectionLost, inner.Message, inner);
            default:
                return new SshConnectException(SshFailure.Unknown, inner.Message, inner);
        }
    }

    private static string? ExtractMethods(string message)
    {
        var open = message.IndexOf('(');
        var close = message.LastIndexOf(')');
        return open >= 0 && close > open ? message[(open + 1)..close] : null;
    }
}
