using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace RDMan.Core.Ssh;

/// <summary>"[user@]host[:port]", "ssh://user@host:port", "[v6]:port" — one hop of ProxyJump or a quick connect.</summary>
public sealed record SshDestination(string? User, string Host, int? Port)
{
    public override string ToString()
    {
        var host = Host.Contains(':') ? $"[{Host}]" : Host;
        var user = string.IsNullOrEmpty(User) ? "" : User + "@";
        return Port is { } p ? $"{user}{host}:{p}" : user + host;
    }

    public static bool TryParse(string? text, out SshDestination destination)
    {
        destination = null!;
        var s = (text ?? "").Trim();
        if (s.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) s = s[6..].TrimEnd('/');
        if (s.Length == 0 || s.Any(char.IsWhiteSpace)) return false;

        string? user = null;
        var at = s.LastIndexOf('@');
        if (at >= 0)
        {
            user = s[..at];
            s = s[(at + 1)..];
            if (user.Length == 0) return false;
        }

        int? port = null;
        string host;
        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close < 0) return false;
            host = s[1..close];
            var rest = s[(close + 1)..];
            if (rest.Length > 0)
            {
                if (!rest.StartsWith(':') || !TryPort(rest[1..], out var p)) return false;
                port = p;
            }
            if (!IPAddress.TryParse(host, out var a) || a.AddressFamily != AddressFamily.InterNetworkV6) return false;
        }
        else
        {
            var colons = s.Count(c => c == ':');
            if (colons == 1)
            {
                var colon = s.IndexOf(':');
                if (!TryPort(s[(colon + 1)..], out var p)) return false;
                port = p;
                host = s[..colon];
            }
            else if (colons > 1)
            {
                // Bare IPv6 literal without a port.
                if (!IPAddress.TryParse(s, out var a) || a.AddressFamily != AddressFamily.InterNetworkV6) return false;
                host = s;
            }
            else
            {
                host = s;
            }
        }

        if (host.Length == 0 || host.IndexOfAny(['/', '\\', '@', '[', ']']) >= 0) return false;
        destination = new SshDestination(user, host, port);
        return true;
    }

    /// <summary>Comma-separated ProxyJump list. "none"/empty = no jump. Returns false on any invalid hop.</summary>
    public static bool TryParseJumpList(string? text, out IReadOnlyList<SshDestination> hops)
    {
        hops = [];
        var s = (text ?? "").Trim();
        if (s.Length == 0 || s.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        var list = new List<SshDestination>();
        foreach (var part in s.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!TryParse(part, out var d)) return false;
            list.Add(d);
        }
        hops = list;
        return true;
    }

    /// <summary>
    /// What people type into a quick-connect box: "user@host", "host:2222", "ssh user@host -p 2222",
    /// "ssh -p 2222 host". Other ssh options are rejected (false) so nothing is silently ignored.
    /// </summary>
    public static bool TryParseCommandLine(string? text, out SshDestination destination)
    {
        destination = null!;
        var parts = (text ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0 && parts[0].Equals("ssh", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
        int? port = null;
        string? user = null;
        string? target = null;
        for (var i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            if (p is "-p" or "-l")
            {
                if (i + 1 >= parts.Count) return false;
                var v = parts[++i];
                if (p == "-p")
                {
                    if (!TryPort(v, out var n)) return false;
                    port = n;
                }
                else user = v;
            }
            else if (p.StartsWith('-')) return false;
            else if (target is null) target = p;
            else return false;
        }
        if (target is null || !TryParse(target, out var d)) return false;
        destination = d with { User = d.User ?? user, Port = port ?? d.Port };
        return true;
    }

    private static bool TryPort(string text, out int port)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
}
