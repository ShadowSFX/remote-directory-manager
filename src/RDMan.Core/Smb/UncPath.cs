using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace RDMan.Core.Smb;

/// <summary>A parsed SMB location: host, optional share, optional sub-path.</summary>
public sealed record SmbTarget(string Host, string? Share, string? SubPath)
{
    /// <summary>Host as it must appear in a UNC path (IPv6 literals use the ipv6-literal.net form).</summary>
    public string UncHost => UncPath.ToUncHost(Host);

    public string ServerRoot => @"\\" + UncHost;

    public string? ShareRoot => Share is null ? null : $@"{ServerRoot}\{Share}";

    public string FullPath
    {
        get
        {
            if (Share is null) return ServerRoot;
            return string.IsNullOrEmpty(SubPath) ? ShareRoot! : $@"{ShareRoot}\{SubPath}";
        }
    }
}

public static partial class UncPath
{
    // Characters Windows forbids in share/file names.
    private static readonly char[] InvalidNameChars = ['"', '<', '>', '|', ':', '*', '?', '/', '\\'];

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9\-_.]{0,252}[A-Za-z0-9_])?$")]
    private static partial Regex HostNameRegex();

    public enum ParseError { None, Empty, PortNotSupported, InvalidHost, InvalidShare, InvalidPath }

    /// <summary>
    /// Accepts what people actually paste: <c>10.0.0.5</c>, <c>10.0.0.5\share</c>, <c>\\10.0.0.5\share\sub</c>,
    /// <c>//host/share</c>, <c>smb://host/share/sub</c>, <c>[fe80::1]</c>, <c>fe80::1</c>.
    /// </summary>
    public static bool TryParse(string? input, out SmbTarget? target, out ParseError error)
    {
        target = null;
        error = ParseError.None;
        var s = (input ?? "").Trim().Trim('"');
        if (s.Length == 0) { error = ParseError.Empty; return false; }

        if (s.StartsWith("smb://", StringComparison.OrdinalIgnoreCase)) s = s[6..];
        else if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) s = s[7..];
        s = s.Replace('/', '\\').TrimStart('\\').TrimEnd('\\');
        if (s.Length == 0) { error = ParseError.Empty; return false; }

        string hostPart;
        string rest;
        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close < 0) { error = ParseError.InvalidHost; return false; }
            hostPart = s[1..close];
            rest = s[(close + 1)..];
            if (rest.StartsWith(':')) { error = ParseError.PortNotSupported; return false; }
            rest = rest.TrimStart('\\');
        }
        else
        {
            var slash = s.IndexOf('\\');
            hostPart = slash < 0 ? s : s[..slash];
            rest = slash < 0 ? "" : s[(slash + 1)..];
        }

        if (!TryNormalizeHost(hostPart, out var host, out error)) return false;

        string? share = null;
        string? sub = null;
        if (rest.Length > 0)
        {
            var parts = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                share = parts[0];
                if (!IsValidName(share)) { error = ParseError.InvalidShare; return false; }
                if (parts.Length > 1)
                {
                    var subParts = parts[1..];
                    if (subParts.Any(p => !IsValidName(p) || p is "." or "..")) { error = ParseError.InvalidPath; return false; }
                    sub = string.Join('\\', subParts);
                }
            }
        }

        target = new SmbTarget(host, share, sub);
        return true;
    }

    private static bool TryNormalizeHost(string raw, out string host, out ParseError error)
    {
        host = raw.Trim();
        error = ParseError.None;
        if (host.Length == 0) { error = ParseError.InvalidHost; return false; }

        // ipv6-literal.net form pasted back in → convert to a real IPv6 literal.
        if (host.EndsWith(".ipv6-literal.net", StringComparison.OrdinalIgnoreCase))
        {
            var lit = host[..^".ipv6-literal.net".Length].Replace('-', ':').Replace('s', '%');
            if (IPAddress.TryParse(lit, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6)
            {
                host = v6.ToString();
                return true;
            }
            error = ParseError.InvalidHost;
            return false;
        }

        if (host.Contains(':'))
        {
            if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                host = ip.ToString();
                return true;
            }
            // "host:445" / "1.2.3.4:139"
            var colon = host.LastIndexOf(':');
            if (colon > 0 && int.TryParse(host[(colon + 1)..], out _)) { error = ParseError.PortNotSupported; return false; }
            error = ParseError.InvalidHost;
            return false;
        }

        if (IPAddress.TryParse(host, out var v4) && v4.AddressFamily == AddressFamily.InterNetwork && host.Count(c => c == '.') == 3)
        {
            host = v4.ToString();
            return true;
        }

        // Host names keep the user's casing for display; all-digit dotted strings that failed IPv4 parsing are rejected.
        if (HostNameRegex().IsMatch(host) && !host.Contains("..") && !host.All(c => char.IsDigit(c) || c == '.'))
            return true;

        error = ParseError.InvalidHost;
        return false;
    }

    public static bool IsValidHost(string? host) => TryNormalizeHost(host ?? "", out _, out _);

    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.IndexOfAny(InvalidNameChars) >= 0) return false;
        return !name.Any(char.IsControl);
    }

    public static string ToUncHost(string host)
    {
        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.ToString().Replace(':', '-').Replace('%', 's') + ".ipv6-literal.net";
        return host;
    }

    /// <summary>Splits a UNC path into server root (\\host), share and sub-path. Returns false for non-UNC paths.</summary>
    public static bool TrySplit(string? path, out SmbTarget? target)
    {
        target = null;
        if (string.IsNullOrEmpty(path) || !path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
        return TryParse(path, out target, out _);
    }

    /// <summary>True when two hosts refer to the same server name (case-insensitive, IPv6-normalized).</summary>
    public static bool SameHost(string a, string b)
    {
        if (IPAddress.TryParse(a, out var ia) && IPAddress.TryParse(b, out var ib)) return ia.Equals(ib);
        return string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
    }
}
