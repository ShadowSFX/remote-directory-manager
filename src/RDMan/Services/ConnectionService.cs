using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using RDMan.Core.Credentials;
using RDMan.Core.Security;
using RDMan.Core.Smb;
using RDMan.Interop;

namespace RDMan.Services;

public sealed class SmbException : Exception
{
    public SmbException(int code, string? message = null) : base(message ?? SafeMessage(code)) => Code = code;

    public int Code { get; }

    private static string SafeMessage(int code)
        => code == SmbErrorTranslator.PortUnreachable ? "TCP 445 timeout" : new Win32Exception(code).Message;
}

public sealed record ShareInfo(string Name, string? Remark, bool IsHidden);

public sealed record WindowsConnection(string RemoteName, string? LocalName, bool Remembered);

/// <summary>
/// Opens SMB sessions with the credential a node resolves to (WNetAddConnection2, no drive letter),
/// probes reachability first so a dead VPS fails in ~2 s instead of freezing ~20 s, and enumerates shares.
/// </summary>
public sealed class ConnectionService
{
    private readonly ISecretProtector _secrets;
    // Hosts for which RDMan itself opened an IPC$/share session (closed on exit if the user wants).
    private readonly ConcurrentDictionary<string, string> _opened = new(StringComparer.OrdinalIgnoreCase);
    // Last credential successfully used per host, to avoid redundant WNet calls on every navigation.
    private readonly ConcurrentDictionary<string, string> _established = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public ConnectionService(ISecretProtector secrets) => _secrets = secrets;

    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2.5);

    public const int SmbPort = 445;

    /// <summary>TCP connect to port 445 (SMB). Returns latency in ms, or null when unreachable.</summary>
    public static Task<int?> ProbeAsync(string host, TimeSpan timeout, CancellationToken ct) => ProbeAsync(host, SmbPort, timeout, ct);

    /// <summary>TCP connect to <paramref name="port"/>. Returns latency in ms, or null when unreachable.</summary>
    public static async Task<int?> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            // Resolve first and time only the TCP connect: name resolution (NetBIOS/LLMNR on a LAN can take
            // hundreds of ms) isn't the server's latency.
            var addresses = System.Net.IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await System.Net.Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
            if (addresses.Length == 0) return null;
            // Dual-mode socket: IPv4 and IPv6 addresses alike; every resolved address is tried in order.
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            var sw = Stopwatch.StartNew();
            await socket.ConnectAsync(addresses, port, cts.Token).ConfigureAwait(false);
            return (int)Math.Max(1, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static string CredentialKey(EffectiveCredential c) => $"{c.Kind}|{c.Username}|{c.ProtectedPassword}";

    /// <summary>
    /// Ensures an authenticated session exists with <paramref name="host"/> for the given credential.
    /// Throws <see cref="SmbException"/> with the Win32 code on failure.
    /// </summary>
    public async Task EnsureSessionAsync(string host, string? share, EffectiveCredential credential, CancellationToken ct)
    {
        var uncHost = UncPath.ToUncHost(host);
        var gate = _locks.GetOrAdd(uncHost, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = CredentialKey(credential);
            if (_established.TryGetValue(uncHost, out var existing) && existing == key) return;

            // The credential for this host changed (edited server, "Test connection" with other values…):
            // drop the sessions RDMan opened with the previous one, or Windows keeps using it / returns 1219.
            foreach (var stale in _opened.Where(kv => string.Equals(kv.Value, uncHost, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
            {
                await Task.Run(() => NativeMethods.WNetCancelConnection2(stale, 0, true), ct).ConfigureAwait(false);
                _opened.TryRemove(stale, out _);
            }

            if (await ProbeAsync(host, ProbeTimeout, ct).ConfigureAwait(false) is null)
                throw new SmbException(SmbErrorTranslator.PortUnreachable);

            if (credential.Kind == EffectiveCredentialKind.CurrentUser)
            {
                // Nothing to establish: Windows uses the logged-on user / Credential Manager on first access.
                _established[uncHost] = key;
                return;
            }

            var password = credential.Kind == EffectiveCredentialKind.Guest ? "" : _secrets.TryUnprotect(credential.ProtectedPassword) ?? "";
            var remote = share is null ? $@"\\{uncHost}\IPC$" : $@"\\{uncHost}\{share}";
            var result = await Task.Run(() => AddConnection(remote, credential.Username, password), ct).ConfigureAwait(false);

            if (result == SmbErrorTranslator.ErrorSessionCredentialConflict)
            {
                // A session with *these same* credentials may already exist (e.g. we connected IPC$ before) — that's fine.
                // Otherwise surface the conflict so the user can choose to close the other session.
                throw new SmbException(result);
            }
            if (result != NativeMethods.NO_ERROR)
                throw new SmbException(result);

            _opened[remote] = uncHost;
            _established[uncHost] = key;
        }
        finally
        {
            gate.Release();
        }
    }

    private static int AddConnection(string remote, string? user, string password)
    {
        var nr = new NativeMethods.NETRESOURCE
        {
            dwType = remote.EndsWith(@"\IPC$", StringComparison.OrdinalIgnoreCase) ? NativeMethods.RESOURCETYPE_ANY : NativeMethods.RESOURCETYPE_DISK,
            lpRemoteName = remote,
        };
        var r = NativeMethods.WNetAddConnection2(ref nr, password, user, NativeMethods.CONNECT_TEMPORARY);
        // 1219 can also mean "this exact remote is already connected with other creds"; IPC$ case handled by caller.
        return r;
    }

    /// <summary>Forgets cached state so the next access re-authenticates (after credentials were edited).</summary>
    public void Invalidate(string host) => _established.TryRemove(UncPath.ToUncHost(host), out _);

    /// <summary>
    /// Closes every connection Windows has to <paramref name="host"/> (ours and Explorer's "net use" ones).
    /// Returns the number of connections closed.
    /// </summary>
    public async Task<int> DisconnectHostAsync(string host)
    {
        var uncHost = UncPath.ToUncHost(host);
        _established.TryRemove(uncHost, out _);
        return await Task.Run(() =>
        {
            var closed = 0;
            var prefix = $@"\\{uncHost}\";
            var targets = EnumerateWindowsConnections(remembered: false)
                .Where(c => c.RemoteName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || string.Equals(c.RemoteName, $@"\\{uncHost}", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.LocalName ?? c.RemoteName)
                .Concat(_opened.Where(kv => string.Equals(kv.Value, uncHost, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key))
                .Append($@"\\{uncHost}\IPC$")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var t in targets)
            {
                if (NativeMethods.WNetCancelConnection2(t, 0, true) == NativeMethods.NO_ERROR) closed++;
                _opened.TryRemove(t, out _);
            }
            return closed;
        }).ConfigureAwait(false);
    }

    public bool HasOpenSession(string host)
        => _established.ContainsKey(UncPath.ToUncHost(host));

    public void DisconnectAllOpenedByUs()
    {
        foreach (var remote in _opened.Keys.ToList())
        {
            NativeMethods.WNetCancelConnection2(remote, 0, true);
            _opened.TryRemove(remote, out _);
        }
        _established.Clear();
    }

    public Task<IReadOnlyList<ShareInfo>> ListSharesAsync(string host, bool includeHidden, CancellationToken ct)
        => Task.Run<IReadOnlyList<ShareInfo>>(() =>
        {
            var server = @"\\" + UncPath.ToUncHost(host);
            var resume = 0;
            var rc = NativeMethods.NetShareEnum(server, 1, out var buffer, -1, out var read, out _, ref resume);
            try
            {
                if (rc != NativeMethods.NO_ERROR && rc != NativeMethods.ERROR_MORE_DATA) throw new SmbException(rc);
                var list = new List<ShareInfo>(read);
                var size = Marshal.SizeOf<NativeMethods.SHARE_INFO_1>();
                for (var i = 0; i < read; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var info = Marshal.PtrToStructure<NativeMethods.SHARE_INFO_1>(buffer + i * size);
                    if ((info.shi1_type & NativeMethods.STYPE_MASK) != NativeMethods.STYPE_DISKTREE) continue; // printers, IPC, devices
                    var hidden = info.shi1_netname.EndsWith('$') || (info.shi1_type & NativeMethods.STYPE_SPECIAL) != 0;
                    if (hidden && !includeHidden) continue;
                    list.Add(new ShareInfo(info.shi1_netname, string.IsNullOrWhiteSpace(info.shi1_remark) ? null : info.shi1_remark, hidden));
                }
                return list.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            finally
            {
                if (buffer != IntPtr.Zero) NativeMethods.NetApiBufferFree(buffer);
            }
        }, ct);

    /// <summary>Connections Windows knows about: currently connected, or remembered ("net use /persistent").</summary>
    public static IReadOnlyList<WindowsConnection> EnumerateWindowsConnections(bool remembered)
    {
        var result = new List<WindowsConnection>();
        foreach (var scope in remembered ? new[] { NativeMethods.RESOURCE_CONNECTED, NativeMethods.RESOURCE_REMEMBERED } : new[] { NativeMethods.RESOURCE_CONNECTED })
        {
            if (NativeMethods.WNetOpenEnum(scope, NativeMethods.RESOURCETYPE_ANY, 0, IntPtr.Zero, out var handle) != NativeMethods.NO_ERROR) continue;
            var bufferSize = 16 * 1024;
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                while (true)
                {
                    var count = -1;
                    var size = bufferSize;
                    var rc = NativeMethods.WNetEnumResource(handle, ref count, buffer, ref size);
                    if (rc == NativeMethods.ERROR_MORE_DATA && size > bufferSize)
                    {
                        Marshal.FreeHGlobal(buffer);
                        bufferSize = size;
                        buffer = Marshal.AllocHGlobal(bufferSize);
                        continue;
                    }
                    if (rc != NativeMethods.NO_ERROR) break;
                    var stride = Marshal.SizeOf<NativeMethods.NETRESOURCE>();
                    for (var i = 0; i < count; i++)
                    {
                        var nr = Marshal.PtrToStructure<NativeMethods.NETRESOURCE>(buffer + i * stride);
                        if (string.IsNullOrEmpty(nr.lpRemoteName) || !nr.lpRemoteName.StartsWith(@"\\", StringComparison.Ordinal)) continue;
                        result.Add(new WindowsConnection(nr.lpRemoteName, string.IsNullOrEmpty(nr.lpLocalName) ? null : nr.lpLocalName, scope == NativeMethods.RESOURCE_REMEMBERED));
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                NativeMethods.WNetCloseEnum(handle);
            }
        }
        return result;
    }
}
