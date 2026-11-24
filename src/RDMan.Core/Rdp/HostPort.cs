using System.Globalization;
using System.Net;
using System.Net.Sockets;
using RDMan.Core.Smb;

namespace RDMan.Core.Rdp;

/// <summary>Parses what people paste for a remote desktop target: "host", "host:3390", "10.0.0.5:3390", "[fe80::1]:3390".</summary>
public static class HostPort
{
    public static bool TryParse(string? input, out string host, out int? port)
    {
        host = "";
        port = null;
        var s = (input ?? "").Trim();
        if (s.StartsWith("rdp://", StringComparison.OrdinalIgnoreCase)) s = s[6..];
        s = s.TrimEnd('/');
        if (s.Length == 0) return false;

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
            return IPAddress.TryParse(host, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6;
        }

        var colon = s.LastIndexOf(':');
        if (colon > 0 && s.IndexOf(':') == colon)
        {
            if (!TryPort(s[(colon + 1)..], out var p)) return false;
            port = p;
            s = s[..colon];
        }
        if (!UncPath.IsValidHost(s)) return false;
        host = UncPath.TryParse(s, out var t, out _) && t is not null ? t.Host : s;
        return true;
    }

    public static bool IsValidPort(int port) => port is > 0 and <= 65535;

    private static bool TryPort(string text, out int port)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && IsValidPort(port);
}
